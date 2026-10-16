using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>OIDC を、動いている IdP へ HTTP で当てて確かめる。</summary>
/// <remarks>
/// IdP 自身の DB も Pleasanter 相当の DB も SQLite のファイル。外部の DB は要らない。
/// **部品の試験では見えない所を見る。** cookie の往復、リダイレクトの連鎖、
/// 認可コードの使い回し、セッション失効のトークンへの波及は、実際に通してみないと分からない。
/// </remarks>
public class OidcEndToEndTests(IdpProcess idp) : IClassFixture<IdpProcess>
{
    private const string Scope = "openid profile email groups offline_access";

    [Fact]
    public async Task ディスカバリとJWKSが取れる()
    {
        using var browser = idp.NewBrowser();

        using var discovery = await browser.GetAsync("/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        var document = await E2E.ReadAsync(discovery);
        Assert.Equal(idp.Issuer, document["issuer"]!.GetValue<string>());
        Assert.Equal(idp.Issuer + "/oidc/token", document["token_endpoint"]!.GetValue<string>());
        Assert.Contains("S256", document["code_challenge_methods_supported"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.DoesNotContain("none", document["id_token_signing_alg_values_supported"]!.AsArray().Select(node => node!.GetValue<string>()));

        using var jwks = await browser.GetAsync("/oidc/jwks");
        var keys = (await E2E.ReadAsync(jwks))["keys"]!.AsArray();
        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.Equal("RSA", key!["kty"]!.GetValue<string>());
            Assert.Null(key["d"]);
            Assert.Null(key["p"]);
        });
    }

    [Fact]
    public async Task 認可コードフローで発行したIDトークンとユーザ情報が正しい()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);

