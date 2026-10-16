using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Tests;

public class PkceTests
{
    // RFC 7636 付録 B の例
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void RFCの例のチャレンジを計算できる()
    {
        Assert.Equal(Challenge, Pkce.ComputeChallenge(Verifier));
    }

    [Fact]
    public void 正しい検証値は通る()
    {
        Assert.True(Pkce.Verify(Verifier, Challenge, Pkce.S256));
    }

    [Fact]
    public void 違う検証値は通らない()
    {
        Assert.False(Pkce.Verify(Verifier + "x", Challenge, Pkce.S256));
        Assert.False(Pkce.Verify("a".PadRight(43, 'a'), Challenge, Pkce.S256));
    }

    [Fact]
    public void plainは受け付けない()
    {
        Assert.False(Pkce.Verify(Verifier, Verifier, "plain"));
        Assert.False(Pkce.Verify(Verifier, Challenge, null));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    [InlineData(null)]
    public void 短すぎる検証値は通らない(string? verifier)
    {
        Assert.False(Pkce.IsValidVerifier(verifier));
        Assert.False(Pkce.Verify(verifier, Challenge, Pkce.S256));
    }

    [Fact]
    public void 長すぎるか使えない文字を含む検証値は通らない()
    {
        Assert.False(Pkce.IsValidVerifier(new string('a', 129)));
        Assert.False(Pkce.IsValidVerifier(new string('a', 42) + "!"));
        Assert.True(Pkce.IsValidVerifier(new string('a', 128)));
    }

    [Theory]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", true)]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-c", false)]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM", false)]
    [InlineData("", false)]
    public void チャレンジの形式を見る(string challenge, bool valid)
    {
        Assert.Equal(valid, Pkce.IsValidChallenge(challenge));
    }
}

public class ScopesTests
{
    [Fact]
    public void 空白区切りを重複なしで読む()
    {
        Assert.Equal(["openid", "email"], Scopes.Parse("openid  email openid"));
        Assert.Empty(Scopes.Parse(null));
        Assert.Empty(Scopes.Parse("  "));
    }

    [Fact]
    public void 許されたスコープだけを付与する()
    {
        var granted = Scopes.Grant(["openid", "email", "groups", "unknown"], ["openid", "email"]);
        Assert.Equal(["openid", "email"], granted);
    }

    [Fact]
    public void 許されなかったスコープを列挙する()
    {
        var denied = Scopes.Denied(["openid", "groups", "admin"], ["openid", "email"]);
        Assert.Equal(["groups", "admin"], denied);
    }
}

public class RedirectUrisTests
{
    [Theory]
    [InlineData("https://app.example.com/callback", false, true)]
    [InlineData("http://localhost:3000/cb", false, true)]
    [InlineData("http://127.0.0.1:5000/cb", false, true)]
    [InlineData("http://app.example.com/cb", false, false)]
    [InlineData("https://app.example.com/*", false, false)]
    [InlineData("https://app.example.com/cb#frag", false, false)]
    [InlineData("https://user:pass@app.example.com/cb", false, false)]
    [InlineData("/relative", false, false)]
    [InlineData("javascript:alert(1)", false, false)]
    [InlineData("com.example.app:/oauth2redirect", true, true)]
    [InlineData("com.example.app:/oauth2redirect", false, false)]
    [InlineData("myapp:/cb", true, false)]
    public void 登録できるURIかどうかを判定する(string uri, bool isPublic, bool ok)
    {
        Assert.Equal(ok, RedirectUris.ValidateRegistration(uri, isPublic) is null);
    }

    [Fact]
    public void 照合は完全一致だけ()
    {
        string[] registered = ["https://app.example.com/callback"];

        Assert.True(RedirectUris.Matches(registered, "https://app.example.com/callback"));
        Assert.False(RedirectUris.Matches(registered, "https://app.example.com/callback/"));
        Assert.False(RedirectUris.Matches(registered, "https://app.example.com/callback?x=1"));
        Assert.False(RedirectUris.Matches(registered, "https://APP.example.com/callback"));
        Assert.False(RedirectUris.Matches(registered, "https://app.example.com.evil.test/callback"));
        Assert.False(RedirectUris.Matches(registered, null));
        Assert.False(RedirectUris.Matches(registered, ""));
    }
}

public class UserClaimsTests
{
    private static PleasanterUserProfile Profile() => new(
        new PleasanterUser
        {
            TenantId = 1,
            UserId = 42,
            LoginId = "taro",
            Name = "山田 太郎",
            Language = "ja",
            TimeZone = "Tokyo Standard Time",
            UserCode = "E001",
            GlobalId = "g-1",
            TenantManager = true,
            PasswordHash = "SECRET-HASH",
            SecretKey = "SECRET-TOTP",
            EnableSecretKey = true,
        },
        ["taro@example.com", "taro2@example.com"],
        ["営業", "管理"],
        "営業部");

    [Fact]
    public void openidだけならsubだけ()
    {
        var claims = UserClaims.ForScopes(Profile(), ["openid"]);

        Assert.Equal(["sub"], claims.Keys);
        Assert.Equal("42", claims["sub"]);
    }

