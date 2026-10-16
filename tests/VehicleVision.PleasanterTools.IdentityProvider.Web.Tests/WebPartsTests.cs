using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Endpoints;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Pages;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Tests;

public class ReturnUrlTests
{
    [Theory]
    [InlineData("/admin", "", true)]
    [InlineData("/oidc/authorize?client_id=a&state=x%20y", "", true)]
    [InlineData("/idp/admin", "/idp", true)]
    [InlineData("/admin", "/idp", false)]
    [InlineData("//evil.example.com/", "", false)]
    [InlineData("/\\evil.example.com/", "", false)]
    [InlineData("\\\\evil.example.com", "", false)]
    [InlineData("https://evil.example.com/", "", false)]
    [InlineData("javascript:alert(1)", "", false)]
    [InlineData("", "", false)]
    [InlineData(null, "", false)]
    [InlineData("/ok\r\nSet-Cookie: x=1", "", false)]
    [InlineData("/ok\\..\\evil", "", false)]
    public void 戻り先に使えるのは同じオリジンの絶対パスだけ(string? url, string pathBase, bool expected)
    {
        Assert.Equal(expected, SsoSessionService.IsSafeReturnUrl(url, new PathString(pathBase)));
    }

    [Fact]
    public void 長すぎる戻り先は使えない()
    {
        Assert.False(SsoSessionService.IsSafeReturnUrl("/" + new string('a', 9000), PathString.Empty));
    }
}

