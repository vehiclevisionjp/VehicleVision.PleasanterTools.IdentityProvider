using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.AspNetCore.WebUtilities;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>IdP のメタデータから読んだ、SP から見た IdP の情報。</summary>
internal sealed record IdpMetadata(string EntityId, Uri SsoUrl, Uri SloUrl, IReadOnlyList<X509Certificate2> SigningCertificates, string Xml);

/// <summary>テスト用の SP（ITfoxtec の SP 側の道具で、本物の SP と同じ手順を踏む）。</summary>
/// <remarks>
/// IdP の実装とは別の経路（ライブラリの SP 側）で要求を作り、応答を検証することで、
/// **自分で作って自分で読むだけの確認にならない**ようにしている。
/// </remarks>
internal sealed class SamlSp : IDisposable
{
    private SamlSp(string entityId, string acsUrl, string sloUrl, X509Certificate2 certificate)
    {
        EntityId = entityId;
        AcsUrl = acsUrl;
        SloUrl = sloUrl;
        Certificate = certificate;
    }

    public string EntityId { get; }

    public string AcsUrl { get; }

    public string SloUrl { get; }

    /// <summary>SP の署名用（秘密鍵付き）。</summary>
    public X509Certificate2 Certificate { get; }

    public string CertificatePem => Certificate.ExportCertificatePem();

    public static SamlSp Create(string? suffix = null)
    {
        suffix ??= Guid.NewGuid().ToString("N")[..8];
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test-sp-" + suffix, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // 秘密鍵付きのまま使えるよう、PFX 経由で読み直す（SignedXml が鍵を取り出せる形にする）
        var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        return new SamlSp(
            $"https://sp-{suffix}.example.com/metadata",
            $"https://sp-{suffix}.example.com/acs",
            $"https://sp-{suffix}.example.com/slo",
            certificate);
    }

