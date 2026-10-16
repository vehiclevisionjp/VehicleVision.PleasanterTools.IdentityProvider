using OtpNet;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Tests;

public class PleasanterPasswordTests
{
    [Fact]
    public void Pleasanterと同じSHA512の16進小文字になる()
    {
        // 参照: _reference/Implem.Pleasanter の Sha512Cng（"pleasanter" は既定の管理者パスワード）
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.UTF8.GetBytes("pleasanter")));

        Assert.Equal(expected, PleasanterPassword.Hash("pleasanter"));
        Assert.Equal(128, PleasanterPassword.Hash("pleasanter").Length);
    }

    [Fact]
    public void 日本語のパスワードもUTF8で照合できる()
    {
        var hash = PleasanterPassword.Hash("パスワード１２３");
        Assert.True(PleasanterPassword.Verify("パスワード１２３", hash));
        Assert.False(PleasanterPassword.Verify("パスワード123", hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 保存値が空なら常に不一致(string? stored)
    {
        Assert.False(PleasanterPassword.Verify("", stored));
        Assert.False(PleasanterPassword.Verify("x", stored));
    }

    [Fact]
    public void 保存値の大文字小文字と前後の空白は無視する()
    {
        var hash = PleasanterPassword.Hash("secret");
        Assert.True(PleasanterPassword.Verify("secret", " " + hash.ToUpperInvariant() + " "));
    }
}

public class TotpVerifierTests
{
    private static readonly byte[] Secret = KeyGeneration.GenerateRandomKey(20);

    private static string Base32 => Base32Encoding.ToString(Secret);

    private static readonly DateTime Now = new(2026, 10, 17, 3, 0, 15, DateTimeKind.Utc);

    private static string CodeAt(DateTime time) => new Totp(Secret, totpSize: 6).ComputeTotp(time);

    [Fact]
    public void 現在の枠のコードが通る()
    {
        var (verified, step) = TotpVerifier.Verify(Base32, CodeAt(Now), countTolerances: 1, Now);

        Assert.True(verified);
        Assert.Equal(new DateTimeOffset(Now).ToUnixTimeSeconds() / 30, step);
    }

    [Fact]
    public void 一つ前の枠は許容回数が1なら通り0なら通らない()
    {
        var previous = CodeAt(Now.AddSeconds(-30));

        Assert.True(TotpVerifier.Verify(Base32, previous, 1, Now).Verified);
        Assert.False(TotpVerifier.Verify(Base32, previous, 0, Now).Verified);
    }

    [Fact]
    public void 二つ前の枠は許容回数が1では通らない()
    {
        Assert.False(TotpVerifier.Verify(Base32, CodeAt(Now.AddSeconds(-60)), 1, Now).Verified);
    }

    [Fact]
    public void 違うコードは通らない()
    {
        var wrong = CodeAt(Now) == "000000" ? "000001" : "000000";
        Assert.False(TotpVerifier.Verify(Base32, wrong, 1, Now).Verified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void 桁や文字が不正なコードは通らない(string code)
    {
        Assert.False(TotpVerifier.Verify(Base32, code, 1, Now).Verified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!not base32!!!")]
    public void 共有鍵が無いか壊れていれば通らない(string? secret)
    {
        Assert.False(TotpVerifier.Verify(secret, "123456", 1, Now).Verified);
    }

    [Fact]
    public void コードの空白は無視する()
    {
        var code = CodeAt(Now);
        Assert.True(TotpVerifier.Verify(Base32, code[..3] + " " + code[3..], 1, Now).Verified);
    }

    [Fact]
    public void ハイフン区切りの共有鍵も読める()
    {
        // Pleasanter は画面に 4 文字ごとのハイフン区切りで見せる（保存は区切り無し）
        var hyphenated = string.Join('-', Enumerable.Range(0, (Base32.Length + 3) / 4)
            .Select(i => Base32.Substring(i * 4, Math.Min(4, Base32.Length - (i * 4)))));

        Assert.True(TotpVerifier.Verify(hyphenated, CodeAt(Now), 1, Now).Verified);
    }

    [Fact]
    public void 許容回数は上限で丸める()
    {
        // 大きな値を設定しても、何十枠も遡れるようにはならない
        Assert.False(TotpVerifier.Verify(Base32, CodeAt(Now.AddMinutes(-10)), 1000, Now).Verified);
    }
}

public class TotpPolicyTests
{
    private static PleasanterUser User(
        bool enableSecretKey = false,
        string? secretKey = null,
        bool enable2fa = false,
        bool disable2fa = false) => new()
        {
            LoginId = "u",
            EnableSecretKey = enableSecretKey,
            SecretKey = secretKey,
            EnableSecondaryAuthentication = enable2fa,
            DisableSecondaryAuthentication = disable2fa,
        };

    private static TotpPolicy Policy(TotpMode mode, PleasanterSecondaryAuthenticationMode pleasanter) =>
        new(mode, pleasanter, 1);

    [Fact]
    public void Autoは登録済みの人にだけ求める()
    {
        var policy = Policy(TotpMode.Auto, PleasanterSecondaryAuthenticationMode.None);

        Assert.Equal(TotpRequirement.Required, policy.Evaluate(User(true, "ABC")));
        Assert.Equal(TotpRequirement.NotRequired, policy.Evaluate(User(false, "ABC")));
        Assert.Equal(TotpRequirement.NotRequired, policy.Evaluate(User(true, "")));
        Assert.Equal(TotpRequirement.NotRequired, policy.Evaluate(User()));
    }

    [Fact]
    public void PleasanterモードのNoneは求めない()
    {
        var policy = Policy(TotpMode.Pleasanter, PleasanterSecondaryAuthenticationMode.None);
        Assert.Equal(TotpRequirement.NotRequired, policy.Evaluate(User(true, "ABC", enable2fa: true)));
    }

    [Fact]
    public void PleasanterモードのDefaultEnableは明示的に無効にした人以外へ求める()
    {
        var policy = Policy(TotpMode.Pleasanter, PleasanterSecondaryAuthenticationMode.DefaultEnable);

        Assert.Equal(TotpRequirement.Required, policy.Evaluate(User(true, "ABC")));
        Assert.Equal(TotpRequirement.NotRequired, policy.Evaluate(User(true, "ABC", disable2fa: true)));
        Assert.Equal(TotpRequirement.RequiredButNotRegistered, policy.Evaluate(User()));
    }

    [Fact]
    public void PleasanterモードのDefaultDisableは明示的に有効にした人だけへ求める()
    {
        var policy = Policy(TotpMode.Pleasanter, PleasanterSecondaryAuthenticationMode.DefaultDisable);

        Assert.Equal(TotpRequirement.NotRequired, policy.Evaluate(User(true, "ABC")));
        Assert.Equal(TotpRequirement.Required, policy.Evaluate(User(true, "ABC", enable2fa: true)));
        Assert.Equal(TotpRequirement.RequiredButNotRegistered, policy.Evaluate(User(enable2fa: true)));
    }
}

public class LoginGateTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0);

    private static PleasanterUser Valid() => new() { LoginId = "u", PasswordHash = "x" };

    [Fact]
    public void 通常のユーザは通る()
    {
        Assert.Equal(LoginDenial.None, LoginGate.Check(Valid(), Now));
    }

    [Fact]
    public void 無効なユーザは通さない()
    {
        Assert.Equal(LoginDenial.Disabled, LoginGate.Check(Valid() with { Disabled = true }, Now));
    }

    [Fact]
    public void ロックアウト中は通さない()
    {
        Assert.Equal(LoginDenial.Lockout, LoginGate.Check(Valid() with { Lockout = true }, Now));
    }

    [Fact]
    public void パスワードが無い外部認証のユーザは通さない()
    {
        Assert.Equal(LoginDenial.NoPassword, LoginGate.Check(Valid() with { PasswordHash = null }, Now));
        Assert.Equal(LoginDenial.NoPassword, LoginGate.Check(Valid() with { PasswordHash = "" }, Now));
    }

    [Fact]
    public void パスワードの有効期限が過ぎていれば通さない()
    {
        Assert.Equal(
            LoginDenial.PasswordExpired,
            LoginGate.Check(Valid() with { PasswordExpirationTime = Now.AddSeconds(-1) }, Now));
        Assert.Equal(
            LoginDenial.None,
            LoginGate.Check(Valid() with { PasswordExpirationTime = Now.AddDays(1) }, Now));
    }

    [Fact]
    public void ログインの有効期限が過ぎていれば通さない()
    {
        Assert.Equal(
            LoginDenial.LoginExpired,
            LoginGate.Check(Valid() with { LoginExpirationLimit = Now.AddDays(-1) }, Now));
    }

    [Fact]
    public void 最終ログインから一定期間が過ぎていれば通さない()
    {
        var user = Valid() with { LastLoginTime = Now.AddDays(-31), LoginExpirationPeriod = 30 };
        Assert.Equal(LoginDenial.LoginExpired, LoginGate.Check(user, Now));
        Assert.Equal(LoginDenial.None, LoginGate.Check(user with { LoginExpirationPeriod = 0 }, Now));
    }

    [Fact]
    public void Pleasanterの日時なしを表す下限の値は無いものとして扱う()
    {
        // Pleasanter は日時が無いことを 1899 年などの下限で持つことがある
        var user = Valid() with
        {
            PasswordExpirationTime = new DateTime(1899, 12, 30),
            LoginExpirationLimit = new DateTime(1899, 12, 30),
            LastLoginTime = new DateTime(1899, 12, 30),
            LoginExpirationPeriod = 30,
        };

        Assert.Equal(LoginDenial.None, LoginGate.Check(user, Now));
    }
}
