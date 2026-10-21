using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.IdentityModel.Tokens.Saml2;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>署名・メタデータ・応答の組み立て（SAML 2.0 の IdP 側）。</summary>
/// <remarks>
/// <para>
/// プロトコルの細部（XML 署名・正規化・バインディング）は ITfoxtec.Identity.Saml2 に任せる。
/// **自前で XML 署名を書かない。** 署名の巻き込み（XML Signature Wrapping）を作り込みやすい。
/// </para>
/// <para>
/// 署名鍵は <see cref="SigningKeyService"/> の <c>saml</c> 用。ローテーション後も、旧鍵の証明書は
/// メタデータへ載せ続ける（SP が古い証明書で検証している間も、署名の検証が通る）。
/// </para>
/// </remarks>
public sealed class SamlIdpService(SigningKeyService keys, IdpOptions options)
{
    /// <summary>この IdP の EntityID（メタデータの URL と同じにする慣例）。</summary>
    public string EntityId => options.Issuer + "/saml/metadata";

    public Uri SingleSignOnUrl => new(options.Issuer + "/saml/sso");

    public Uri SingleLogoutUrl => new(options.Issuer + "/saml/slo");

    /// <summary>署名に使う設定（SAML 用 active 鍵）。</summary>
    public async Task<Saml2Configuration> SigningConfigurationAsync(
        SamlProvider? provider,
        CancellationToken cancellationToken)
    {
        var key = await keys.GetActiveAsync(SigningKey.Saml, cancellationToken).ConfigureAwait(false);
        return new Saml2Configuration
        {
            Issuer = EntityId,
            SingleSignOnDestination = SingleSignOnUrl,
            SingleLogoutDestination = SingleLogoutUrl,
            SigningCertificate = key.Certificate,
            SignatureAlgorithm = Saml2SecurityAlgorithms.RsaSha256Signature,
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
            AuthnResponseSignType = provider is null ? Saml2AuthnResponseSignTypes.SignAssertionAndResponse : SignTypeOf(provider),
        };
    }

    /// <summary>SP からの要求を検証するための設定。署名があるときだけ、登録された証明書を使う。</summary>
    public static Saml2Configuration ValidationConfiguration(SamlProvider provider, string issuer, bool hasSignature)
    {
        var configuration = new Saml2Configuration
        {
            Issuer = issuer,
            AllowedIssuer = provider.EntityId,
            CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
            RevocationMode = X509RevocationMode.NoCheck,
        };

        // **署名の種類は SP 任せにしない。** SHA-1 は受け取らない
        configuration.SignatureValidationAlgorithms.Add(Saml2SecurityAlgorithms.RsaSha256Signature);
        configuration.SignatureValidationAlgorithms.Add(Saml2SecurityAlgorithms.RsaSha384Signature);
        configuration.SignatureValidationAlgorithms.Add(Saml2SecurityAlgorithms.RsaSha512Signature);

        if (hasSignature && !string.IsNullOrWhiteSpace(provider.SigningCertificate)
            && CertificateText.TryParse(provider.SigningCertificate) is { } certificate)
        {
            configuration.SignatureValidationCertificates.Add(certificate);

            // ⚠️ **これを立てないと、証明書を渡しても署名を検証しない**（ライブラリの仕様。
            // 別の鍵で署名した要求も、改ざんした要求も通ってしまう。E2E 試験で確認済み）。
            // 「署名を期待する」という意味の設定で、要求側の名前（SignAuthnRequest）のまま使われる
            configuration.SignAuthnRequest = true;
        }

        return configuration;
    }

    public async Task<string> BuildMetadataAsync(CancellationToken cancellationToken)
    {
        var configuration = await SigningConfigurationAsync(null, cancellationToken).ConfigureAwait(false);
        var certificates = (await keys.GetAllAsync(SigningKey.Saml, cancellationToken).ConfigureAwait(false))
            .Select(key => key.Certificate)
            .ToArray();

        var descriptor = new EntityDescriptor(configuration)
        {
            ValidUntil = 365,
            IdPSsoDescriptor = new IdPSsoDescriptor
            {
                // **署名付きの要求は SP ごとの設定で必須にできる。** メタデータでは一律に求めない
                WantAuthnRequestsSigned = false,
                SigningCertificates = certificates,
                SingleSignOnServices =
                [
                    new SingleSignOnService { Binding = ProtocolBindings.HttpRedirect, Location = SingleSignOnUrl },
                    new SingleSignOnService { Binding = ProtocolBindings.HttpPost, Location = SingleSignOnUrl },
                ],
                SingleLogoutServices =
                [
                    new SingleLogoutService { Binding = ProtocolBindings.HttpRedirect, Location = SingleLogoutUrl },
                    new SingleLogoutService { Binding = ProtocolBindings.HttpPost, Location = SingleLogoutUrl },
                ],
                NameIDFormats =
                [
                    NameIdentifierFormats.Email,
                    NameIdentifierFormats.Persistent,
                    NameIdentifierFormats.Unspecified,
                    NameIdentifierFormats.Transient,
                ],
            },
        };

        return new Saml2Metadata(descriptor).CreateMetadata().ToXml();
    }