    [Fact]
    public void profileとemailとgroupsを載せる()
    {
        var claims = UserClaims.ForScopes(Profile(), ["openid", "profile", "email", "groups"]);

        Assert.Equal("山田 太郎", claims["name"]);
        Assert.Equal("taro", claims["preferred_username"]);
        Assert.Equal("ja", claims["locale"]);
        Assert.Equal(1, claims["tenant_id"]);
        Assert.Equal("営業部", claims["dept"]);
        Assert.Equal("taro@example.com", claims["email"]);
        Assert.Equal(false, claims["email_verified"]);
        Assert.Equal(["営業", "管理"], (IReadOnlyList<string>)claims["groups"]);
        Assert.Equal(true, claims["tenant_manager"]);
    }

    [Fact]
    public void 秘密の値はどのスコープでも載せない()
    {
        var claims = UserClaims.ForScopes(Profile(), Scopes.Supported);
        var text = string.Join('|', claims.Select(pair => pair.Key + "=" + pair.Value));

        Assert.DoesNotContain("SECRET-HASH", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-TOTP", text, StringComparison.Ordinal);
        Assert.DoesNotContain(claims.Keys, key => key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void メールが無ければemailを載せない()
    {
        var profile = Profile() with { MailAddresses = [] };

        Assert.False(UserClaims.ForScopes(profile, ["openid", "email"]).ContainsKey("email"));
    }

    [Theory]
    [InlineData("loginId", "taro")]
    [InlineData("email", "taro@example.com")]
    [InlineData("userId", "42")]
    [InlineData("globalId", "g-1")]
    [InlineData("unknown", null)]
    public void SAMLのNameIDを選べる(string source, string? expected)
    {
        Assert.Equal(expected, UserClaims.SamlNameId(Profile(), source));
    }

    [Fact]
    public void SAMLの属性値を元の名前から引ける()
    {
        var profile = Profile();

        Assert.Equal(["営業", "管理"], UserClaims.SamlValues(profile, "groups"));
        Assert.Equal(["taro@example.com"], UserClaims.SamlValues(profile, "email"));
        Assert.Equal(["1"], UserClaims.SamlValues(profile, "tenantId"));
        Assert.Empty(UserClaims.SamlValues(profile, "nothing"));
    }
}

public class RegistrationValidatorTests
{
    private static OidcClient ValidClient() => new()
    {
        ClientId = "app-1",
        DisplayName = "アプリ",
        ClientType = OidcClient.Confidential,
        RedirectUris = ["https://app.example.com/cb"],
        AllowedScopes = ["openid", "profile"],
    };

    [Fact]
    public void 正しいクライアントは通る()
    {
        Assert.Empty(RegistrationValidator.Validate(ValidClient()));
    }

    [Fact]
    public void リダイレクトURIが無ければ通らない()
    {
        Assert.NotEmpty(RegistrationValidator.Validate(ValidClient() with { RedirectUris = [] }));
    }

    [Fact]
    public void openidスコープが無ければ通らない()
    {
        Assert.NotEmpty(RegistrationValidator.Validate(ValidClient() with { AllowedScopes = ["profile"] }));
    }

    [Fact]
    public void 扱えないスコープは通らない()
    {
        Assert.NotEmpty(RegistrationValidator.Validate(ValidClient() with { AllowedScopes = ["openid", "admin"] }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("日本語")]
    public void クライアントIDの文字を制限する(string clientId)
    {
        Assert.NotEmpty(RegistrationValidator.Validate(ValidClient() with { ClientId = clientId }));
    }

    [Fact]
    public void トークンの有効期間の範囲を見る()
    {
        Assert.NotEmpty(RegistrationValidator.Validate(ValidClient() with { AccessTokenLifetimeSeconds = 10 }));
        Assert.NotEmpty(RegistrationValidator.Validate(ValidClient() with { RefreshTokenLifetimeSeconds = 99_999_999 }));
        Assert.Empty(RegistrationValidator.Validate(ValidClient() with { AccessTokenLifetimeSeconds = 3600 }));
    }

    private static SamlProvider ValidProvider() => new()
    {
        EntityId = "https://sp.example.com/metadata",
        DisplayName = "SP",
        AcsUrl = "https://sp.example.com/acs",
    };

    [Fact]
    public void 正しいSPは通る()
    {
        Assert.Empty(RegistrationValidator.Validate(ValidProvider()));
    }

    [Fact]
    public void ACSがhttpsでなければ通らない()
    {
        Assert.NotEmpty(RegistrationValidator.Validate(ValidProvider() with { AcsUrl = "http://sp.example.com/acs" }));
        Assert.NotEmpty(RegistrationValidator.Validate(ValidProvider() with { AcsUrl = "javascript:alert(1)" }));
        Assert.Empty(RegistrationValidator.Validate(ValidProvider() with { AcsUrl = "http://localhost:8080/acs" }));
    }

    [Fact]
    public void 署名必須なのに証明書が無ければ通らない()
    {
        Assert.NotEmpty(RegistrationValidator.Validate(ValidProvider() with { RequireSignedRequests = true }));
    }

    [Fact]
    public void 読み取れない証明書は通らない()
    {
        Assert.NotEmpty(RegistrationValidator.Validate(ValidProvider() with { SigningCertificate = "not a certificate" }));
    }

    [Fact]
    public void 属性の元は決まった名前だけ()
    {
        var provider = ValidProvider() with { AttributeMappings = [new SamlAttributeMapping("mail", "password")] };
        Assert.NotEmpty(RegistrationValidator.Validate(provider));
    }
}