public class CookieSignerTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void 署名した値は読み戻せる()
    {
        var signer = new CookieSigner(Key);

        var value = signer.Sign("session", "日本語のペイロード {\"a\":1}");

        Assert.Equal("日本語のペイロード {\"a\":1}", signer.Verify("session", value));
    }

    [Fact]
    public void 用途が違えば読めない()
    {
        var signer = new CookieSigner(Key);

        Assert.Null(signer.Verify("mfa", signer.Sign("session", "x")));
    }

    [Fact]
    public void 鍵が違えば読めない()
    {
        Assert.Null(new CookieSigner(RandomNumberGenerator.GetBytes(32)).Verify("p", new CookieSigner(Key).Sign("p", "x")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData(".")]
    [InlineData("a.")]
    [InlineData(".b")]
    [InlineData("!!!.!!!")]
    public void 壊れた値はnull(string? value)
    {
        Assert.Null(new CookieSigner(Key).Verify("p", value));
    }

    [Fact]
    public void 本体を書き換えると読めない()
    {
        var signer = new CookieSigner(Key);
        var parts = signer.Sign("p", "user=1").Split('.');
        var forgedBody = Core.Oidc.Pkce.Base64Url(System.Text.Encoding.UTF8.GetBytes("user=2"));

        Assert.Null(signer.Verify("p", forgedBody + "." + parts[1]));
    }
}

public class ClientSecretsTests
{
    [Fact]
    public void 生成した値は十分に長く毎回違う()
    {
        var a = ClientSecrets.Generate();
        var b = ClientSecrets.Generate();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }

    [Fact]
    public void ハッシュで照合でき値そのものは含まれない()
    {
        var secret = ClientSecrets.Generate();
        var hash = ClientSecrets.Hash(secret);

        Assert.True(ClientSecrets.Verify(secret, hash));
        Assert.False(ClientSecrets.Verify(secret + "x", hash));
        Assert.DoesNotContain(secret, hash, StringComparison.Ordinal);
        Assert.Equal(64, hash.Length);
    }

    [Theory]
    [InlineData(null, "abc")]
    [InlineData("", "abc")]
    [InlineData("abc", null)]
    [InlineData("abc", "")]
    public void どちらかが空なら通らない(string? presented, string? stored)
    {
        Assert.False(ClientSecrets.Verify(presented, stored));
    }
}

public class SecretProtectorTests
{
    [Fact]
    public void 暗号化して復号できる毎回違う暗号文になる()
    {
        var protector = new SecretProtector(SecretProtector.GenerateKey());

        var a = protector.Protect("秘密の値");
        var b = protector.Protect("秘密の値");

        Assert.NotEqual(a, b);
        Assert.Equal("秘密の値", protector.Unprotect(a));
        Assert.DoesNotContain("秘密", a, StringComparison.Ordinal);
    }

    [Fact]
    public void 改ざんや違う鍵や壊れた入力はnull()
    {
        var protector = new SecretProtector(SecretProtector.GenerateKey());
        var value = protector.Protect("x");
        var parts = value.Split('$');
        var tampered = string.Join('$', parts[0], parts[1], parts[2], Convert.ToBase64String(new byte[] { 1 }));

        Assert.Null(protector.Unprotect(tampered));
        Assert.Null(new SecretProtector(SecretProtector.GenerateKey()).Unprotect(value));
        Assert.Null(protector.Unprotect("v1$a$b"));
        Assert.Null(protector.Unprotect("v2$a$b$c"));
        Assert.Null(protector.Unprotect(string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")]
    public void 鍵の形式を検証する(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SecretProtector(key));
    }
}

public class IdpOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void 既定値()
    {
        var options = IdpOptions.FromConfiguration(Config());

        Assert.Equal("http://localhost:8081", options.Issuer);
        Assert.Equal(5, options.LoginMaxFailures);
        Assert.Equal(TimeSpan.FromMinutes(10), options.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromHours(8), options.SessionLifetime);
        Assert.Equal(TotpMode.Auto, options.Totp.Mode);
        Assert.Equal(1, options.Totp.CountTolerances);
        Assert.Equal(AdminRole.TenantManager, options.AdminRole);
        Assert.Empty(options.AdminAccessRules);
        Assert.False(options.IsSecure);
    }

    [Fact]
    public void 設定を読む()
    {
        var options = IdpOptions.FromConfiguration(Config(
            ("IDP_ISSUER", "https://idp.example.com/path/"),
            ("IDP_SESSION_LIFETIME_MINUTES", "60"),
            ("IDP_LOGIN_MAX_FAILURES", "3"),
            ("IDP_TOTP_MODE", "pleasanter"),
            ("SecondaryAuthentication:Mode", "DefaultEnable"),
            ("SecondaryAuthentication:CountTolerances", "2"),
            ("IDP_ADMIN_ROLE", "ServiceManager"),
            ("IDP_ADMIN_INCLUDE", "group:5, dept:2"),
            ("IDP_ADMIN_EXCLUDE", "user:9"),
            ("IDP_ADMIN_LOGIN_IDS", "Admin, root;ops")));

        Assert.Equal("https://idp.example.com/path", options.Issuer);
        Assert.True(options.IsSecure);
        Assert.Equal(TimeSpan.FromHours(1), options.SessionLifetime);
        Assert.Equal(3, options.LoginMaxFailures);
        Assert.Equal(new TotpPolicy(TotpMode.Pleasanter, PleasanterSecondaryAuthenticationMode.DefaultEnable, 2), options.Totp);
        Assert.Equal(AdminRole.ServiceManager, options.AdminRole);
        Assert.Equal(
            [new AccessRule("include", "group", 5), new AccessRule("include", "dept", 2), new AccessRule("exclude", "user", 9)],
            options.AdminAccessRules);
        Assert.Equal(["admin", "root", "ops"], options.AdminLoginIds);
    }

    [Theory]
    [InlineData("IDP_LOGIN_MAX_FAILURES", "0")]
    [InlineData("IDP_LOGIN_MAX_FAILURES", "abc")]
    [InlineData("IDP_LOGIN_MAX_FAILURES", "-1")]
    [InlineData("IDP_ACCESS_TOKEN_LIFETIME_SECONDS", "5")]
    [InlineData("IDP_ACCESS_TOKEN_LIFETIME_SECONDS", "999999")]
    [InlineData("IDP_TOTP_MODE", "sometimes")]
    [InlineData("IDP_ADMIN_ROLE", "root")]
    [InlineData("IDP_ADMIN_INCLUDE", "group")]
    [InlineData("IDP_ADMIN_EXCLUDE", "tenant:1")]
    [InlineData("SecondaryAuthentication:Mode", "Always")]
    [InlineData("SecondaryAuthentication:CountTolerances", "9")]
    [InlineData("IDP_ISSUER", "not a url")]
    [InlineData("IDP_ISSUER", "ftp://idp.example.com")]
    [InlineData("IDP_ISSUER", "https://idp.example.com/?x=1")]
    [InlineData("IDP_ISSUER", "http://idp.example.com")]
    public void 書き間違いは起動時に止める(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => IdpOptions.FromConfiguration(Config((key, value))));
    }

    [Fact]
    public void 平文のHTTPは明示すれば許し_ループバックは常に許す()
    {
        Assert.Equal("http://idp.example.com", IdpOptions.FromConfiguration(Config(
            ("IDP_ISSUER", "http://idp.example.com"), ("IDP_ALLOW_INSECURE", "true"))).Issuer);
        Assert.Equal("http://127.0.0.1:5000", IdpOptions.FromConfiguration(Config(("IDP_ISSUER", "http://127.0.0.1:5000"))).Issuer);
        Assert.Equal("http://localhost:8081", IdpOptions.FromConfiguration(Config(("IDP_ISSUER", "http://localhost:8081"))).Issuer);
    }
}

public class HtmlPagesTests
{
    [Fact]
    public void 値は必ずエンコードして埋める()
    {
        var html = HtmlPages.Login("ja", "/idp", "\"><script>alert(1)</script>", "tok\"en", "<b>id</b>", UiText.InvalidCredentials);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>id</b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/idp/login\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void インラインのスクリプトとスタイルを含まない()
    {
        var pages = new[]
        {
            HtmlPages.Login("ja", "", "/admin", "t"),
            HtmlPages.Totp("en", "", "/admin", "t"),
            HtmlPages.Message("ja", "", "t", "m", "/login"),
            HtmlPages.LogoutConfirm("ja", "", "t", "山田", null, null),
        };

        foreach (var html in pages)
        {
            Assert.DoesNotContain("<style", html, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
            Assert.DoesNotContain(" onclick=", html, StringComparison.Ordinal);
            Assert.DoesNotContain("style=\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 自動送信ページは外部のスクリプトを読みフォームの値をエンコードする()
    {
        var html = HtmlPages.AutoPost("ja", "", "https://sp.example.com/acs", new Dictionary<string, string>
        {
            ["SAMLResponse"] = "AAA=\"><x>",
            ["RelayState"] = "r&s",
        });

        Assert.Contains("<script src=\"/idp-assets/post.js\"></script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<x>", html, StringComparison.Ordinal);
        Assert.Contains("r&amp;s", html, StringComparison.Ordinal);
        Assert.Contains("id=\"autoPost\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void 日本語はそのまま出る()
    {
        Assert.Contains("サインイン", HtmlPages.Login("ja", "", "/admin", "t"), StringComparison.Ordinal);
        Assert.Contains("Sign in", HtmlPages.Login("en", "", "/admin", "t"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "ja")]
    [InlineData("", "ja")]
    [InlineData("ja,en-US;q=0.9", "ja")]
    [InlineData("en-US,en;q=0.9,ja;q=0.8", "en")]
    [InlineData("fr-FR", "en")]
    public void 言語はAcceptLanguageで決める(string? header, string expected)
    {
        Assert.Equal(expected, UiText.Resolve(header));
    }

    [Fact]
    public void 全ての文言に日本語と英語がある()
    {
        var keys = typeof(UiText).GetFields().Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.Contains('.', StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.NotEqual(key, UiText.Of("ja", key, "x"));
            Assert.NotEqual(key, UiText.Of("en", key, "x"));
        }
    }
}

public class CsrfGuardTests
{
    private static CsrfGuard Guard() => new(new IdpOptions { Issuer = "https://idp.example.com" });

    private static DefaultHttpContext Context(string? cookie, string? origin = null, string? referer = null)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{CsrfGuard.CookieName}={cookie}";
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (referer is not null)
        {
            context.Request.Headers.Referer = referer;
        }

        return context;
    }

    [Fact]
    public void cookieと一致すれば通る()
    {
        Assert.True(Guard().Validate(Context("abc"), "abc"));
        Assert.True(Guard().Validate(Context("abc", origin: "https://idp.example.com"), "abc"));
        Assert.True(Guard().Validate(Context("abc", referer: "https://idp.example.com/login"), "abc"));
    }

    [Theory]
    [InlineData("abc", "abd")]
    [InlineData("abc", "")]
    [InlineData("abc", null)]
    [InlineData(null, "abc")]
    [InlineData("", "")]
    public void トークンが無いか一致しなければ通らない(string? cookie, string? form)
    {
        Assert.False(Guard().Validate(Context(cookie), form));
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("null")]
    [InlineData("http://idp.example.com")]
    [InlineData("https://idp.example.com.evil.test")]
    public void 他所のオリジンからは通らない(string origin)
    {
        Assert.False(Guard().Validate(Context("abc", origin: origin), "abc"));
        Assert.False(Guard().Validate(Context("abc", referer: origin + "/page"), "abc"));
    }

    [Fact]
    public void 発行するトークンは毎回違いcookieに入る()
    {
        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();

        var a = Guard().Issue(first);
        var b = Guard().Issue(second);

        Assert.NotEqual(a, b);
        var setCookie = first.Response.Headers.SetCookie.ToString();
        Assert.Contains(CsrfGuard.CookieName + "=" + a, setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }
}

public class AdminPathAndPathBaseTests
{
    [Theory]
    [InlineData(null, "/admin")]
    [InlineData("/console", "/console")]
    [InlineData("/my-admin-2", "/my-admin-2")]
    public void 管理画面のパス(string? value, string expected)
    {
        Assert.Equal(expected, AdminPathOptions.Parse(value).Path);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("/")]
    [InlineData("/Admin")]
    [InlineData("/a/b")]
    [InlineData("/api")]
    [InlineData("/healthz")]
    [InlineData("/ready")]
    [InlineData("/login")]
    [InlineData("/oidc")]
    [InlineData("/saml")]
    [InlineData("/idp-assets")]
    public void 使えない管理画面のパスは起動時に止める(string value)
    {
        Assert.Throws<InvalidOperationException>(() => AdminPathOptions.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public void サブパスなしは根(string? value)
    {
        Assert.False(PathBaseOptions.Parse(value).IsConfigured);
    }

    [Fact]
    public void サブパスを読む()
    {
        Assert.Equal("/idp", PathBaseOptions.Parse("/idp").Value.Value);
    }

    [Theory]
    [InlineData("idp")]
    [InlineData("/idp/")]
    [InlineData("/id p")]
    [InlineData("/a/../b")]
    [InlineData("/./a")]
    [InlineData("/a?x=1")]
    public void 使えないサブパスは起動時に止める(string value)
    {
        Assert.Throws<InvalidOperationException>(() => PathBaseOptions.Parse(value));
    }
}

public class NetworkRestrictionTests
{
    [Fact]
    public void 許可したネットワークの外へは生存確認を隠す()
    {
        var restrictions = EndpointNetworkRestrictions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EndpointNetworkRestrictions.HealthSetting] = "10.0.0.0/8, 192.168.1.0/24",
            }).Build());

        Assert.True(restrictions.IsAllowed("/healthz", IPAddress.Parse("10.1.2.3")));
        Assert.True(restrictions.IsAllowed("/ready", IPAddress.Parse("::ffff:192.168.1.9")));
        Assert.False(restrictions.IsAllowed("/healthz", IPAddress.Parse("203.0.113.5")));
        Assert.False(restrictions.IsAllowed("/healthz", null));

        // 他の経路は対象外
        Assert.True(restrictions.IsAllowed("/login", IPAddress.Parse("203.0.113.5")));
    }

    [Fact]
    public void 設定が無ければ制限しない()
    {
        var restrictions = EndpointNetworkRestrictions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.True(restrictions.IsAllowed("/healthz", IPAddress.Parse("203.0.113.5")));
    }

    [Fact]
    public void CIDRでない値は起動時に止める()
    {
        Assert.Throws<InvalidOperationException>(() => ForwardedProxyNetworks.Parse("10.0.0.0/8, nonsense"));
    }
}

public class SamlIdpServiceHelperTests
{
    [Theory]
    [InlineData("emailAddress", "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress")]
    [InlineData("persistent", "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent")]
    [InlineData("transient", "urn:oasis:names:tc:SAML:2.0:nameid-format:transient")]
    [InlineData("unspecified", "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified")]
    [InlineData("anything", "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified")]
    public void NameID形式(string format, string expected)
    {
        Assert.Equal(expected, SamlIdpService.NameIdFormatOf(format).OriginalString);
    }

    [Fact]
    public void セッション索引はセッションIDと相互に変換できる()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, SamlIdpService.SessionIdOf(SamlIdpService.SessionIndexOf(id)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("_")]
    [InlineData("_not-a-guid")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void 不正なセッション索引はnull(string? index)
    {
        Assert.Null(SamlIdpService.SessionIdOf(index));
    }
}

public class ImportMetadataTests
{
    [Fact]
    public void 本文が空や大きすぎるときは400()
    {
        Assert.Equal(400, StatusOf(AdminEndpoints.ImportMetadata(null)));
        Assert.Equal(400, StatusOf(AdminEndpoints.ImportMetadata("  ")));
        Assert.Equal(400, StatusOf(AdminEndpoints.ImportMetadata(new string('x', 1_048_577))));
    }

    [Fact]
    public void DTDを含むXMLは外部実体を読まず400()
    {
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a SYSTEM \"file:///c:/windows/win.ini\">]>"
            + "<EntityDescriptor xmlns=\"urn:oasis:names:tc:SAML:2.0:metadata\" entityID=\"&a;\"/>";

        Assert.Equal(400, StatusOf(AdminEndpoints.ImportMetadata(xml)));
    }

    private static int StatusOf(IResult result) =>
        result is IStatusCodeHttpResult { StatusCode: { } status } ? status : 200;
}

public class ConnectionSecurityTests
{
    [Theory]
    [InlineData(Data.DatabaseProvider.SqlServer, "Server=db;Database=d;UID=u;PWD=p;Encrypt=True", true)]
    [InlineData(Data.DatabaseProvider.SqlServer, "Server=db;Database=d;UID=u;PWD=p", true)]
    [InlineData(Data.DatabaseProvider.SqlServer, "Server=db;Database=d;UID=u;PWD=p;Encrypt=False", false)]
    [InlineData(Data.DatabaseProvider.SqlServer, "Server=db;Database=d;UID=u;PWD=p;TrustServerCertificate=True", false)]
    [InlineData(Data.DatabaseProvider.PostgreSql, "Host=h;Database=d;Username=u;Password=p;SSL Mode=VerifyFull", true)]
    [InlineData(Data.DatabaseProvider.PostgreSql, "Host=h;Database=d;Username=u;Password=p;SSL Mode=VerifyCA", true)]
    [InlineData(Data.DatabaseProvider.PostgreSql, "Host=h;Database=d;Username=u;Password=p;SSL Mode=Disable", false)]
    [InlineData(Data.DatabaseProvider.PostgreSql, "Host=h;Database=d;Username=u;Password=p;SSL Mode=Prefer", false)]
    [InlineData(Data.DatabaseProvider.PostgreSql, "Host=h;Database=d;Username=u;Password=p;SSL Mode=Require", false)]
    [InlineData(Data.DatabaseProvider.MySql, "Server=h;Database=d;Uid=u;Pwd=p;SslMode=VerifyFull", true)]
    [InlineData(Data.DatabaseProvider.MySql, "Server=h;Database=d;Uid=u;Pwd=p;SslMode=None", false)]
    [InlineData(Data.DatabaseProvider.MySql, "Server=h;Database=d;Uid=u;Pwd=p;SslMode=Preferred", false)]
    [InlineData(Data.DatabaseProvider.MySql, "Server=h;Database=d;Uid=u;Pwd=p;SslMode=Required", false)]
    [InlineData(Data.DatabaseProvider.Sqlite, "Data Source=x.db", true)]
    public void 暗号化と証明書の確認を見る(Data.DatabaseProvider provider, string connectionString, bool secure)
    {
        Assert.Equal(secure, Data.ConnectionSecurity.Inspect(provider, connectionString).Count == 0);
    }

    [Fact]
    public void 不備があれば起動を止め_明示すれば緩める()
    {
        const string insecure = "Host=h;Database=d;Username=u;Password=p;SSL Mode=Disable";

        var exception = Assert.Throws<InvalidOperationException>(
            () => Data.ConnectionSecurity.EnsureSecure(Data.DatabaseProvider.PostgreSql, insecure, allowInsecure: false));
        Data.ConnectionSecurity.EnsureSecure(Data.DatabaseProvider.PostgreSql, insecure, allowInsecure: true);

        // 接続文字列（資格情報を含む）は例外へ出さない。緩める設定の名前は伝える
        Assert.DoesNotContain("Password=p", exception.Message, StringComparison.Ordinal);
        Assert.Contains("IDP_DB_ALLOW_INSECURE", exception.Message, StringComparison.Ordinal);

        var pleasanter = Assert.Throws<InvalidOperationException>(() => Data.ConnectionSecurity.EnsureSecure(
            Data.DatabaseProvider.PostgreSql, insecure, allowInsecure: false, allowSetting: "IDP_PLEASANTER_DB_ALLOW_INSECURE"));
        Assert.Contains("IDP_PLEASANTER_DB_ALLOW_INSECURE", pleasanter.Message, StringComparison.Ordinal);
    }
}