        var loginId = E2E.Unique("taro");
        await idp.Pleasanter.AddDeptAsync(1, 7, "営業部");
        await idp.Pleasanter.AddGroupAsync(1, 70, "営業チーム");
        await idp.Pleasanter.AddGroupAsync(1, 71, "全社");
        await idp.Pleasanter.AddGroupAsync(1, 72, "無効グループ", disabled: true);
        var userId = await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, name: "山田 太郎", mail: loginId + "@example.com", deptId: 7);
        await idp.Pleasanter.AddGroupMemberAsync(70, userId: userId);
        await idp.Pleasanter.AddGroupMemberAsync(71, deptId: 7);
        await idp.Pleasanter.AddGroupMemberAsync(72, userId: userId);

        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();
        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, Scope), loginId);

        var query = QueryHelpers.ParseQuery(redirect.Query);
        Assert.Equal("st-1", query["state"].ToString());
        Assert.Equal(idp.Issuer, query["iss"].ToString());
        Assert.StartsWith(E2E.RedirectUri, redirect.ToString(), StringComparison.Ordinal);

        using var tokenResponse = await E2E.ExchangeCodeAsync(browser, clientId, secret, E2E.CodeOf(redirect), verifier);
        var text = await tokenResponse.Content.ReadAsStringAsync();
        Assert.True(tokenResponse.StatusCode == HttpStatusCode.OK, text);
        Assert.Contains("no-store", tokenResponse.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var tokens = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        Assert.Equal("Bearer", tokens["token_type"]!.GetValue<string>());
        Assert.NotNull(tokens["refresh_token"]);

        var idToken = await E2E.ValidateAsync(browser, tokens["id_token"]!.GetValue<string>(), clientId);
        Assert.Equal(userId.ToString(), idToken.Subject);
        Assert.Equal("nonce-1", idToken.GetPayloadValue<string>("nonce"));
        Assert.Equal("山田 太郎", idToken.GetPayloadValue<string>("name"));
        Assert.Equal(loginId + "@example.com", idToken.GetPayloadValue<string>("email"));
        Assert.Equal(["pwd"], idToken.GetPayloadValue<string[]>("amr"));
        Assert.Equal(OidcTokenAtHash(tokens["access_token"]!.GetValue<string>()), idToken.GetPayloadValue<string>("at_hash"));

        // UserInfo
        using var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, "/oidc/userinfo");
        userInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens["access_token"]!.GetValue<string>());
        using var userInfoResponse = await browser.Http.SendAsync(userInfoRequest);
        Assert.Equal(HttpStatusCode.OK, userInfoResponse.StatusCode);
        var userInfo = await E2E.ReadAsync(userInfoResponse);
        Assert.Equal(userId.ToString(), userInfo["sub"]!.GetValue<string>());
        Assert.Equal("営業部", userInfo["dept"]!.GetValue<string>());
        var groups = userInfo["groups"]!.AsArray().Select(node => node!.GetValue<string>()).ToHashSet();

        // 本人が直接のメンバーのグループと、所属部署がメンバーのグループ。無効なグループは含めない
        Assert.Equal(new HashSet<string> { "営業チーム", "全社" }, groups);

        // **秘密の値はどの応答にも載らない**
        var all = text + await userInfoResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretKey", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 公開クライアントはPKCEが必須でシークレットなしで交換できる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin, clientType: "public");
        Assert.Null(secret);

        var loginId = E2E.Unique("pub");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        // PKCE 無しの認可要求は、リダイレクト先へエラーを返す
        using var noPkce = await browser.GetAsync(
            "/oidc/authorize?response_type=code&client_id=" + clientId + "&redirect_uri=" + Uri.EscapeDataString(E2E.RedirectUri) + "&scope=openid&state=s");
        Assert.Equal(HttpStatusCode.Redirect, noPkce.StatusCode);
        Assert.Equal("invalid_request", QueryHelpers.ParseQuery(noPkce.Headers.Location!.Query)["error"].ToString());

        var (verifier, challenge) = E2E.NewPkce();
        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, "openid"), loginId);
        using var response = await E2E.ExchangeCodeAsync(browser, clientId, null, E2E.CodeOf(redirect), verifier);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 登録されていないリダイレクトURIへは返さずエラー画面にする()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin);
        var (_, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        foreach (var evil in new[]
        {
            "https://evil.example.com/callback",
            "https://app.example.com/callback/",
            "https://app.example.com/callback?x=1",
            "https://app.example.com.evil.test/callback",
        })
        {
            using var response = await browser.GetAsync(E2E.AuthorizeUrl(clientId, challenge, redirectUri: evil));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }

        // 存在しないクライアントも同じ
        using var unknown = await browser.GetAsync(E2E.AuthorizeUrl("no-such-client", challenge));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task 許されていないスコープはエラーで返る()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin, scopes: ["openid", "profile"]);
        var (_, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        using var response = await browser.GetAsync(E2E.AuthorizeUrl(clientId, challenge, "openid groups"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("invalid_scope", QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString());
    }

    [Fact]
    public async Task 同じパラメータが二重に来た要求は受け付けない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin);
        var (_, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        using var response = await browser.GetAsync(E2E.AuthorizeUrl(clientId, challenge) + "&client_id=other");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 認可コードは一度しか使えず再利用するとリフレッシュトークンも止まる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("replay");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, Scope), loginId);
        var code = E2E.CodeOf(redirect);

        using var first = await E2E.ExchangeCodeAsync(browser, clientId, secret, code, verifier);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var refresh = (await E2E.ReadAsync(first))["refresh_token"]!.GetValue<string>();

        using var second = await E2E.ExchangeCodeAsync(browser, clientId, secret, code, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("invalid_grant", (await E2E.ReadAsync(second))["error"]!.GetValue<string>());

        // **使い回しを検知したら、そのコードから出たトークンを全て失効させる**
        using var afterReplay = await E2E.RefreshAsync(browser, clientId, secret, refresh);
        Assert.Equal(HttpStatusCode.BadRequest, afterReplay.StatusCode);
    }

    [Fact]
    public async Task PKCEの検証値が違えば交換できない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("pkce");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (_, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge), loginId);

        using var response = await E2E.ExchangeCodeAsync(browser, clientId, secret, E2E.CodeOf(redirect), E2E.NewPkce().Verifier);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", (await E2E.ReadAsync(response))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task 別のクライアントのコードやシークレット違いは通らない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var (otherId, otherSecret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("cross");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge), loginId);
        var code = E2E.CodeOf(redirect);

        using var wrongSecret = await E2E.ExchangeCodeAsync(browser, clientId, secret + "x", code, verifier);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal("invalid_client", (await E2E.ReadAsync(wrongSecret))["error"]!.GetValue<string>());

        // 別のクライアントが、盗んだコードを自分の資格情報で交換しようとする
        using var stolen = await E2E.ExchangeCodeAsync(browser, otherId, otherSecret, code, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);

        // コードは上の失敗で使われた扱いになる（盗まれたコードを持ち主が使えなくなる側へ倒す）
        using var owner = await E2E.ExchangeCodeAsync(browser, clientId, secret, code, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, owner.StatusCode);
    }

    [Fact]
    public async Task リフレッシュトークンは使うたびに替わり古いものを使うと系統ごと止まる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("refresh");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, Scope), loginId);
        using var exchange = await E2E.ExchangeCodeAsync(browser, clientId, secret, E2E.CodeOf(redirect), verifier);
        var refresh1 = (await E2E.ReadAsync(exchange))["refresh_token"]!.GetValue<string>();

        using var rotated = await E2E.RefreshAsync(browser, clientId, secret, refresh1);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var body = await E2E.ReadAsync(rotated);
        var refresh2 = body["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(refresh1, refresh2);

        // 更新で出た ID トークンには nonce が載らない（OIDC Core 12.2）
        var idToken = await E2E.ValidateAsync(browser, body["id_token"]!.GetValue<string>(), clientId);
        Assert.False(idToken.TryGetPayloadValue<string>("nonce", out _));

        // 古いトークンを使い回す → 拒否され、新しい方も止まる
        using var replay = await E2E.RefreshAsync(browser, clientId, secret, refresh1);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        using var afterReplay = await E2E.RefreshAsync(browser, clientId, secret, refresh2);
        Assert.Equal(HttpStatusCode.BadRequest, afterReplay.StatusCode);
    }

    [Fact]
    public async Task リフレッシュ時にスコープを増やすことはできない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("scope");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, "openid offline_access"), loginId);
        using var exchange = await E2E.ExchangeCodeAsync(browser, clientId, secret, E2E.CodeOf(redirect), verifier);
        var refresh = (await E2E.ReadAsync(exchange))["refresh_token"]!.GetValue<string>();

        using var request = E2E.TokenRequest(clientId, secret,
        [
            new("grant_type", "refresh_token"), new("refresh_token", refresh), new("scope", "openid groups"),
        ]);
        using var response = await browser.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_scope", (await E2E.ReadAsync(response))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ログインしていなければprompt_noneはlogin_requiredを返す()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin);
        var (_, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        using var response = await browser.GetAsync(E2E.AuthorizeUrl(clientId, challenge, extra: "&prompt=none"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("login_required", QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString());
    }

    [Fact]
    public async Task シングルサインオンで二つ目のクライアントは再ログインなしで通る()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (firstId, _) = await E2E.CreateClientAsync(admin);
        var (secondId, _) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("sso");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(firstId, E2E.NewPkce().Challenge), loginId);

        using var second = await browser.GetAsync(E2E.AuthorizeUrl(secondId, E2E.NewPkce().Challenge));
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith(E2E.RedirectUri, second.Headers.Location!.ToString(), StringComparison.Ordinal);

        // prompt=login なら再認証を求める
        using var forced = await browser.GetAsync(E2E.AuthorizeUrl(secondId, E2E.NewPkce().Challenge, extra: "&prompt=login"));
        Assert.StartsWith("/login", forced.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 許可していないテナントのユーザには発行しない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin, tenants: [2]);
        var loginId = E2E.Unique("tenant");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, tenantId: 1);
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, E2E.NewPkce().Challenge), loginId);

        Assert.Equal("access_denied", QueryHelpers.ParseQuery(redirect.Query)["error"].ToString());
    }

    [Fact]
    public async Task ログアウトするとそのセッションのアクセストークンも使えなくなる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("logout");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, Scope), loginId);
        using var exchange = await E2E.ExchangeCodeAsync(browser, clientId, secret, E2E.CodeOf(redirect), verifier);
        var tokens = await E2E.ReadAsync(exchange);
        var accessToken = tokens["access_token"]!.GetValue<string>();
        var idToken = tokens["id_token"]!.GetValue<string>();

        async Task<HttpStatusCode> UserInfoAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/oidc/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await browser.Http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await UserInfoAsync());

        // 登録していない戻り先は拒否
        using var badRedirect = await browser.GetAsync(
            "/oidc/logout?id_token_hint=" + Uri.EscapeDataString(idToken) + "&post_logout_redirect_uri=" + Uri.EscapeDataString("https://evil.example.com/"));
        Assert.Equal(HttpStatusCode.BadRequest, badRedirect.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await UserInfoAsync());

        using var logout = await browser.GetAsync(
            "/oidc/logout?id_token_hint=" + Uri.EscapeDataString(idToken)
            + "&post_logout_redirect_uri=" + Uri.EscapeDataString("https://app.example.com/signed-out") + "&state=bye");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("https://app.example.com/signed-out?state=bye", logout.Headers.Location!.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, await UserInfoAsync());

        // リフレッシュトークンも止まっている
        using var refresh = await E2E.RefreshAsync(browser, clientId, secret, tokens["refresh_token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);

        // セッションが無いので、再び認可要求を出すとログインを求められる
        using var again = await browser.GetAsync(E2E.AuthorizeUrl(clientId, E2E.NewPkce().Challenge));
        Assert.StartsWith("/login", again.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ヒントの無いログアウトは確認画面を挟みCSRFトークンが要る()
    {
        var loginId = E2E.Unique("confirm");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();
        await browser.LoginAsync(loginId, E2E.Password);

        using var page = await browser.GetAsync("/oidc/logout");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        // 他所のページから POST された想定（CSRF トークンが無い）
        using var forged = await browser.PostFormAsync("/oidc/logout", []);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var stillIn = await browser.GetAsync("/api/admin/me");
        Assert.NotEqual(HttpStatusCode.Unauthorized, stillIn.StatusCode);

        using var ok = await browser.PostFormAsync("/oidc/logout", [new("csrf", Browser.Hidden(html, "csrf"))]);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var after = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task トークンの失効要求はリフレッシュトークンを止め存在を教えない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("revoke");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, Scope), loginId);
        using var exchange = await E2E.ExchangeCodeAsync(browser, clientId, secret, E2E.CodeOf(redirect), verifier);
        var refresh = (await E2E.ReadAsync(exchange))["refresh_token"]!.GetValue<string>();

        using var unknown = E2E.TokenRequest(clientId, secret, [new("token", "no-such-token")]);
        unknown.RequestUri = new Uri("/oidc/revocation", UriKind.Relative);
        using var unknownResponse = await browser.Http.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.OK, unknownResponse.StatusCode);

        using var revoke = E2E.TokenRequest(clientId, secret, [new("token", refresh)]);
        revoke.RequestUri = new Uri("/oidc/revocation", UriKind.Relative);
        using var revokeResponse = await browser.Http.SendAsync(revoke);
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        using var use = await E2E.RefreshAsync(browser, clientId, secret, refresh);
        Assert.Equal(HttpStatusCode.BadRequest, use.StatusCode);
    }

    [Fact]
    public async Task 署名鍵をローテーションしても発行済みのIDトークンは検証できる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("rotate");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        var (verifier, challenge) = E2E.NewPkce();
        using var browser = idp.NewBrowser();

        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge, "openid"), loginId);
        using var exchange = await E2E.ExchangeCodeAsync(browser, clientId, secret, E2E.CodeOf(redirect), verifier);
        var oldIdToken = (await E2E.ReadAsync(exchange))["id_token"]!.GetValue<string>();
        var oldKid = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(oldIdToken).Kid;

        using var rotate = await E2E.AdminPostAsync(admin, "/api/admin/signing-keys/rotate", new { purpose = "oidc" });
        Assert.Equal(HttpStatusCode.Created, rotate.StatusCode);
        var newKid = (await E2E.ReadAsync(rotate))["keyId"]!.GetValue<string>();
        Assert.NotEqual(oldKid, newKid);

        // 鍵のキャッシュは最長 30 秒。**旧鍵は公開し続ける**ので、どちらの時点でも古いトークンは検証できる
        await E2E.ValidateAsync(browser, oldIdToken, clientId, validateLifetime: false);

        using var jwks = await browser.GetAsync("/oidc/jwks");
        var kids = (await E2E.ReadAsync(jwks))["keys"]!.AsArray().Select(key => key!["kid"]!.GetValue<string>()).ToArray();
        Assert.Contains(oldKid, kids);

        // active は削除できない。旧鍵（retired）は消せる
        using var deleteActive = await E2E.AdminSendAsync(admin, HttpMethod.Delete, "/api/admin/signing-keys/" + newKid);
        Assert.Equal(HttpStatusCode.Conflict, deleteActive.StatusCode);
        using var deleteOld = await E2E.AdminSendAsync(admin, HttpMethod.Delete, "/api/admin/signing-keys/" + oldKid);
        Assert.Equal(HttpStatusCode.NoContent, deleteOld.StatusCode);
    }

    private static string OidcTokenAtHash(string accessToken) =>
        Pkce.Base64Url(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(accessToken))[..16]);
}
