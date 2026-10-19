namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

/// <summary>Pleasanter の <c>Users</c> の 1 行（この IdP が読む列だけ）。</summary>
/// <remarks>
/// **読み取り専用。** この型を Pleasanter へ書き戻す道は作らない。
/// <see cref="PasswordHash"/> と <see cref="SecretKey"/> は認証にだけ使い、
/// ログ・応答・監査ログへ出さない。
/// </remarks>
public sealed record PleasanterUser
{
    public int TenantId { get; init; }

    public int UserId { get; init; }

    public string LoginId { get; init; } = string.Empty;

    public string? GlobalId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? UserCode { get; init; }

    public string? Language { get; init; }

    public string? TimeZone { get; init; }

    public int DeptId { get; init; }

    public bool Disabled { get; init; }

    /// <summary>Pleasanter 側のロックアウト（連続失敗による）。</summary>
    public bool Lockout { get; init; }

    public bool TenantManager { get; init; }

    public bool ServiceManager { get; init; }

    /// <summary><c>Users.Password</c>。SHA-512 の 16 進小文字。外部認証だけのユーザでは空。</summary>
    public string? PasswordHash { get; init; }

    public DateTime? PasswordExpirationTime { get; init; }

    public DateTime? LoginExpirationLimit { get; init; }

    public int LoginExpirationPeriod { get; init; }

    public DateTime? LastLoginTime { get; init; }

    /// <summary><c>Users.SecretKey</c>。TOTP の共有鍵（Base32）。</summary>
    public string? SecretKey { get; init; }

    /// <summary><c>Users.EnableSecretKey</c>。Pleasanter で TOTP の登録を済ませているか。</summary>
    public bool EnableSecretKey { get; init; }

    public bool EnableSecondaryAuthentication { get; init; }

    public bool DisableSecondaryAuthentication { get; init; }

    /// <summary>TOTP を登録済み（共有鍵があり、有効）か。</summary>
    public bool HasTotp => EnableSecretKey && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>同じ人を指す、IdP 内で一意な鍵。</summary>
    public string UserKey => $"{TenantId}:{UserId}";
}

/// <summary>ユーザと、クレームに載せる周辺の情報。</summary>
public sealed record PleasanterUserProfile(
    PleasanterUser User,
    IReadOnlyList<string> MailAddresses,
    IReadOnlyList<string> Groups,
    string? DeptName)
{
    /// <summary>所属するグループの <c>GroupId</c>（<see cref="Groups"/> と同じ範囲）。</summary>
    public IReadOnlyList<int> GroupIds { get; init; } = [];

    /// <summary>先頭のメールアドレス（Pleasanter で最初に登録されたもの）。</summary>
    public string? PrimaryMail => MailAddresses.Count > 0 ? MailAddresses[0] : null;
}
