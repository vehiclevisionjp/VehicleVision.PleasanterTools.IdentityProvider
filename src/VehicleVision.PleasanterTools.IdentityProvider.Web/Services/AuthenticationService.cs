using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

public enum PasswordStatus
{
    /// <summary>通した（二段階認証も要らない）。</summary>
    Success,

    InvalidCredentials,

    LockedOut,

    /// <summary>パスワードは合ったが、ログインを通せない（無効・期限切れなど）。</summary>
    Denied,

    /// <summary>パスワードは合った。続けて TOTP を求める。</summary>
    TotpRequired,

    /// <summary>TOTP が要るが、Pleasanter 側で登録されていない。</summary>
    TotpNotRegistered,
}

public sealed record PasswordResult(
    PasswordStatus Status,
    PleasanterUser? User = null,
    LoginDenial Denial = LoginDenial.None);

public enum TotpStatus
{
    Success,
    Invalid,
    LockedOut,
}

/// <summary>パスワードと TOTP による認証。</summary>
/// <remarks>
/// <para>
/// **Pleasanter の DB は読むだけ。** 失敗の回数・締め出し・使用済みの TOTP の枠は、
/// この IdP 自身の DB に持つ（<see cref="ILoginSecurityStore"/>）。
/// Pleasanter の <c>Lockout</c> は尊重する（立っていれば通さない）が、書き換えない。
/// </para>
/// <para>
/// **ログイン ID が存在するかどうかを、応答から読み取らせない。**
/// 存在しない ID でも同じ時間をかけて同じ応答を返し、同じ回数の失敗で締め出す。
/// パスワードが合ったあとにだけ、通せない理由を伝える。
/// </para>
/// <para>
/// **パスワードと TOTP の失敗は、同じ鍵（ログイン ID）で数える。**
/// パスワードを知っている相手に、6 桁の数字を総当たりさせないため。
/// </para>
/// </remarks>
public sealed class AuthenticationService(
    IPleasanterUserStore users,
    ILoginSecurityStore security,
    IdpOptions options,
    TimeProvider timeProvider)
{
    /// <summary>存在しないユーザへの照合で、実在するときと同じ時間をかけるためのダミー。</summary>
    private static readonly string DummyHash = PleasanterPassword.Hash("dummy-password-for-timing");

    public async Task<PasswordResult> CheckPasswordAsync(
        string loginId,
        string password,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var key = LoginKey(loginId);

        if (await security.GetLockedUntilAsync(key, now, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new PasswordResult(PasswordStatus.LockedOut);
        }

        var user = await users.FindByLoginIdAsync(loginId, cancellationToken).ConfigureAwait(false);
        var verified = PleasanterPassword.Verify(password, user?.PasswordHash ?? DummyHash);

        if (user is null || !verified)
        {
            var state = await RecordFailureAsync(key, now, cancellationToken).ConfigureAwait(false);
            return state.LockedUntil is not null
                ? new PasswordResult(PasswordStatus.LockedOut)
                : new PasswordResult(PasswordStatus.InvalidCredentials);
        }

        var denial = LoginGate.Check(user, DateTime.Now);
        if (denial is not LoginDenial.None)
        {
            return new PasswordResult(PasswordStatus.Denied, user, denial);
        }

        switch (options.Totp.Evaluate(user))
        {
            case TotpRequirement.Required:
                // **まだ失敗の記録は消さない。** TOTP まで通って初めて「成功」
                return new PasswordResult(PasswordStatus.TotpRequired, user);

            case TotpRequirement.RequiredButNotRegistered:
                return new PasswordResult(PasswordStatus.TotpNotRegistered, user);

            default:
                await security.ResetAsync(key, cancellationToken).ConfigureAwait(false);
                return new PasswordResult(PasswordStatus.Success, user);
        }
    }

    /// <summary>Pleasanter が保存した共有鍵で TOTP を検証する。</summary>
    public async Task<TotpStatus> VerifyTotpAsync(
        PleasanterUser user,
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var key = LoginKey(user.LoginId);

        if (await security.GetLockedUntilAsync(key, now, cancellationToken).ConfigureAwait(false) is not null)
        {
            return TotpStatus.LockedOut;
        }

        var (verified, timeStep) = TotpVerifier.Verify(user.SecretKey, code, options.Totp.CountTolerances, now);

        // **同じ枠は 1 度しか通さない。** 検証に通っても、使用済みなら失敗と同じに扱う
        if (verified
            && await security.TryUseTotpStepAsync(user.UserKey, timeStep, now, cancellationToken).ConfigureAwait(false))
        {
            await security.ResetAsync(key, cancellationToken).ConfigureAwait(false);
            return TotpStatus.Success;
        }

        var state = await RecordFailureAsync(key, now, cancellationToken).ConfigureAwait(false);
        return state.LockedUntil is not null ? TotpStatus.LockedOut : TotpStatus.Invalid;
    }

    /// <summary>ログイン ID の正規化。大文字小文字・前後の空白で別人扱いにしない。</summary>
    public static string LoginKey(string loginId) => loginId.Trim().ToLowerInvariant();

    private Task<LoginFailureState> RecordFailureAsync(string key, DateTime now, CancellationToken cancellationToken) =>
        security.RecordFailureAsync(
            key,
            now,
            options.LoginMaxFailures,
            options.LoginFailureWindow,
            options.LoginLockoutDuration,
            cancellationToken);
}
