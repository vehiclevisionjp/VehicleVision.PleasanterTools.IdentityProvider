namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

/// <summary>パスワードが合ったあとに、ログインを通してよいか。</summary>
public enum LoginDenial
{
    None,
    Disabled,
    Lockout,
    LoginExpired,
    PasswordExpired,
    NoPassword,
}

/// <summary>Pleasanter と同じ条件でログインの可否を見る。</summary>
/// <remarks>
/// 参照: <c>UserModel.cs</c> の <c>LoginExpired</c> / <c>PasswordExpired</c> と、
/// <c>GetByCredentials</c> の <c>Disabled(false)</c>。
/// **Pleasanter はパスワード期限切れのとき変更画面を出す**が、この IdP は書き込めないので
/// 通さずに Pleasanter での変更を案内する。
/// </remarks>
public static class LoginGate
{
    /// <summary>Pleasanter は日時が無いことを下限の値で表す。1900 年以前は「無い」と読む。</summary>
    private static readonly DateTime NoTime = new(1900, 1, 1);

    public static LoginDenial Check(PleasanterUser user, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Disabled)
        {
            return LoginDenial.Disabled;
        }

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return LoginDenial.NoPassword;
        }

        if (user.Lockout)
        {
            return LoginDenial.Lockout;
        }

        if (IsReal(user.LoginExpirationLimit) && user.LoginExpirationLimit <= now)
        {
            return LoginDenial.LoginExpired;
        }

        if (IsReal(user.LastLoginTime)
            && user.LoginExpirationPeriod != 0
            && user.LastLoginTime!.Value.AddDays(user.LoginExpirationPeriod) <= now)
        {
            return LoginDenial.LoginExpired;
        }

        if (IsReal(user.PasswordExpirationTime) && user.PasswordExpirationTime <= now)
        {
            return LoginDenial.PasswordExpired;
        }

        return LoginDenial.None;
    }

    private static bool IsReal(DateTime? value) => value is { } time && time > NoTime;
}
