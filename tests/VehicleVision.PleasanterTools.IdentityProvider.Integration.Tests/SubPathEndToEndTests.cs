using System.Net;
using Microsoft.AspNetCore.WebUtilities;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>Pleasanter と同じホストのサブパス（<c>/idp</c>）へ置いた構成を、通しで確かめる。</summary>
/// <remarks>
/// **リダイレクトの <c>Location</c>・フォームの送信先・cookie の <c>Path</c>・メタデータの URL に、
/// サブパスが抜けると、ログインが回らない。** 根に置いた構成の試験では見えない。
/// </remarks>
public class SubPathEndToEndTests(SubPathProcess idp) : IClassFixture<SubPathProcess>
{
    private const string Prefix = "/idp";

    [Fact]
    public async Task 設定したサブパスの外には応答しない()
    {
        using var browser = idp.NewBrowser();

        foreach (var path in new[] { "/login", "/.well-known/openid-configuration", "/oidc/jwks", "/saml/metadata", "/api/admin/me", "/admin" })
        {
            using var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // 生存確認だけはサブパスの外でも応答する（コンテナの probe がプロキシを通らずに直接当たるため）
        using var health = await browser.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ディスカバリとメタデータのURLにサブパスが入る()
    {
        using var browser = idp.NewBrowser();

        using var discovery = await browser.GetAsync(Prefix + "/.well-known/openid-configuration");
        var document = await E2E.ReadAsync(discovery);
        Assert.Equal(idp.Issuer, document["issuer"]!.GetValue<string>());
        Assert.EndsWith("/idp", document["issuer"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(idp.Issuer + "/oidc/authorize", document["authorization_endpoint"]!.GetValue<string>());

        using var metadata = await browser.GetAsync(Prefix + "/saml/metadata");
        var xml = await metadata.Content.ReadAsStringAsync();
        Assert.Contains(idp.Issuer + "/saml/sso", xml, StringComparison.Ordinal);
        Assert.Contains("entityID=\"" + idp.Issuer + "/saml/metadata\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ログインのリダイレクトとフォームとcookieにサブパスが入る()
    {
        var loginId = E2E.Unique("subpath");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true);
        using var browser = idp.NewBrowser();

        using var page = await browser.GetAsync(Prefix + "/login?returnUrl=" + Uri.EscapeDataString(Prefix + "/admin"));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/idp/login\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/idp/idp-assets/login.css\"", html, StringComparison.Ordinal);
        var csrfCookie = string.Join(';', page.Headers.GetValues("Set-Cookie"));
        Assert.Contains("path=/idp", csrfCookie, StringComparison.OrdinalIgnoreCase);

        var form = new Dictionary<string, string>
        {
            ["loginId"] = loginId,
            ["password"] = E2E.Password,
            ["returnUrl"] = Browser.Hidden(html, "returnUrl"),
            ["csrf"] = Browser.Hidden(html, "csrf"),
        };
        using var login = await browser.PostFormAsync(Prefix + "/login", form);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/idp/admin", login.Headers.Location!.OriginalString);
        Assert.Contains("path=/idp", string.Join(';', login.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        using var me = await browser.GetAsync(Prefix + "/api/admin/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // 戻り先は、サブパスの外へは出られない
        using var stranger = idp.NewBrowser();
        var strangerLogin = await stranger.GetAsync(Prefix + "/login");
        var strangerHtml = await strangerLogin.Content.ReadAsStringAsync();
        using var outside = await stranger.PostFormAsync(Prefix + "/login", new Dictionary<string, string>
        {
            ["loginId"] = loginId,
            ["password"] = E2E.Password,
            ["returnUrl"] = "/admin",
            ["csrf"] = Browser.Hidden(strangerHtml, "csrf"),
        });
        Assert.Equal("/idp/admin", outside.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task サブパスの下でOIDCの認可コードフローが通る()
    {
        using var admin = idp.NewBrowser();
        var adminLogin = E2E.Unique("subadmin");
        await idp.Pleasanter.AddUserAsync(adminLogin, E2E.Password, serviceManager: true);
        var adminPage = await admin.GetAsync(Prefix + "/login");
        var adminHtml = await adminPage.Content.ReadAsStringAsync();
        using (var adminPost = await admin.PostFormAsync(Prefix + "/login", new Dictionary<string, string>
        {
            ["loginId"] = adminLogin,
            ["password"] = E2E.Password,
            ["returnUrl"] = Prefix + "/admin",
            ["csrf"] = Browser.Hidden(adminHtml, "csrf"),
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, adminPost.StatusCode);
        }

        using var created = await E2E.AdminPostAsync(admin, Prefix + "/api/admin/oidc-clients", new
        {
            displayName = "サブパス",
            clientType = "public",
            redirectUris = new[] { E2E.RedirectUri },
            allowedScopes = new[] { "openid", "profile" },
        });
        var clientId = (await E2E.ReadAsync(created))["client"]!["clientId"]!.GetValue<string>();

        var loginId = E2E.Unique("subuser");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        using var authorize = await browser.GetAsync(Prefix + E2E.AuthorizeUrl(clientId, challenge, "openid profile"));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.StartsWith("/idp/login?returnUrl=", authorize.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var returnUrl = QueryHelpers.ParseQuery(authorize.Headers.Location.OriginalString.Split('?', 2)[1])["returnUrl"].ToString();
        Assert.StartsWith("/idp/oidc/authorize?", returnUrl, StringComparison.Ordinal);

        var loginPage = await browser.GetAsync(authorize.Headers.Location.OriginalString);
        var loginHtml = await loginPage.Content.ReadAsStringAsync();
        using var login = await browser.PostFormAsync(Prefix + "/login", new Dictionary<string, string>
        {
            ["loginId"] = loginId,
            ["password"] = E2E.Password,
            ["returnUrl"] = returnUrl,
            ["csrf"] = Browser.Hidden(loginHtml, "csrf"),
        });
        Assert.Equal(returnUrl, login.Headers.Location!.OriginalString);

        using var code = await browser.GetAsync(login.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, code.StatusCode);

        using var request = E2E.TokenRequest(clientId, null,
        [
            new("grant_type", "authorization_code"),
            new("code", E2E.CodeOf(code.Headers.Location!)),
            new("redirect_uri", E2E.RedirectUri),
            new("code_verifier", verifier),
        ]);
        request.RequestUri = new Uri(Prefix + "/oidc/token", UriKind.Relative);
        using var token = await browser.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var idToken = (await E2E.ReadAsync(token))["id_token"]!.GetValue<string>();
        Assert.Equal(idp.Issuer, new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(idToken).Issuer);
    }

    [Fact]
    public async Task サブパスの下でSAMLのSSOが通る()
    {
        using var admin = idp.NewBrowser();
        var adminLogin = E2E.Unique("subsaml");
        await idp.Pleasanter.AddUserAsync(adminLogin, E2E.Password, serviceManager: true);
        var adminPage = await admin.GetAsync(Prefix + "/login");
        using (var adminPost = await admin.PostFormAsync(Prefix + "/login", new Dictionary<string, string>
        {
            ["loginId"] = adminLogin,
            ["password"] = E2E.Password,
            ["returnUrl"] = Prefix + "/admin",
            ["csrf"] = Browser.Hidden(await adminPage.Content.ReadAsStringAsync(), "csrf"),
        }))
        {
            Assert.Equal(HttpStatusCode.Redirect, adminPost.StatusCode);
        }

        using var sp = SamlSp.Create();
        using (var created = await E2E.AdminPostAsync(admin, Prefix + "/api/admin/saml-providers", new
        {
            entityId = sp.EntityId,
            displayName = "サブパス SP",
            acsUrl = sp.AcsUrl,
            nameIdFormat = "emailAddress",
            nameIdSource = "email",
            attributeMappings = new[] { new { attributeName = "email", source = "email" } },
        }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var loginId = E2E.Unique("subsamluser");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, mail: loginId + "@example.com");
        using var browser = idp.NewBrowser();
        var metadata = await SamlSp.ReadMetadataAsync(browser, Prefix);
        Assert.Equal(idp.Issuer + "/saml/sso", metadata.SsoUrl.ToString());

        using var page = await SamlSp.SsoAsync(browser, sp.RedirectAuthnRequest(metadata).PathAndQuery, loginId);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("src=\"/idp/idp-assets/post.js\"", html, StringComparison.Ordinal);
        var (_, samlResponse, _) = SamlSp.ParseAutoPost(html);
        var response = sp.ValidateResponse(metadata, samlResponse);
        Assert.Equal(ITfoxtec.Identity.Saml2.Schemas.Saml2StatusCodes.Success, response.Status);
    }
}