    /// <summary>管理 API で登録する。</summary>
    public async Task<JsonNode> RegisterAsync(
        Browser admin,
        bool includeCertificate = true,
        bool requireSigned = false,
        string nameIdFormat = "emailAddress",
        string nameIdSource = "email",
        int[]? tenants = null,
        bool enabled = true,
        bool signAssertion = true,
        bool signResponse = true)
    {
        using var response = await E2E.AdminPostAsync(admin, "/api/admin/saml-providers", new
        {
            entityId = EntityId,
            displayName = "テスト SP",
            acsUrl = AcsUrl,
            sloUrl = SloUrl,
            signingCertificate = includeCertificate ? CertificatePem : null,
            requireSignedRequests = requireSigned,
            nameIdFormat,
            nameIdSource,
            attributeMappings = new[]
            {
                new { attributeName = "email", source = "email" },
                new { attributeName = "displayName", source = "name" },
                new { attributeName = "groups", source = "groups" },
                new { attributeName = "department", source = "dept" },
            },
            signAssertion,
            signResponse,
            allowedTenantIds = tenants ?? [],
            enabled,
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonNode.Parse(text)!;
    }

    public static async Task<IdpMetadata> ReadMetadataAsync(Browser browser)
    {
        using var response = await browser.GetAsync("/saml/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = await response.Content.ReadAsStringAsync();
        var descriptor = new EntityDescriptor().ReadIdPSsoDescriptor(xml);
        var sso = descriptor.IdPSsoDescriptor!.SingleSignOnServices.First(service => service.Binding == ProtocolBindings.HttpRedirect).Location;
        var slo = descriptor.IdPSsoDescriptor.SingleLogoutServices.First(service => service.Binding == ProtocolBindings.HttpRedirect).Location;
        return new IdpMetadata(
            descriptor.EntityId, sso, slo, [.. descriptor.IdPSsoDescriptor.SigningCertificates], xml);
    }

    private Saml2Configuration RequestConfiguration(IdpMetadata idp, bool sign, X509Certificate2? signWith = null) => new()
    {
        Issuer = EntityId,
        SingleSignOnDestination = idp.SsoUrl,
        SingleLogoutDestination = idp.SloUrl,
        SigningCertificate = sign ? signWith ?? Certificate : null,
        SignAuthnRequest = sign,
        SignatureAlgorithm = Saml2SecurityAlgorithms.RsaSha256Signature,
        CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
        RevocationMode = X509RevocationMode.NoCheck,
    };

    /// <summary>HTTP-Redirect バインディングの AuthnRequest（IdP 内のパスとクエリ）を作る。</summary>
    public (string PathAndQuery, string RequestId) RedirectAuthnRequest(
        IdpMetadata idp,
        bool sign = false,
        string relayState = "rs-1",
        bool forceAuthn = false,
        bool isPassive = false,
        string? acsOverride = null,
        X509Certificate2? signWith = null)
    {
        var request = new Saml2AuthnRequest(RequestConfiguration(idp, sign, signWith))
        {
            AssertionConsumerServiceUrl = new Uri(acsOverride ?? AcsUrl),
            ProtocolBinding = ProtocolBindings.HttpPost,
            ForceAuthn = forceAuthn ? true : null,
            IsPassive = isPassive ? true : null,
        };
        var binding = new Saml2RedirectBinding { RelayState = relayState };
        _ = binding.Bind(request);
        return (binding.RedirectLocation.PathAndQuery, request.IdAsString);
    }

    /// <summary>HTTP-POST バインディングの AuthnRequest（フォームの値）を作る。</summary>
    public (Dictionary<string, string> Form, string RequestId) PostAuthnRequest(IdpMetadata idp, bool sign = false, string relayState = "rs-post")
    {
        var request = new Saml2AuthnRequest(RequestConfiguration(idp, sign))
        {
            AssertionConsumerServiceUrl = new Uri(AcsUrl),
            ProtocolBinding = ProtocolBindings.HttpPost,
        };
        var binding = new Saml2PostBinding { RelayState = relayState };
        _ = binding.Bind(request);
        return (
            new Dictionary<string, string>
            {
                ["SAMLRequest"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.XmlDocument.OuterXml)),
                ["RelayState"] = relayState,
            },
            request.IdAsString);
    }

    /// <summary>ログアウト要求（HTTP-Redirect）を作る。</summary>
    public string RedirectLogoutRequest(IdpMetadata idp, string nameId, string sessionIndex, bool sign = true)
    {
        var request = new Saml2LogoutRequest(RequestConfiguration(idp, sign))
        {
            NameId = new Microsoft.IdentityModel.Tokens.Saml2.Saml2NameIdentifier(nameId, NameIdentifierFormats.Email),
            SessionIndex = sessionIndex,
            Destination = idp.SloUrl,
        };
        var binding = new Saml2RedirectBinding { RelayState = "slo-rs" };
        _ = binding.Bind(request);
        return binding.RedirectLocation.PathAndQuery;
    }

    /// <summary>IdP の応答（<c>SAMLResponse</c>）を、SP として検証して読む。</summary>
    public Saml2AuthnResponse ValidateResponse(IdpMetadata idp, string samlResponse, string? relayState = null)
    {
        var configuration = new Saml2Configuration
        {
            Issuer = EntityId,
            AllowedIssuer = idp.EntityId,
            AudienceRestricted = true,
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        configuration.AllowedAudienceUris.Add(EntityId);
        configuration.SignatureValidationCertificates.AddRange(idp.SigningCertificates);

        var response = new Saml2AuthnResponse(configuration);
        var form = new NameValueCollection { ["SAMLResponse"] = samlResponse };
        if (relayState is not null)
        {
            form["RelayState"] = relayState;
        }

        var http = new ITfoxtec.Identity.Saml2.Http.HttpRequest
        {
            Method = "POST",
            Form = form,
            QueryString = string.Empty,
            Query = new NameValueCollection(),
            Binding = new Saml2PostBinding(),
        };
        http.Binding.ReadSamlResponse(http, response);
        if (response.Status == Saml2StatusCodes.Success)
        {
            http.Binding.Unbind(http, response);
        }

        return response;
    }

    public static string DecodeXml(string samlResponse) => Encoding.UTF8.GetString(Convert.FromBase64String(samlResponse));

    public void Dispose() => Certificate.Dispose();

    /// <summary>自動送信ページから、ACS とフォームの値を取り出す。</summary>
    public static (string Action, string SamlResponse, string? RelayState) ParseAutoPost(string html)
    {
        var action = Regex.Match(html, "<form id=\"autoPost\" method=\"post\" action=\"([^\"]*)\"");
        Assert.True(action.Success, "自動送信のフォームが無い:\n" + html);
        var response = Regex.Match(html, "name=\"SAMLResponse\" value=\"([^\"]*)\"");
        Assert.True(response.Success, "SAMLResponse が無い:\n" + html);
        var relay = Regex.Match(html, "name=\"RelayState\" value=\"([^\"]*)\"");
        return (
            WebUtility.HtmlDecode(action.Groups[1].Value),
            WebUtility.HtmlDecode(response.Groups[1].Value),
            relay.Success ? WebUtility.HtmlDecode(relay.Groups[1].Value) : null);
    }

    /// <summary>
    /// SSO を最後まで通す。ログインが要れば <paramref name="loginId"/> で入り、
    /// 最後の応答（自動送信ページ、またはエラー）を返す。
    /// </summary>
    public static async Task<HttpResponseMessage> SsoAsync(Browser browser, string pathAndQuery, string? loginId, string password = E2E.Password)
    {
        var first = await browser.GetAsync(pathAndQuery);
        return await ContinueAsync(browser, first, loginId, password);
    }

    public static async Task<HttpResponseMessage> PostSsoAsync(Browser browser, Dictionary<string, string> form, string? loginId, string password = E2E.Password)
    {
        var first = await browser.PostFormAsync("/saml/sso", form);
        return await ContinueAsync(browser, first, loginId, password);
    }

    private static async Task<HttpResponseMessage> ContinueAsync(Browser browser, HttpResponseMessage first, string? loginId, string password)
    {
        if (first.StatusCode == HttpStatusCode.Redirect && first.Headers.Location!.OriginalString.StartsWith("/login", StringComparison.Ordinal))
        {
            Assert.NotNull(loginId);
            var returnUrl = QueryHelpers.ParseQuery(first.Headers.Location.OriginalString.Split('?', 2)[1])["returnUrl"].ToString();
            first.Dispose();

            var login = await browser.SubmitPasswordAsync(loginId!, password, returnUrl);
            if (login.StatusCode == HttpStatusCode.Redirect && login.Headers.Location!.OriginalString.StartsWith("/login/totp", StringComparison.Ordinal))
            {
                return login;
            }

            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var next = login.Headers.Location!.OriginalString;
            login.Dispose();
            return await browser.GetAsync(next);
        }

        return first;
    }
}
