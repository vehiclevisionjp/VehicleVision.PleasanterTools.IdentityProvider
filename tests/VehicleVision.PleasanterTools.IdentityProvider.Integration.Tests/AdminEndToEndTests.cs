using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>管理 API（<c>_documents/管理API仕様.md</c>）を、動いている IdP へ HTTP で当てて確かめる。</summary>
public class AdminEndToEndTests(IdpProcess idp) : IClassFixture<IdpProcess>
{
    // ---- 認証・権限・CSRF ---------------------------------------------------

    [Fact]
    public async Task ログインしていなければ401()
    {
        using var browser = idp.NewBrowser();

        foreach (var path in new[] { "/api/admin/me", "/api/admin/status", "/api/admin/oidc-clients", "/api/admin/audit-logs" })
        {
            using var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("unauthorized", (await E2E.ReadAsync(response))["error"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task 管理者でないユーザは403()
    {
        var loginId = E2E.Unique("plain");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();
        await browser.LoginAsync(loginId, E2E.Password);

        using var response = await browser.GetAsync("/api/admin/oidc-clients");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 既定ではテナント管理者だけでは入れずシステム管理者が必要()
    {
        var loginId = E2E.Unique("tenantmgr");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, tenantManager: true);
        using var browser = idp.NewBrowser();
        await browser.LoginAsync(loginId, E2E.Password);

        using var response = await browser.GetAsync("/api/admin/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task 管理者でなくなった人は既存のセッションでも入れない()
    {
        var loginId = E2E.Unique("demoted");
        var userId = await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true);
        using var browser = idp.NewBrowser();
        await browser.LoginAsync(loginId, E2E.Password);
        using (var before = await browser.GetAsync("/api/admin/me"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        // Pleasanter 側で管理者権限を外した（IdP のセッションは残っている）
        await using (var connection = idp.Pleasanter.Factory.Create())
        {
            await connection.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(connection, "UPDATE \"Users\" SET \"ServiceManager\" = 0 WHERE \"UserId\" = @userId", new { userId });
        }

        using var after = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task 無効化された人は既存のセッションでも入れない()
    {
        var loginId = E2E.Unique("fired");
        var userId = await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true);
        using var browser = idp.NewBrowser();
        await browser.LoginAsync(loginId, E2E.Password);

        await using (var connection = idp.Pleasanter.Factory.Create())
        {
            await connection.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(connection, "UPDATE \"Users\" SET \"Disabled\" = 1 WHERE \"UserId\" = @userId", new { userId });
        }

        using var response = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 更新系はCSRF用のヘッダとオリジンが要る()
    {
        using var admin = await E2E.AdminAsync(idp);
        var body = new
        {
            displayName = "CSRF",
            clientType = "confidential",
            redirectUris = new[] { E2E.RedirectUri },
            allowedScopes = new[] { "openid" },
        };

        using var noHeader = await admin.Http.PostAsJsonAsync("/api/admin/oidc-clients", body);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/admin/oidc-clients") { Content = JsonContent.Create(body) };
        crossOrigin.Headers.Add("X-IdP-Admin", "1");
        crossOrigin.Headers.Add("Origin", "https://evil.example.com");
        using var crossResponse = await admin.Http.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, crossResponse.StatusCode);

        using var ok = await E2E.AdminPostAsync(admin, "/api/admin/oidc-clients", body);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task ログアウトするとAPIが使えなくなる()
    {
        using var admin = await E2E.AdminAsync(idp);

        using var logout = await E2E.AdminPostAsync(admin, "/api/admin/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var me = await admin.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // ---- 状態 ------------------------------------------------------------

    [Fact]
    public async Task 状態にエンドポイントと接続の結果が出る()
    {
        using var admin = await E2E.AdminAsync(idp);

        using var response = await admin.GetAsync("/api/admin/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await E2E.ReadAsync(response);
        Assert.Equal(idp.Issuer, status["issuer"]!.GetValue<string>());
        Assert.Equal(idp.Issuer + "/oidc/token", status["endpoints"]!["token"]!.GetValue<string>());
        Assert.Equal(idp.Issuer + "/saml/metadata", status["endpoints"]!["samlMetadata"]!.GetValue<string>());
        Assert.Equal("Sqlite", status["database"]!["provider"]!.GetValue<string>());
        Assert.True(status["database"]!["latestVersion"]!.GetValue<long>() >= 1);
        Assert.Equal(0, status["database"]!["pendingCount"]!.GetValue<int>());
        Assert.True(status["pleasanter"]!["connected"]!.GetValue<bool>());
        Assert.True(status["pleasanter"]!["userCount"]!.GetValue<long>() >= 1);
        Assert.Equal("Auto", status["totp"]!["mode"]!.GetValue<string>());

        // **接続文字列やパスワードを含めない**
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Data Source", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(IdpProcess.SecretKey, text, StringComparison.Ordinal);
    }

    // ---- OIDC クライアント -------------------------------------------------

    [Fact]
    public async Task クライアントの作成と取得と更新と削除ができる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var clientId = E2E.Unique("crud");

        using var create = await E2E.AdminPostAsync(admin, "/api/admin/oidc-clients", new
        {
            clientId,
            displayName = "  CRUD アプリ  ",
            clientType = "confidential",
            redirectUris = new[] { E2E.RedirectUri, E2E.RedirectUri },
            allowedScopes = new[] { "openid", "email" },
            allowedTenantIds = new[] { 1, 1, 2 },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await E2E.ReadAsync(create);
        Assert.False(string.IsNullOrEmpty(created["clientSecret"]!.GetValue<string>()));
        Assert.Equal("CRUD アプリ", created["client"]!["displayName"]!.GetValue<string>());
        Assert.Single(created["client"]!["redirectUris"]!.AsArray());
        Assert.Equal(2, created["client"]!["allowedTenantIds"]!.AsArray().Count);
        Assert.True(created["client"]!["hasSecret"]!.GetValue<bool>());

        // **シークレットは 1 度しか返さない**（取得 API の本文には出ない）
        using var get = await admin.GetAsync("/api/admin/oidc-clients/" + clientId);
        var getText = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain(created["clientSecret"]!.GetValue<string>(), getText, StringComparison.Ordinal);
        Assert.DoesNotContain("secretHash", getText, StringComparison.OrdinalIgnoreCase);

        using var update = await E2E.AdminSendAsync(admin, HttpMethod.Put, "/api/admin/oidc-clients/" + clientId, new
        {
            displayName = "更新後",
            clientType = "public",
            redirectUris = new[] { E2E.RedirectUri, "https://app.example.com/other" },
            allowedScopes = new[] { "openid" },
            enabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await E2E.ReadAsync(update);
        Assert.Equal("更新後", updated["displayName"]!.GetValue<string>());
        Assert.Equal("confidential", updated["clientType"]!.GetValue<string>());
        Assert.False(updated["enabled"]!.GetValue<bool>());

        using var list = await admin.GetAsync("/api/admin/oidc-clients");
        Assert.Contains((await E2E.ReadAsync(list)).AsArray(), node => node!["clientId"]!.GetValue<string>() == clientId);

        using var delete = await E2E.AdminSendAsync(admin, HttpMethod.Delete, "/api/admin/oidc-clients/" + clientId);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var gone = await admin.GetAsync("/api/admin/oidc-clients/" + clientId);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task クライアントIDを省略すると生成され重複は409になる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var body = new
        {
            displayName = "生成",
            clientType = "public",
            redirectUris = new[] { E2E.RedirectUri },
            allowedScopes = new[] { "openid" },
        };

        using var first = await E2E.AdminPostAsync(admin, "/api/admin/oidc-clients", body);
        var created = await E2E.ReadAsync(first);
        var clientId = created["client"]!["clientId"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(clientId));
        Assert.Null(created["clientSecret"]);

        using var duplicate = await E2E.AdminPostAsync(admin, "/api/admin/oidc-clients", new
        {
            clientId,
            displayName = "重複",
            clientType = "public",
            redirectUris = new[] { E2E.RedirectUri },
            allowedScopes = new[] { "openid" },
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Theory]
    [InlineData("http://app.example.com/cb")]
    [InlineData("https://app.example.com/*")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a uri")]
    public async Task 登録できないリダイレクトURIは400(string uri)
    {
        using var admin = await E2E.AdminAsync(idp);

        using var response = await E2E.AdminPostAsync(admin, "/api/admin/oidc-clients", new
        {
            displayName = "bad",
            clientType = "confidential",
            redirectUris = new[] { uri },
            allowedScopes = new[] { "openid" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", (await E2E.ReadAsync(response))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task シークレットを再発行すると古いシークレットは使えなくなる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, oldSecret) = await E2E.CreateClientAsync(admin);
        var loginId = E2E.Unique("rotate-secret");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        using var reissue = await E2E.AdminPostAsync(admin, $"/api/admin/oidc-clients/{clientId}/secret");
        Assert.Equal(HttpStatusCode.OK, reissue.StatusCode);
        var newSecret = (await E2E.ReadAsync(reissue))["clientSecret"]!.GetValue<string>();
        Assert.NotEqual(oldSecret, newSecret);

        var (verifier, challenge) = E2E.NewPkce();
        var redirect = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge), loginId);
        using var withOld = await E2E.ExchangeCodeAsync(browser, clientId, oldSecret, E2E.CodeOf(redirect), verifier);
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);

        var (verifier2, challenge2) = E2E.NewPkce();
        var redirect2 = await E2E.AuthorizeAsync(browser, E2E.AuthorizeUrl(clientId, challenge2), loginId);
        using var withNew = await E2E.ExchangeCodeAsync(browser, clientId, newSecret, E2E.CodeOf(redirect2), verifier2);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    [Fact]
    public async Task 公開クライアントのシークレットは再発行できない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin, clientType: "public");

        using var response = await E2E.AdminPostAsync(admin, $"/api/admin/oidc-clients/{clientId}/secret");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 無効にしたクライアントは認可を受け付けない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var (clientId, _) = await E2E.CreateClientAsync(admin);
        using var disable = await E2E.AdminSendAsync(admin, HttpMethod.Put, "/api/admin/oidc-clients/" + clientId, new
        {
            displayName = "無効",
            redirectUris = new[] { E2E.RedirectUri },
            allowedScopes = new[] { "openid" },
            enabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        using var browser = idp.NewBrowser();

        using var response = await browser.GetAsync(E2E.AuthorizeUrl(clientId, E2E.NewPkce().Challenge));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- セッション・監査 ---------------------------------------------------

    [Fact]
    public async Task セッション一覧に自分が出て他のセッションを失効できる()
    {
        using var admin = await E2E.AdminAsync(idp);
        var victimLogin = E2E.Unique("victim");
        await idp.Pleasanter.AddUserAsync(victimLogin, E2E.Password, serviceManager: true);
        using var victim = idp.NewBrowser();
        await victim.LoginAsync(victimLogin, E2E.Password);

        using var list = await admin.GetAsync("/api/admin/sessions?loginId=" + victimLogin);
        var sessions = (await E2E.ReadAsync(list)).AsArray();
        var target = Assert.Single(sessions);
        Assert.False(target!["current"]!.GetValue<bool>());

        using var all = await admin.GetAsync("/api/admin/sessions?limit=200");
        Assert.Contains((await E2E.ReadAsync(all)).AsArray(), node => node!["current"]!.GetValue<bool>());

        using var revoke = await E2E.AdminSendAsync(admin, HttpMethod.Delete, "/api/admin/sessions/" + target["sessionId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var victimMe = await victim.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, victimMe.StatusCode);
    }

    [Fact]
    public async Task 監査ログに認証と管理操作が残り秘密は入らない()
    {
        var loginId = E2E.Unique("audited");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true);
        using var admin = idp.NewBrowser();
        using (var failed = await admin.SubmitPasswordAsync(loginId, "wrong-password-xyz")) { }
        await admin.LoginAsync(loginId, E2E.Password);
        var (clientId, secret) = await E2E.CreateClientAsync(admin);

        using var response = await admin.GetAsync("/api/admin/audit-logs?actor=" + loginId + "&limit=200");
        var text = await response.Content.ReadAsStringAsync();
        var rows = JsonNode.Parse(text)!.AsArray();

        var actions = rows.Select(row => row!["action"]!.GetValue<string>()).ToArray();
        Assert.Contains("auth.login.failure", actions);
        Assert.Contains("auth.login.success", actions);
        Assert.Contains(actions, action => action.StartsWith("POST /api/admin/oidc-clients", StringComparison.Ordinal));
        var created = rows.First(row => row!["action"]!.GetValue<string>().StartsWith("POST /api/admin/oidc-clients", StringComparison.Ordinal))!;
        Assert.Equal(clientId, created["targetId"]!.GetValue<string>());
        Assert.Equal(201, created["statusCode"]!.GetValue<int>());

        // **パスワード・シークレットは監査ログへ入らない**
        Assert.DoesNotContain("wrong-password-xyz", text, StringComparison.Ordinal);
        Assert.DoesNotContain(E2E.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret!, text, StringComparison.Ordinal);

        using var failedOnly = await admin.GetAsync("/api/admin/audit-logs?actor=" + loginId + "&failedOnly=true");
        var failedRows = (await E2E.ReadAsync(failedOnly)).AsArray();
        Assert.NotEmpty(failedRows);
        Assert.All(failedRows, row => Assert.True(row!["statusCode"]!.GetValue<int>() >= 400));
    }

    // ---- ユーザ照会 -------------------------------------------------------

    [Fact]
    public async Task ユーザ照会はTOTPを求めるかを返し秘密は返さない()
    {
        using var admin = await E2E.AdminAsync(idp);
        var loginId = E2E.Unique("lookup");
        var secret = E2E.NewTotpSecret();
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: secret, enableSecretKey: true, mail: loginId + "@example.com");

        using var response = await admin.GetAsync("/api/admin/pleasanter/users/lookup?loginId=" + loginId);

        var text = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(text)!;
        Assert.True(body["found"]!.GetValue<bool>());
        Assert.True(body["hasTotp"]!.GetValue<bool>());
        Assert.True(body["totpRequired"]!.GetValue<bool>());
        Assert.Contains(loginId + "@example.com", body["mailAddresses"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Core.Pleasanter.PleasanterPassword.Hash(E2E.Password), text, StringComparison.Ordinal);

        using var missing = await admin.GetAsync("/api/admin/pleasanter/users/lookup?loginId=" + E2E.Unique("nobody"));
        Assert.False((await E2E.ReadAsync(missing))["found"]!.GetValue<bool>());
    }

    // ---- 署名鍵 -----------------------------------------------------------

    [Fact]
    public async Task 署名鍵の一覧と証明書が取れ秘密鍵は出ない()
    {
        using var admin = await E2E.AdminAsync(idp);

        using var list = await admin.GetAsync("/api/admin/signing-keys");
        var text = await list.Content.ReadAsStringAsync();
        var keys = JsonNode.Parse(text)!.AsArray();

        Assert.Contains(keys, key => key!["purpose"]!.GetValue<string>() == "oidc" && key["status"]!.GetValue<string>() == "active");
        Assert.Contains(keys, key => key!["purpose"]!.GetValue<string>() == "saml" && key["status"]!.GetValue<string>() == "active");
        Assert.DoesNotContain("PRIVATE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", text, StringComparison.OrdinalIgnoreCase);

        var samlKey = keys.First(key => key!["purpose"]!.GetValue<string>() == "saml")!;
        using var certificate = await admin.GetAsync("/api/admin/signing-keys/" + samlKey["keyId"]!.GetValue<string>() + "/certificate");
        var pem = await certificate.Content.ReadAsStringAsync();
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", pem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 不正な用途のローテーションは400()
    {
        using var admin = await E2E.AdminAsync(idp);

        using var response = await E2E.AdminPostAsync(admin, "/api/admin/signing-keys/rotate", new { purpose = "other" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- 管理画面の入口 ---------------------------------------------------

    [Fact]
    public async Task 管理画面のパスは配信され素のHTMLのパスは配らない()
    {
        using var browser = idp.NewBrowser();

        // フロントエンドを組み立てていない環境（wwwroot が無い）では 404。組み立て済みなら 200 で印が埋まっている
        using var admin = await browser.GetAsync("/admin");
        Assert.True(admin.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);
        if (admin.StatusCode == HttpStatusCode.OK)
        {
            var html = await admin.Content.ReadAsStringAsync();
            Assert.DoesNotContain("__IDP_BASE_PATH__", html, StringComparison.Ordinal);
            Assert.DoesNotContain("__IDP_ADMIN_PATH__", html, StringComparison.Ordinal);
        }

        using var raw = await browser.GetAsync("/admin.html");
        Assert.Equal(HttpStatusCode.NotFound, raw.StatusCode);
    }

    [Fact]
    public async Task ルートを開くと管理画面へ案内する()
    {
        using var browser = idp.NewBrowser();

        using var response = await browser.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task 生存確認と受付可否が応答する()
    {
        using var browser = idp.NewBrowser();

        using var health = await browser.GetAsync("/healthz");
        using var ready = await browser.GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}
