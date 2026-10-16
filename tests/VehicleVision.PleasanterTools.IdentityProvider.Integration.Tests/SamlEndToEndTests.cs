using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Claims;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.AspNetCore.WebUtilities;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>SAML 2.0 の IdP を、SP 側のライブラリ（ITfoxtec）で実際に通して確かめる。</summary>
public class SamlEndToEndTests(IdpProcess idp) : IClassFixture<IdpProcess>
{
    [Fact]
    public async Task メタデータにSSOとSLOの宛先と署名証明書がある()
    {
        using var browser = idp.NewBrowser();

        var metadata = await SamlSp.ReadMetadataAsync(browser);

        Assert.Equal(idp.Issuer + "/saml/metadata", metadata.EntityId);
        Assert.Equal(idp.Issuer + "/saml/sso", metadata.SsoUrl.ToString());
        Assert.Equal(idp.Issuer + "/saml/slo", metadata.SloUrl.ToString());
        Assert.NotEmpty(metadata.SigningCertificates);
        Assert.All(metadata.SigningCertificates, certificate => Assert.False(certificate.HasPrivateKey));
        Assert.DoesNotContain("PRIVATE", metadata.Xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 署名なしのRedirect要求でログインし署名付きの応答が検証できる()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        var loginId = E2E.Unique("saml");
        await idp.Pleasanter.AddDeptAsync(1, 31, "開発部");
        await idp.Pleasanter.AddGroupAsync(1, 310, "開発チーム");
        var userId = await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, name: "山田 花子", mail: loginId + "@example.com", deptId: 31);
        await idp.Pleasanter.AddGroupMemberAsync(310, userId: userId);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var (request, requestId) = sp.RedirectAuthnRequest(metadata, relayState: "relay-123");
        using var page = await SamlSp.SsoAsync(browser, request, loginId);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var (action, samlResponse, relayState) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        Assert.Equal(sp.AcsUrl, action);
        Assert.Equal("relay-123", relayState);

        // 応答の送り先（ACS）だけをフォームの送信先として許す CSP
        var csp = string.Join(';', page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("form-action 'self' https://sp-", csp, StringComparison.Ordinal);

        var response = sp.ValidateResponse(metadata, samlResponse, relayState);
        Assert.Equal(Saml2StatusCodes.Success, response.Status);
        Assert.Equal(requestId, response.InResponseToAsString);
        Assert.Equal(loginId + "@example.com", response.ClaimsIdentity.FindFirst(Saml2ClaimTypes.NameId)!.Value);
        Assert.Equal("山田 花子", response.ClaimsIdentity.FindFirst("displayName")!.Value);
        Assert.Equal("開発部", response.ClaimsIdentity.FindFirst("department")!.Value);
        Assert.Equal(["開発チーム"], response.ClaimsIdentity.FindAll("groups").Select(claim => claim.Value).ToArray());
        Assert.StartsWith("_", response.ClaimsIdentity.FindFirst(Saml2ClaimTypes.SessionIndex)!.Value, StringComparison.Ordinal);

        // **秘密は載らない**
        var xml = SamlSp.DecodeXml(samlResponse);
        Assert.DoesNotContain("SecretKey", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Core.Pleasanter.PleasanterPassword.Hash(E2E.Password), xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 署名付きのRedirect要求は署名を検証して受け付ける()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, requireSigned: true);
        var loginId = E2E.Unique("signed");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var (request, _) = sp.RedirectAuthnRequest(metadata, sign: true);
        using var page = await SamlSp.SsoAsync(browser, request, loginId);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        Assert.Equal(Saml2StatusCodes.Success, sp.ValidateResponse(metadata, samlResponse).Status);
    }

    [Fact]
    public async Task 署名が必須なのに署名の無い要求は受け付けない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, requireSigned: true);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var (request, _) = sp.RedirectAuthnRequest(metadata, sign: false);
        using var response = await browser.GetAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 改ざんした署名付きの要求は受け付けない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, requireSigned: true);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var (request, _) = sp.RedirectAuthnRequest(metadata, sign: true, relayState: "original");
        var tampered = request.Replace("RelayState=original", "RelayState=tampered", StringComparison.Ordinal);
        Assert.NotEqual(request, tampered);

        using var response = await browser.GetAsync(tampered);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 別の鍵で署名した要求は受け付けない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        using var attacker = SamlSp.Create("attacker");
        await sp.RegisterAsync(admin, requireSigned: true);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        // 登録した SP の名前（Issuer）を名乗り、自分の鍵で署名する
        var (request, _) = sp.RedirectAuthnRequest(metadata, sign: true, signWith: attacker.Certificate);
        using var response = await browser.GetAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task POSTバインディングの要求でも通る()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, requireSigned: true);
        var loginId = E2E.Unique("post");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var (form, requestId) = sp.PostAuthnRequest(metadata, sign: true, relayState: "rs-post");
        using var page = await SamlSp.PostSsoAsync(browser, form, loginId);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var (_, samlResponse, relayState) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        var response = sp.ValidateResponse(metadata, samlResponse, relayState);
        Assert.Equal(requestId, response.InResponseToAsString);
        Assert.Equal("rs-post", relayState);
    }

    [Fact]
    public async Task 登録されていないSPや無効なSPには応答しない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var unregistered = SamlSp.Create();
        using var disabled = SamlSp.Create();
        await disabled.RegisterAsync(admin, enabled: false);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        foreach (var sp in new[] { unregistered, disabled })
        {
            var (request, _) = sp.RedirectAuthnRequest(metadata);
            using var response = await browser.GetAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }

    [Fact]
    public async Task 登録と違うACSを名乗る要求は受け付けない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var (request, _) = sp.RedirectAuthnRequest(metadata, acsOverride: "https://evil.example.com/acs");
        using var response = await browser.GetAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 壊れた要求は400()
    {
        using var browser = idp.NewBrowser();

        foreach (var path in new[] { "/saml/sso", "/saml/sso?SAMLRequest=!!!", "/saml/sso?SAMLRequest=aGVsbG8%3D" })
        {
            using var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task 既にログインしていれば再入力なしで二つ目のSPへ応答する()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var first = SamlSp.Create();
        using var second = SamlSp.Create();
        await first.RegisterAsync(admin);
        await second.RegisterAsync(admin);
        var loginId = E2E.Unique("sso2");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        using var firstPage = await SamlSp.SsoAsync(browser, first.RedirectAuthnRequest(metadata).PathAndQuery, loginId);
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);

        // ログインを求められず、直接応答が返る
        using var secondPage = await browser.GetAsync(second.RedirectAuthnRequest(metadata).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        var (action, _, _) = SamlSp.ParseAutoPost(await secondPage.Content.ReadAsStringAsync());
        Assert.Equal(second.AcsUrl, action);
    }

    [Fact]
    public async Task ForceAuthnなら再認証を求める()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        var loginId = E2E.Unique("force");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);
        using (var warm = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId)) { }

        using var forced = await browser.GetAsync(sp.RedirectAuthnRequest(metadata, forceAuthn: true).PathAndQuery);

        Assert.Equal(HttpStatusCode.Redirect, forced.StatusCode);
        Assert.StartsWith("/login", forced.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsPassiveでログインしていなければNoPassiveを返す()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var (request, requestId) = sp.RedirectAuthnRequest(metadata, isPassive: true);
        using var page = await browser.GetAsync(request);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var (action, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        Assert.Equal(sp.AcsUrl, action);
        var xml = SamlSp.DecodeXml(samlResponse);
        Assert.Contains("NoPassive", xml, StringComparison.Ordinal);
        Assert.Contains(requestId, xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<saml:Assertion", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TOTPを使うユーザはSAMLでもコードを求められ認証方式が伝わる()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        var loginId = E2E.Unique("samltotp");
        var secret = E2E.NewTotpSecret();
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: secret, enableSecretKey: true, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var (request, _) = sp.RedirectAuthnRequest(metadata);
        using var password = await SamlSp.SsoAsync(browser, request, loginId);
        Assert.Equal(HttpStatusCode.Redirect, password.StatusCode);
        var totpUrl = password.Headers.Location!.OriginalString;
        Assert.StartsWith("/login/totp", totpUrl, StringComparison.Ordinal);

        var returnUrl = QueryHelpers.ParseQuery(totpUrl.Split('?', 2)[1])["returnUrl"].ToString();
        using var totp = await browser.SubmitTotpAsync(E2E.TotpCode(secret), returnUrl);
        Assert.Equal(HttpStatusCode.Redirect, totp.StatusCode);
        using var page = await browser.GetAsync(totp.Headers.Location!.OriginalString);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        Assert.Equal(Saml2StatusCodes.Success, sp.ValidateResponse(metadata, samlResponse).Status);
        Assert.Contains("TimeSyncToken", SamlSp.DecodeXml(samlResponse), StringComparison.Ordinal);
    }

    [Fact]
    public async Task メールが無いのにメールをNameIDに求められたら応答しない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, nameIdSource: "email");
        var loginId = E2E.Unique("nomail");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.DoesNotContain("SAMLResponse", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 許可していないテナントのユーザには応答しない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, tenants: [2]);
        var loginId = E2E.Unique("othertenant");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, tenantId: 1, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
    }

    [Theory]
    [InlineData("persistent", "userId")]
    [InlineData("unspecified", "loginId")]
    [InlineData("emailAddress", "email")]
    public async Task NameIDの形式と元を選べる(string format, string source)
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, nameIdFormat: format, nameIdSource: source);
        var loginId = E2E.Unique("nameid");
        var userId = await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        var response = sp.ValidateResponse(metadata, samlResponse);

        var expected = source switch { "userId" => userId.ToString(), "loginId" => loginId, _ => loginId + "@example.com" };
        Assert.Equal(expected, response.ClaimsIdentity.FindFirst(Saml2ClaimTypes.NameId)!.Value);
    }

    [Fact]
    public async Task transientのNameIDは毎回変わる()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, nameIdFormat: "transient", nameIdSource: "loginId");
        var loginId = E2E.Unique("transient");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        var ids = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);
            var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
            ids.Add(sp.ValidateResponse(metadata, samlResponse).ClaimsIdentity.FindFirst(Saml2ClaimTypes.NameId)!.Value);
        }

        Assert.NotEqual(ids[0], ids[1]);
        Assert.DoesNotContain(ids, id => id.Contains(loginId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task アサーションだけ署名する設定でも検証できる()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin, signAssertion: true, signResponse: false);
        var loginId = E2E.Unique("assertiononly");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());

        Assert.Equal(Saml2StatusCodes.Success, sp.ValidateResponse(metadata, samlResponse).Status);
    }

    [Fact]
    public async Task ローテーション後は新しい鍵で署名し旧鍵の証明書もメタデータに残る()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var browser = idp.NewBrowser();
        var before = await SamlSp.ReadMetadataAsync(browser);

        using var rotate = await E2E.AdminPostAsync(admin, "/api/admin/signing-keys/rotate", new { purpose = "saml" });
        Assert.Equal(HttpStatusCode.Created, rotate.StatusCode);

        // 鍵のキャッシュは最長 30 秒。**旧鍵の証明書は残り続ける**ので、SP が古い証明書で検証していても通る
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        var loginId = E2E.Unique("rotatesaml");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, mail: loginId + "@example.com");

        var deadline = DateTime.UtcNow.AddSeconds(45);
        IdpMetadata after;
        do
        {
            after = await SamlSp.ReadMetadataAsync(browser);
            if (after.SigningCertificates.Count > before.SigningCertificates.Count)
            {
                break;
            }

            await Task.Delay(1000);
        }
        while (DateTime.UtcNow < deadline);

        Assert.True(after.SigningCertificates.Count > before.SigningCertificates.Count);
        foreach (var certificate in before.SigningCertificates)
        {
            Assert.Contains(after.SigningCertificates, c => c.Thumbprint == certificate.Thumbprint);
        }

        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(after).PathAndQuery, loginId);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        Assert.Equal(Saml2StatusCodes.Success, sp.ValidateResponse(after, samlResponse).Status);
    }

    // ---- SLO -------------------------------------------------------------

    [Fact]
    public async Task SPからのログアウト要求でIdPのセッションが閉じ応答が返る()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        var loginId = E2E.Unique("slo");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);

        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        var login = sp.ValidateResponse(metadata, samlResponse);
        var nameId = login.ClaimsIdentity.FindFirst(Saml2ClaimTypes.NameId)!.Value;
        var sessionIndex = login.ClaimsIdentity.FindFirst(Saml2ClaimTypes.SessionIndex)!.Value;
        using (var before = await browser.GetAsync("/api/admin/me"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var logout = await browser.GetAsync(sp.RedirectLogoutRequest(metadata, nameId, sessionIndex));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var location = logout.Headers.Location!;
        Assert.StartsWith(sp.SloUrl, location.ToString(), StringComparison.Ordinal);
        Assert.True(QueryHelpers.ParseQuery(location.Query).ContainsKey("SAMLResponse"));
        using var after = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task cookieの無いログアウト要求もセッション索引とNameIDで対応づけて閉じる()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        var loginId = E2E.Unique("slocookieless");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);
        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        var login = sp.ValidateResponse(metadata, samlResponse);

        // SP のサーバから直接（ブラウザの cookie を持たずに）送る
        using var server = idp.NewBrowser();
        using var logout = await server.GetAsync(sp.RedirectLogoutRequest(
            metadata,
            login.ClaimsIdentity.FindFirst(Saml2ClaimTypes.NameId)!.Value,
            login.ClaimsIdentity.FindFirst(Saml2ClaimTypes.SessionIndex)!.Value));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using var after = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task 署名の無いログアウト要求は証明書を登録したSPには受け付けない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);
        var loginId = E2E.Unique("sloforged");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);
        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
        var login = sp.ValidateResponse(metadata, samlResponse);

        using var forged = await browser.GetAsync(sp.RedirectLogoutRequest(
            metadata,
            login.ClaimsIdentity.FindFirst(Saml2ClaimTypes.NameId)!.Value,
            login.ClaimsIdentity.FindFirst(Saml2ClaimTypes.SessionIndex)!.Value,
            sign: false));

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var still = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    // ---- 管理 API（SAML の登録）--------------------------------------------

    [Fact]
    public async Task メタデータXMLから登録の下書きを作れる()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        var descriptor = new EntityDescriptor(new Saml2Configuration { Issuer = sp.EntityId })
        {
            SPSsoDescriptor = new SPSsoDescriptor
            {
                AuthnRequestsSigned = true,
                WantAssertionsSigned = true,
                SigningCertificates = [sp.Certificate],
                AssertionConsumerServices =
                [
                    new AssertionConsumerService { Binding = ProtocolBindings.HttpRedirect, Location = new Uri("https://other.example.com/redirect-acs") },
                    new AssertionConsumerService { Binding = ProtocolBindings.HttpPost, Location = new Uri(sp.AcsUrl), IsDefault = true },
                ],
                SingleLogoutServices = [new SingleLogoutService { Binding = ProtocolBindings.HttpRedirect, Location = new Uri(sp.SloUrl) }],
            },
        };
        var xml = new Saml2Metadata(descriptor).CreateMetadata().ToXml();

        using var response = await E2E.AdminPostAsync(admin, "/api/admin/saml-providers/import-metadata", new { xml });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var draft = await E2E.ReadAsync(response);
        Assert.Equal(sp.EntityId, draft["entityId"]!.GetValue<string>());
        Assert.Equal(sp.AcsUrl, draft["acsUrl"]!.GetValue<string>());
        Assert.Equal(sp.SloUrl, draft["sloUrl"]!.GetValue<string>());
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", draft["signingCertificate"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(draft["requireSignedRequests"]!.GetValue<bool>());

        // 下書きなので、保存はされていない
        using var list = await admin.GetAsync("/api/admin/saml-providers");
        Assert.DoesNotContain((await E2E.ReadAsync(list)).AsArray(), node => node!["entityId"]!.GetValue<string>() == sp.EntityId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<root/>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a SYSTEM \"file:///c:/windows/win.ini\">]><EntityDescriptor xmlns=\"urn:oasis:names:tc:SAML:2.0:metadata\" entityID=\"&a;\"/>")]
    public async Task 読めないメタデータや外部実体を含むXMLは400(string xml)
    {
        using var admin = await E2E.AdminAsync(idp);

        using var response = await E2E.AdminPostAsync(admin, "/api/admin/saml-providers/import-metadata", new { xml });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SPの登録は検証され重複は409になる()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        await sp.RegisterAsync(admin);

        using var duplicate = await E2E.AdminPostAsync(admin, "/api/admin/saml-providers", new
        {
            entityId = sp.EntityId,
            displayName = "重複",
            acsUrl = sp.AcsUrl,
            nameIdFormat = "emailAddress",
            nameIdSource = "email",
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var invalid = await E2E.AdminPostAsync(admin, "/api/admin/saml-providers", new
        {
            entityId = "https://invalid.example.com",
            displayName = "不正",
            acsUrl = "http://insecure.example.com/acs",
            nameIdFormat = "emailAddress",
            nameIdSource = "email",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task SPの更新と削除ができ削除後は応答しない()
    {
        using var admin = await E2E.AdminAsync(idp);
        using var sp = SamlSp.Create();
        var created = await sp.RegisterAsync(admin);
        var providerId = created["providerId"]!.GetValue<string>();

        using var update = await E2E.AdminSendAsync(admin, HttpMethod.Put, "/api/admin/saml-providers/" + providerId, new
        {
            entityId = sp.EntityId,
            displayName = "更新後",
            acsUrl = sp.AcsUrl,
            nameIdFormat = "unspecified",
            nameIdSource = "loginId",
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("更新後", (await E2E.ReadAsync(update))["displayName"]!.GetValue<string>());

        using var delete = await E2E.AdminSendAsync(admin, HttpMethod.Delete, "/api/admin/saml-providers/" + providerId);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser);
        using var response = await browser.GetAsync(sp.RedirectAuthnRequest(metadata).PathAndQuery);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
