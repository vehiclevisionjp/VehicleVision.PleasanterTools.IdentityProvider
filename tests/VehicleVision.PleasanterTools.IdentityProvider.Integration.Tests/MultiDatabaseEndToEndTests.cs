using System.Net;
using ITfoxtec.Identity.Saml2.Schemas;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>IdP 自身の DB と Pleasanter 相当の DB を、RDBMS ごとに実機へ向けて、通しで動かす。</summary>
/// <remarks>
/// <para>
/// ストアごとの試験（<see cref="StoreTests"/>）では見えない、**起動時のマイグレーション・署名鍵の生成と暗号化・
/// ログイン・TOTP・認可コード・トークン・SAML 応答**の連なりを、DB ごとに 1 本通す。
/// </para>
/// <para>
/// **SQLite は常に。SQL Server / PostgreSQL / MySQL は <c>IDP_INTEGRATION=1</c> のときだけ。**
/// </para>
/// </remarks>
public class MultiDatabaseEndToEndTests
{
    public static IEnumerable<object[]> Providers() => TestDatabases.Providers();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task OIDCの認可コードフローがRDBMSを問わず通る(DatabaseProvider provider)
    {
        var idp = new IdpProcess { Provider = provider };
        await idp.InitializeAsync();
        try
        {
            using var admin = await E2E.AdminAsync(idp);
            var (clientId, secret) = await E2E.CreateClientAsync(admin);
            var loginId = E2E.Unique("multi");
            var totpSecret = E2E.NewTotpSecret();
            await idp.Pleasanter.AddGroupAsync(1, 10, "グループ");
            var userId = await idp.Pleasanter.AddUserAsync(
                loginId, E2E.Password, name: "山田 🚀 太郎", mail: loginId + "@example.com",
                secretKey: totpSecret, enableSecretKey: true);
            await idp.Pleasanter.AddGroupMemberAsync(10, userId: userId);

            var (verifier, challenge) = E2E.NewPkce();
            using var browser = idp.NewBrowser();

            // パスワード → TOTP → 認可コード
            using var authorize = await browser.GetAsync(E2E.AuthorizeUrl(clientId, challenge));
            var returnUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers
                .ParseQuery(authorize.Headers.Location!.OriginalString.Split('?', 2)[1])["returnUrl"].ToString();
            using var password = await browser.SubmitPasswordAsync(loginId, E2E.Password, returnUrl);
            Assert.StartsWith("/login/totp", password.Headers.Location!.OriginalString, StringComparison.Ordinal);
            using var totp = await browser.SubmitTotpAsync(E2E.TotpCode(totpSecret), returnUrl);
            Assert.Equal(HttpStatusCode.Redirect, totp.StatusCode);
            using var redirect = await browser.GetAsync(totp.Headers.Location!.OriginalString);
            var code = E2E.CodeOf(redirect.Headers.Location!);

            using var tokenResponse = await E2E.ExchangeCodeAsync(browser, clientId, secret, code, verifier);
            Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
            var tokens = await E2E.ReadAsync(tokenResponse);
            var idToken = await E2E.ValidateAsync(browser, tokens["id_token"]!.GetValue<string>(), clientId);
            Assert.Equal(userId.ToString(), idToken.Subject);
            Assert.Equal("山田 🚀 太郎", idToken.GetPayloadValue<string>("name"));
            Assert.Equal(["pwd", "otp"], idToken.GetPayloadValue<string[]>("amr"));

            // 時刻が UTC のまま往復する（auth_time は発行の直前）
            var authTime = DateTimeOffset.FromUnixTimeSeconds(idToken.GetPayloadValue<long>("auth_time")).UtcDateTime;
            Assert.InRange(authTime, DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddSeconds(5));

            using var refresh = await E2E.RefreshAsync(browser, clientId, secret, tokens["refresh_token"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

            // 管理 API（署名鍵は暗号化して保存され、再読み込みできる）
            using var keys = await admin.GetAsync("/api/admin/signing-keys");
            Assert.Equal(HttpStatusCode.OK, keys.StatusCode);
            using var audit = await admin.GetAsync("/api/admin/audit-logs?actor=" + loginId);
            Assert.Contains("auth.login.success", await audit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await idp.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SAMLの署名付き応答がRDBMSを問わず通る(DatabaseProvider provider)
    {
        var idp = new IdpProcess { Provider = provider };
        await idp.InitializeAsync();
        try
        {
            using var admin = await E2E.AdminAsync(idp);
            using var sp = SamlSp.Create();
            await sp.RegisterAsync(admin, requireSigned: true);
            var loginId = E2E.Unique("multisaml");
            await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, name: "花子 🌸", mail: loginId + "@example.com");
            using var browser = idp.NewBrowser();
            var metadata = await SamlSp.ReadMetadataAsync(browser);

            var (request, requestId) = sp.RedirectAuthnRequest(metadata, sign: true);
            using var page = await SamlSp.SsoAsync(browser, request, loginId);

            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var (_, samlResponse, relay) = SamlSp.ParseAutoPost(await page.Content.ReadAsStringAsync());
            var response = sp.ValidateResponse(metadata, samlResponse, relay);
            Assert.Equal(Saml2StatusCodes.Success, response.Status);
            Assert.Equal(requestId, response.InResponseToAsString);
            Assert.Equal("花子 🌸", response.ClaimsIdentity.FindFirst("displayName")!.Value);
        }
        finally
        {
            await idp.DisposeAsync();
        }
    }
}
