namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

/// <summary>TOTP を求めるかどうかを、何を根拠に決めるか。</summary>
public enum TotpMode
{
    /// <summary>
    /// **既定。** Pleasanter で TOTP を登録済み（<c>EnableSecretKey</c> が有効で共有鍵がある）の人に求める。
    /// Pleasanter 側の二次認証の設定（<c>Security.json</c>）を IdP へ写さなくてよい。
    /// </summary>
    Auto,

    /// <summary>
    /// Pleasanter の <c>SecondaryAuthentication.Mode</c> とユーザごとの
    /// 有効・無効の指定に従う。Pleasanter 本体と同じ条件で求める。
    /// </summary>
    Pleasanter,
}

/// <summary>Pleasanter の <c>SecondaryAuthentication.Mode</c>。</summary>
public enum PleasanterSecondaryAuthenticationMode
{
    None,
    DefaultEnable,
    DefaultDisable,
}

/// <summary>この人に TOTP を求めるか。</summary>
public enum TotpRequirement
{
    /// <summary>求めない。</summary>
    NotRequired,

    /// <summary>求める。</summary>
    Required,

    /// <summary>
    /// 求めるべきだが、Pleasanter 側で登録されていない。
    /// **IdP は Pleasanter の DB へ書けない**ので、ここで登録させる道は無い。
    /// ログインは通さず、Pleasanter での登録を案内する。
    /// </summary>
    RequiredButNotRegistered,
}

/// <summary>TOTP を求めるかどうかの判定。</summary>
/// <remarks>
/// 参照: <c>_reference/Implem.Pleasanter/Implem.Pleasanter/Models/Users/UserModel.cs</c> の
/// <c>EnabledSecondaryAuthentication</c>（<c>Mode</c> と <c>EnableSecondaryAuthentication</c> /
/// <c>DisableSecondaryAuthentication</c> の組み合わせ）。
/// </remarks>
public sealed record TotpPolicy(
    TotpMode Mode,
    PleasanterSecondaryAuthenticationMode PleasanterMode,
    int CountTolerances)
{
    public static readonly TotpPolicy Default = new(
        TotpMode.Auto,
        PleasanterSecondaryAuthenticationMode.None,
        1);

    public TotpRequirement Evaluate(PleasanterUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (Mode is TotpMode.Auto)
        {
            return user.HasTotp ? TotpRequirement.Required : TotpRequirement.NotRequired;
        }

        var required = PleasanterMode switch
        {
            PleasanterSecondaryAuthenticationMode.DefaultEnable => !user.DisableSecondaryAuthentication,
            PleasanterSecondaryAuthenticationMode.DefaultDisable => user.EnableSecondaryAuthentication,
            _ => false,
        };

        if (!required)
        {
            return TotpRequirement.NotRequired;
        }

        return user.HasTotp ? TotpRequirement.Required : TotpRequirement.RequiredButNotRegistered;
    }
}
