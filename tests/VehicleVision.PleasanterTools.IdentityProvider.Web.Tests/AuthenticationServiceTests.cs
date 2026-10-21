using Microsoft.Extensions.Time.Testing;
using OtpNet;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Tests;

public class AuthenticationServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 3, 0, 15, TimeSpan.Zero);

    private static (AuthenticationService Service, FakeTimeProvider Time, FakeLoginSecurityStore Security) Create(
        PleasanterUser[] users,
        TotpPolicy? totp = null,
        int maxFailures = 5)
    {
        var time = new FakeTimeProvider(Start);
        var security = new FakeLoginSecurityStore();
        var options = new IdpOptions
        {
            Issuer = "http://localhost:8081",
            LoginMaxFailures = maxFailures,
            Totp = totp ?? TotpPolicy.Default,
        };
        return (new AuthenticationService(new FakePleasanterUserStore(users), security, options, time), time, security);
    }

    [Fact]
    public async Task 正しいパスワードで通る()
    {
        var (service, _, _) = Create([TestUsers.Valid()]);

        var result = await service.CheckPasswordAsync("taro", "correct-horse");

        Assert.Equal(PasswordStatus.Success, result.Status);
        Assert.Equal(7, result.User!.UserId);
    }

    [Fact]
    public async Task ログインIDの大文字小文字を区別しない()
    {
        var (service, _, _) = Create([TestUsers.Valid()]);

        Assert.Equal(PasswordStatus.Success, (await service.CheckPasswordAsync("  TARO ", "correct-horse")).Status);
    }

    [Fact]
    public async Task 間違ったパスワードと存在しないユーザは同じ結果()
    {
        var (service, _, _) = Create([TestUsers.Valid()]);

        var wrong = await service.CheckPasswordAsync("taro", "wrong");
        var ghost = await service.CheckPasswordAsync("ghost", "wrong");

        Assert.Equal(PasswordStatus.InvalidCredentials, wrong.Status);
        Assert.Equal(PasswordStatus.InvalidCredentials, ghost.Status);
        Assert.Null(wrong.User);
        Assert.Null(ghost.User);
    }

    [Fact]
    public async Task 規定回数の失敗で締め出し正しいパスワードでも通さない()
    {
        var (service, _, _) = Create([TestUsers.Valid()], maxFailures: 3);

        Assert.Equal(PasswordStatus.InvalidCredentials, (await service.CheckPasswordAsync("taro", "x")).Status);
        Assert.Equal(PasswordStatus.InvalidCredentials, (await service.CheckPasswordAsync("taro", "x")).Status);
        Assert.Equal(PasswordStatus.LockedOut, (await service.CheckPasswordAsync("taro", "x")).Status);
        Assert.Equal(PasswordStatus.LockedOut, (await service.CheckPasswordAsync("taro", "correct-horse")).Status);
    }

    [Fact]
    public async Task 存在しないログインIDも同じ回数で締め出す()
    {
        var (service, _, _) = Create([], maxFailures: 2);

        Assert.Equal(PasswordStatus.InvalidCredentials, (await service.CheckPasswordAsync("ghost", "x")).Status);
        Assert.Equal(PasswordStatus.LockedOut, (await service.CheckPasswordAsync("ghost", "x")).Status);
    }

    [Fact]
    public async Task 締め出しは時間が経つと解ける()
    {
        var (service, time, _) = Create([TestUsers.Valid()], maxFailures: 1);
        await service.CheckPasswordAsync("taro", "x");
        Assert.Equal(PasswordStatus.LockedOut, (await service.CheckPasswordAsync("taro", "correct-horse")).Status);

        time.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(PasswordStatus.Success, (await service.CheckPasswordAsync("taro", "correct-horse")).Status);
    }

    [Fact]
    public async Task 成功すると失敗の記録が消える()
    {
        var (service, _, _) = Create([TestUsers.Valid()], maxFailures: 3);
        await service.CheckPasswordAsync("taro", "x");
        await service.CheckPasswordAsync("taro", "x");

        Assert.Equal(PasswordStatus.Success, (await service.CheckPasswordAsync("taro", "correct-horse")).Status);

        // 消えたので、また 2 回までは締め出されない
        Assert.Equal(PasswordStatus.InvalidCredentials, (await service.CheckPasswordAsync("taro", "x")).Status);
        Assert.Equal(PasswordStatus.InvalidCredentials, (await service.CheckPasswordAsync("taro", "x")).Status);
    }

    [Fact]
    public async Task 通せない理由はパスワードが合ったあとにだけ返す()
    {
        var (service, _, _) = Create([TestUsers.Valid() with { Disabled = true }]);

        Assert.Equal(PasswordStatus.InvalidCredentials, (await service.CheckPasswordAsync("taro", "wrong")).Status);

        var correct = await service.CheckPasswordAsync("taro", "correct-horse");
        Assert.Equal(PasswordStatus.Denied, correct.Status);
        Assert.Equal(LoginDenial.Disabled, correct.Denial);
    }

    [Fact]
    public async Task TOTPが登録済みならパスワードのあとにコードを求める()
    {
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        var (service, _, _) = Create([TestUsers.Valid(secretKey: secret, enableSecretKey: true)]);

        var result = await service.CheckPasswordAsync("taro", "correct-horse");

        Assert.Equal(PasswordStatus.TotpRequired, result.Status);
        Assert.NotNull(result.User);
    }

    [Fact]
    public async Task TOTPが必須なのに未登録なら通さない()
    {
        var policy = new TotpPolicy(TotpMode.Pleasanter, PleasanterSecondaryAuthenticationMode.DefaultEnable, 1);
        var (service, _, _) = Create([TestUsers.Valid()], policy);

        Assert.Equal(PasswordStatus.TotpNotRegistered, (await service.CheckPasswordAsync("taro", "correct-horse")).Status);
    }

    [Fact]
    public async Task 正しいコードで通り同じ枠の再利用は拒否する()
    {
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var user = TestUsers.Valid(secretKey: Base32Encoding.ToString(secretBytes), enableSecretKey: true);
        var (service, time, _) = Create([user]);
        var code = new Totp(secretBytes).ComputeTotp(time.GetUtcNow().UtcDateTime);

        Assert.Equal(TotpStatus.Success, await service.VerifyTotpAsync(user, code));
        Assert.Equal(TotpStatus.Invalid, await service.VerifyTotpAsync(user, code));
    }

    [Fact]
    public async Task 間違ったコードの連続で締め出す()
    {
        var user = TestUsers.Valid(secretKey: Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20)), enableSecretKey: true);
        var (service, _, _) = Create([user], maxFailures: 3);

        Assert.Equal(TotpStatus.Invalid, await service.VerifyTotpAsync(user, "000000"));
        Assert.Equal(TotpStatus.Invalid, await service.VerifyTotpAsync(user, "000001"));
        Assert.Equal(TotpStatus.LockedOut, await service.VerifyTotpAsync(user, "000002"));
        Assert.Equal(PasswordStatus.LockedOut, (await service.CheckPasswordAsync("taro", "correct-horse")).Status);
    }

    [Fact]
    public async Task パスワードとコードの失敗は同じ枠で数える()
    {
        var user = TestUsers.Valid(secretKey: Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20)), enableSecretKey: true);
        var (service, _, _) = Create([user], maxFailures: 3);

        await service.CheckPasswordAsync("taro", "wrong");
        await service.CheckPasswordAsync("taro", "wrong");

        // パスワードを知っていても、残りの 1 回でコードを当てなければ締め出される
        Assert.Equal(TotpStatus.LockedOut, await service.VerifyTotpAsync(user, "000000"));
    }
}