    /// <summary>アサーション入りの応答を POST バインディング用に組み立てる。</summary>
    /// <returns>フォームへ入れる <c>SAMLResponse</c>（Base64）。</returns>
    public async Task<string> BuildResponseAsync(
        SamlProvider provider,
        string inResponseTo,
        string? relayState,
        PleasanterUserProfile profile,
        string sessionIndex,
        bool multiFactor,
        string nameId,
        Uri nameIdFormat,
        CancellationToken cancellationToken)
    {
        var configuration = await SigningConfigurationAsync(provider, cancellationToken).ConfigureAwait(false);
        var response = new Saml2AuthnResponse(configuration)
        {
            Status = Saml2StatusCodes.Success,
            InResponseTo = new Saml2Id(inResponseTo),
            Destination = new Uri(provider.AcsUrl),
            SessionIndex = sessionIndex,
            NameId = new Saml2NameIdentifier(nameId, nameIdFormat),
        };

        var claims = new List<Claim>();
        foreach (var mapping in provider.AttributeMappings)
        {
            foreach (var value in UserClaims.SamlValues(profile, mapping.Source))
            {
                claims.Add(new Claim(mapping.AttributeName, value));
            }
        }

        // **属性が 1 つも無い SP（NameID だけを使う SP）がある。** ライブラリは属性の無い
        // AttributeStatement を書けず（IDX13129。実際に 500 になった）、ClaimsIdentity を空にしても
        // null にしても落ちる。**属性が 1 つも要らない SP には、ログイン ID を `loginId` として 1 つだけ載せる**
        // （属性は無視するのが SP の普通の作りで、害は無い）
        if (claims.Count == 0)
        {
            claims.Add(new Claim("loginId", profile.User.LoginId));
        }

        response.ClaimsIdentity = new ClaimsIdentity(claims);

        // 多要素なら、その旨を認証コンテキストで SP へ伝える
        var authnContext = multiFactor
            ? new Uri("urn:oasis:names:tc:SAML:2.0:ac:classes:TimeSyncToken")
            : new Uri("urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport");

        _ = response.CreateSecurityToken(
            provider.EntityId,
            authnContext,
            declAuthnContext: null,
            subjectConfirmationLifetime: Math.Max(1, (int)Math.Ceiling(options.SamlAssertionLifetime.TotalMinutes)),
            issuedTokenLifetime: (int)options.SamlAssertionLifetime.TotalSeconds);

        var binding = new Saml2PostBinding();
        _ = binding.Bind(response);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.XmlDocument.OuterXml));
    }

    /// <summary>失敗（<c>NoPassive</c> など）を知らせる、アサーション無しの応答。</summary>
    public async Task<string> BuildStatusResponseAsync(
        SamlProvider provider,
        string inResponseTo,
        Saml2StatusCodes status,
        CancellationToken cancellationToken)
    {
        var configuration = await SigningConfigurationAsync(provider, cancellationToken).ConfigureAwait(false);
        var response = new Saml2AuthnResponse(configuration)
        {
            Status = status,
            InResponseTo = new Saml2Id(inResponseTo),
            Destination = new Uri(provider.AcsUrl),
        };

        var binding = new Saml2PostBinding();
        _ = binding.Bind(response);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.XmlDocument.OuterXml));
    }

    public static Uri NameIdFormatOf(string format) => format switch
    {
        "emailAddress" => NameIdentifierFormats.Email,
        "persistent" => NameIdentifierFormats.Persistent,
        "transient" => NameIdentifierFormats.Transient,
        _ => NameIdentifierFormats.Unspecified,
    };

    public static string SessionIndexOf(Guid sessionId) => "_" + sessionId.ToString("N");

    public static Guid? SessionIdOf(string? sessionIndex) =>
        sessionIndex is { Length: 33 } && sessionIndex[0] == '_' && Guid.TryParseExact(sessionIndex[1..], "N", out var id)
            ? id
            : null;

    private static Saml2AuthnResponseSignTypes SignTypeOf(SamlProvider provider) => (provider.SignAssertion, provider.SignResponse) switch
    {
        (true, true) => Saml2AuthnResponseSignTypes.SignAssertionAndResponse,
        (false, true) => Saml2AuthnResponseSignTypes.SignResponse,
        _ => Saml2AuthnResponseSignTypes.SignAssertion,
    };
}
