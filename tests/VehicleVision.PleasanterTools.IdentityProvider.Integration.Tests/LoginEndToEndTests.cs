using System.Net;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>パスワード・TOTP・締め出しを、動いている IdP へ HTTP で当てて確かめる。</summary>
/// <remarks>
/// **Pleasanter の DB は読み取り専用**という前提も、ここで確かめる
/// （ログイン失敗を重ねても、Pleasanter 相当の表の <c>Lockout</c> は変わらない）。
/// </remarks>
public class LoginEndToEndTests(IdpProcess idp) : IClassFixture<IdpProcess>
{
    [Fact]
    public async Task 正しいパスワードで入れて戻り先へ進む()
    {
        var loginId = E2E.Unique("ok");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true);
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId, E2E.Password, "/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
        using var me = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await E2E.ReadAsync(me);
        Assert.Equal(loginId, body["loginId"]!.GetValue<string>());
        Assert.Equal(["pwd"], body["authMethods"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task ログインIDの大文字小文字は区別しない()
    {
        var loginId = E2E.Unique("Case");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId.ToUpperInvariant(), E2E.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task 間違ったパスワードと存在しないユーザは同じ応答になる()
    {
        var loginId = E2E.Unique("known");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        using var wrong = await browser.SubmitPasswordAsync(loginId, "wrong-password");
        var unknownId = E2E.Unique("nobody");
        using var unknown = await browser.SubmitPasswordAsync(unknownId, "wrong-password");

        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        var wrongHtml = await wrong.Content.ReadAsStringAsync();
        var unknownHtml = await unknown.Content.ReadAsStringAsync();
        Assert.Contains("正しくありません", wrongHtml, StringComparison.Ordinal);
        Assert.Contains("正しくありません", unknownHtml, StringComparison.Ordinal);

        // **応答から、ログイン ID が実在するかを読み取れない**（CSRF トークンと入力の反映を除いて同じ）
        string Normalize(string html, string id) =>
            System.Text.RegularExpressions.Regex.Replace(html.Replace(id, "ID"), "name=\"csrf\" value=\"[^\"]*\"", "csrf");
        Assert.Equal(Normalize(wrongHtml, loginId), Normalize(unknownHtml, unknownId));

        using var me = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task 失敗が続くと締め出し正しいパスワードでも入れない()
    {
        var loginId = E2E.Unique("lock");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        for (var i = 0; i < 4; i++)
        {
            using var attempt = await browser.SubmitPasswordAsync(loginId, "wrong-" + i);
            Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        }

        // 5 回目で締め出し
        using var fifth = await browser.SubmitPasswordAsync(loginId, "wrong-5");
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);

        using var correct = await browser.SubmitPasswordAsync(loginId, E2E.Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);

        // **存在しないログイン ID も同じ回数で締め出す**（実在の見分けを付けさせない）
        var ghost = E2E.Unique("ghost");
        for (var i = 0; i < 4; i++)
        {
            using var attempt = await browser.SubmitPasswordAsync(ghost, "x");
            Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        }

        using var ghostFifth = await browser.SubmitPasswordAsync(ghost, "x");
        Assert.Equal(HttpStatusCode.TooManyRequests, ghostFifth.StatusCode);
    }

    [Fact]
    public async Task 成功すると失敗の記録は消える()
    {
        var loginId = E2E.Unique("reset");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        for (var round = 0; round < 3; round++)
        {
            using var browser = idp.NewBrowser();
            for (var i = 0; i < 4; i++)
            {
                using var attempt = await browser.SubmitPasswordAsync(loginId, "wrong");
                Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
            }

            using var ok = await browser.SubmitPasswordAsync(loginId, E2E.Password);
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        }
    }

    [Fact]
    public async Task Pleasanterの表へは何も書き込まない()
    {
        var loginId = E2E.Unique("readonly");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();
        for (var i = 0; i < 6; i++)
        {
            using var attempt = await browser.SubmitPasswordAsync(loginId, "wrong");
        }

        await using var connection = idp.Pleasanter.Factory.Create();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Lockout\", \"LastLoginTime\" FROM \"Users\" WHERE \"LoginId\" = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = loginId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, Convert.ToInt64(reader.GetValue(0)));
        Assert.True(await reader.IsDBNullAsync(1));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("lockout")]
    public async Task 無効またはロックアウト中のユーザは入れない(string kind)
    {
        var loginId = E2E.Unique(kind);
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, disabled: kind == "disabled", lockout: kind == "lockout");
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId, E2E.Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var me = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task パスワードの有効期限が切れていれば入れずPleasanterでの変更を案内する()
    {
        var loginId = E2E.Unique("expired");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, passwordExpirationTime: DateTime.Now.AddDays(-1));
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId, E2E.Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("有効期限", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task パスワードが無い外部認証のユーザは入れない()
    {
        var loginId = E2E.Unique("nopass");
        await idp.Pleasanter.AddUserAsync(loginId, "ignored", passwordHash: "");
        using var browser = idp.NewBrowser();

        using var empty = await browser.SubmitPasswordAsync(loginId, "");
        using var guess = await browser.SubmitPasswordAsync(loginId, "anything");

        Assert.NotEqual(HttpStatusCode.Redirect, empty.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, guess.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("//evil.example.com/")]
    [InlineData("/\\evil.example.com/")]
    [InlineData("javascript:alert(1)")]
    public async Task 戻り先に外部のURLは使えない(string returnUrl)
    {
        var loginId = E2E.Unique("redir");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId, E2E.Password, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task CSRFトークンが無いか他所のオリジンからのPOSTは拒否する()
    {
        var loginId = E2E.Unique("csrf");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        using var noToken = await browser.PostFormAsync("/login",
            [new("loginId", loginId), new("password", E2E.Password), new("returnUrl", "/admin")]);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var (csrf, returnUrl) = await browser.OpenLoginAsync("/admin");
        using var wrongToken = await browser.PostFormAsync("/login",
            [new("loginId", loginId), new("password", E2E.Password), new("returnUrl", returnUrl), new("csrf", csrf + "x")]);
        Assert.Equal(HttpStatusCode.BadRequest, wrongToken.StatusCode);

        // 正しいトークンでも、Origin が他所なら拒否
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(
            [
                new("loginId", loginId), new("password", E2E.Password), new("returnUrl", returnUrl), new("csrf", csrf),
            ]),
        };
        crossOrigin.Headers.Add("Origin", "https://evil.example.com");
        using var crossResponse = await browser.Http.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.BadRequest, crossResponse.StatusCode);

        using var me = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task ログイン画面は安全なヘッダ付きで返りキャッシュされない()
    {
        using var browser = idp.NewBrowser();

        using var response = await browser.GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", string.Join(';', response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Equal("nosniff", string.Join(',', response.Headers.GetValues("X-Content-Type-Options")));

        // `no-referrer` だと、ブラウザは同じオリジンへのフォーム送信にも `Origin: null` を送り、CSRF の検査で全て断られる
        Assert.Equal("same-origin", string.Join(',', response.Headers.GetValues("Referrer-Policy")));

        // CSS / JS は別ファイルで配る（インラインのスタイルとスクリプトを使わない）
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<style", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        using var css = await browser.GetAsync("/idp-assets/login.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
    }

    [Fact]
    public async Task ログインのたびに新しいセッションIDを発行する()
    {
        var loginId = E2E.Unique("fixation");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, serviceManager: true);
        using var browser = idp.NewBrowser();

        await browser.LoginAsync(loginId, E2E.Password);
        var first = browser.Cookies.GetCookies(idp.BaseAddress)[SsoSessionCookie]!.Value;

        using (var logout = await E2E.AdminPostAsync(browser, "/api/admin/logout")) { }
        await browser.LoginAsync(loginId, E2E.Password);
        var second = browser.Cookies.GetCookies(idp.BaseAddress)[SsoSessionCookie]!.Value;

        Assert.NotEqual(first, second);

        // 古い cookie では入れない（ログアウトで失効している）
        var stale = idp.NewBrowser();
        stale.Cookies.Add(idp.BaseAddress, new Cookie(SsoSessionCookie, first));
        using var response = await stale.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private const string SsoSessionCookie = "idp_session";

    [Fact]
    public async Task 署名の合わないセッションcookieでは入れない()
    {
        using var browser = idp.NewBrowser();
        browser.Cookies.Add(idp.BaseAddress, new Cookie(SsoSessionCookie, Guid.NewGuid() + "." + "AAAA"));

        using var response = await browser.GetAsync("/api/admin/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 巨大な要求本文は受け付けない()
    {
        using var browser = idp.NewBrowser();
        using var content = new StringContent(new string('a', 300 * 1024), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        using var response = await browser.Http.PostAsync("/login", content);

        Assert.True(response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, response.StatusCode.ToString());
    }

    // ---- TOTP ------------------------------------------------------------

    [Fact]
    public async Task TOTPを登録済みのユーザはパスワードのあとにコードを求められる()
    {
        var loginId = E2E.Unique("totp");
        var secret = E2E.NewTotpSecret();
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: secret, enableSecretKey: true, serviceManager: true);
        using var browser = idp.NewBrowser();

        using var password = await browser.SubmitPasswordAsync(loginId, E2E.Password);
        Assert.Equal(HttpStatusCode.Redirect, password.StatusCode);
        Assert.StartsWith("/login/totp", password.Headers.Location!.OriginalString, StringComparison.Ordinal);

        // **TOTP が済むまではセッションが無い**
        using var early = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, early.StatusCode);

        using var wrong = await browser.SubmitTotpAsync("000000");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("認証コード", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var correct = await browser.SubmitTotpAsync(E2E.TotpCode(secret));
        Assert.Equal(HttpStatusCode.Redirect, correct.StatusCode);

        using var me = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await E2E.ReadAsync(me);
        Assert.Equal(["pwd", "otp"], body["authMethods"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task 使用済みのコードは別のログインで再利用できない()
    {
        var loginId = E2E.Unique("replay");
        var secret = E2E.NewTotpSecret();
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: secret, enableSecretKey: true);
        var code = E2E.TotpCode(secret);

        using (var first = idp.NewBrowser())
        {
            using var password = await first.SubmitPasswordAsync(loginId, E2E.Password);
            using var ok = await first.SubmitTotpAsync(code);
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        }

        // **盗み見られた数字が、同じ 30 秒の間に使い回されない**
        using var second = idp.NewBrowser();
        using var again = await second.SubmitPasswordAsync(loginId, E2E.Password);
        using var replay = await second.SubmitTotpAsync(code);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var me = await second.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task 時計が少しずれた端末の前後の枠のコードも通る()
    {
        var loginId = E2E.Unique("skew");
        var secret = E2E.NewTotpSecret();
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: secret, enableSecretKey: true);
        using var browser = idp.NewBrowser();

        using var password = await browser.SubmitPasswordAsync(loginId, E2E.Password);
        using var response = await browser.SubmitTotpAsync(E2E.TotpCode(secret, stepOffset: -1));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task 二つ前の枠のコードは通らない()
    {
        var loginId = E2E.Unique("old");
        var secret = E2E.NewTotpSecret();
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: secret, enableSecretKey: true);
        using var browser = idp.NewBrowser();

        using var password = await browser.SubmitPasswordAsync(loginId, E2E.Password);
        using var response = await browser.SubmitTotpAsync(E2E.TotpCode(secret, stepOffset: -3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task コードの総当たりは同じ回数で締め出される()
    {
        var loginId = E2E.Unique("brute");
        var secret = E2E.NewTotpSecret();
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: secret, enableSecretKey: true);
        using var browser = idp.NewBrowser();
        using var password = await browser.SubmitPasswordAsync(loginId, E2E.Password);

        for (var i = 0; i < 4; i++)
        {
            using var attempt = await browser.SubmitTotpAsync("00000" + i);
            Assert.Equal(HttpStatusCode.OK, attempt.StatusCode);
        }

        using var locked = await browser.SubmitTotpAsync("000009");
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        // 締め出し中は、正しいコードでも通らない
        using var late = idp.NewBrowser();
        using var latePassword = await late.SubmitPasswordAsync(loginId, E2E.Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, latePassword.StatusCode);
    }

    [Fact]
    public async Task パスワードの段階を経ずにTOTPの画面へは進めない()
    {
        using var browser = idp.NewBrowser();

        using var page = await browser.GetAsync("/login/totp");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("name=\"loginId\"", html, StringComparison.Ordinal);

        using var post = await browser.SubmitTotpAsync("123456");
        Assert.NotEqual(HttpStatusCode.Redirect, post.StatusCode);
    }

    [Fact]
    public async Task TOTPを登録していないユーザはパスワードだけで入れる()
    {
        var loginId = E2E.Unique("nototp");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId, E2E.Password);

        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task 無効化したTOTPは求めない()
    {
        // EnableSecretKey が無効なら、共有鍵が残っていても使われない
        var loginId = E2E.Unique("disabledtotp");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: E2E.NewTotpSecret(), enableSecretKey: false);
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId, E2E.Password);

        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
    }
}

/// <summary>TOTP を Pleasanter の設定（<c>DefaultEnable</c>）に従わせる構成。</summary>
public class PleasanterTotpModeEndToEndTests(PleasanterTotpModeProcess idp) : IClassFixture<PleasanterTotpModeProcess>
{
    [Fact]
    public async Task 二次認証が必須で登録済みならコードを求める()
    {
        var loginId = E2E.Unique("must");
        var secret = E2E.NewTotpSecret();
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, secretKey: secret, enableSecretKey: true);
        using var browser = idp.NewBrowser();

        using var password = await browser.SubmitPasswordAsync(loginId, E2E.Password);
        Assert.StartsWith("/login/totp", password.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var ok = await browser.SubmitTotpAsync(E2E.TotpCode(secret));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [Fact]
    public async Task 二次認証が必須なのに未登録ならIdPでは登録できないので入れない()
    {
        var loginId = E2E.Unique("unregistered");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password);
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId, E2E.Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Pleasanter で登録", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var me = await browser.GetAsync("/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task 二次認証を無効にしたユーザは求められない()
    {
        var loginId = E2E.Unique("exempt");
        await idp.Pleasanter.AddUserAsync(loginId, E2E.Password, disableSecondaryAuthentication: true);
        using var browser = idp.NewBrowser();

        using var response = await browser.SubmitPasswordAsync(loginId, E2E.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location!.OriginalString);
    }
}

/// <summary>IP ごとのレート制限。</summary>
public class LoginRateLimitEndToEndTests(LowRateLimitProcess idp) : IClassFixture<LowRateLimitProcess>
{
    [Fact]
    public async Task 同じ送信元からの多数のログイン要求は止まる()
    {
        var statuses = new List<HttpStatusCode>();
        using var browser = idp.NewBrowser();
        for (var i = 0; i < 6; i++)
        {
            using var response = await browser.SubmitPasswordAsync(E2E.Unique("rate"), "x");
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.OK, statuses[0]);
    }
}
