using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>管理画面へ入れる Pleasanter 上の役割。</summary>
public enum AdminRole
{
    /// <summary>システム管理者（<c>Users.ServiceManager</c>）だけ。</summary>
    ServiceManager,

    /// <summary>テナント管理者（<c>Users.TenantManager</c>）以上。**既定。**</summary>
    TenantManager,
}

/// <summary>IdP の動作設定。起動時に 1 度読む。</summary>
/// <remarks>
/// <para>
/// 設定は <c>App_Data/Parameters/*.json</c> と環境変数から来る
/// （<see cref="Services.ParameterFiles"/>、<c>App_Data/Parameters/README.md</c>）。
/// **書き間違いは起動時に止める。** 黙って既定へ落とすと、締め出しを 5 回にしたつもりで
/// 無制限のまま動くといったことが起きる。
/// </para>
/// <para>**例外の文言は英語。** Kudu の Debug console では日本語が化ける。</para>
/// </remarks>
public sealed record IdpOptions
{
    public const string IssuerSetting = "IDP_ISSUER";
    public const string DefaultIssuer = "http://localhost:8081";

    /// <summary>外から見える IdP の URL（末尾の <c>/</c> なし、サブパス込み）。<c>iss</c> と SAML の EntityID になる。</summary>
    public required string Issuer { get; init; }

    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(8);

    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan AuthorizationCodeLifetime { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan IdTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan SamlAssertionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public int LoginMaxFailures { get; init; } = 5;

    public TimeSpan LoginFailureWindow { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan LoginLockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

    public TotpPolicy Totp { get; init; } = TotpPolicy.Default;

    public AdminRole AdminRole { get; init; } = AdminRole.TenantManager;

    /// <summary>空なら <see cref="AdminRole"/> を満たす全員。値があれば、その中でさらに絞る。</summary>
    public IReadOnlyList<string> AdminLoginIds { get; init; } = [];

    /// <summary>管理画面へ入れる人へ重ねる Include / Exclude（<c>IDP_ADMIN_INCLUDE</c> / <c>IDP_ADMIN_EXCLUDE</c>）。</summary>
    /// <remarks>
    /// <see cref="AdminRole"/> と <see cref="AdminLoginIds"/> を満たしたうえで、さらにこれを通る人だけが入れる。
    /// **設定でだけ持つ**（管理画面から変えられると、権限の奪い合いと締め出しの道になる）。
    /// </remarks>
    public IReadOnlyList<AccessRule> AdminAccessRules { get; init; } = [];

    /// <summary>Issuer が https で配信されているか。cookie の <c>Secure</c> を決める。</summary>
    public bool IsSecure => Issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static IdpOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var defaults = new IdpOptions { Issuer = DefaultIssuer };
        var issuer = (configuration[IssuerSetting] ?? DefaultIssuer).Trim().TrimEnd('/');
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            || issuerUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(issuerUri.Query)
            || !string.IsNullOrEmpty(issuerUri.Fragment))
        {
            throw new InvalidOperationException(
                $"{IssuerSetting} must be an absolute http(s) URL without query or fragment. Current value: {issuer}");
        }

        var transport = TransportSecurityOptions.FromConfiguration(configuration);
        if (issuerUri.Scheme == "http" && !transport.AllowInsecure && !issuerUri.IsLoopback
            && !string.Equals(issuerUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            // **ログイン画面とトークンを平文で流す構成は、運用者に明示させる。**
            throw new InvalidOperationException(
                $"{IssuerSetting} must use https unless {TransportSecurityOptions.AllowInsecureSetting}=true "
                + "(loopback hosts are always allowed).");
        }

        var tolerances = Int(configuration, "SecondaryAuthentication:CountTolerances", 1, 0, 3);

        return new IdpOptions
        {
            Issuer = issuer,
            SessionLifetime = Minutes(configuration, "IDP_SESSION_LIFETIME_MINUTES", defaults.SessionLifetime, 5, 60 * 24 * 30),
            SessionIdleTimeout = Minutes(configuration, "IDP_SESSION_IDLE_MINUTES", defaults.SessionIdleTimeout, 1, 60 * 24 * 30),
            AuthorizationCodeLifetime = Seconds(configuration, "IDP_AUTHORIZATION_CODE_LIFETIME_SECONDS", defaults.AuthorizationCodeLifetime, 10, 600),
            AccessTokenLifetime = Seconds(configuration, "IDP_ACCESS_TOKEN_LIFETIME_SECONDS", defaults.AccessTokenLifetime, 60, 86400),
            IdTokenLifetime = Seconds(configuration, "IDP_ID_TOKEN_LIFETIME_SECONDS", defaults.IdTokenLifetime, 60, 86400),
            RefreshTokenLifetime = Seconds(configuration, "IDP_REFRESH_TOKEN_LIFETIME_SECONDS", defaults.RefreshTokenLifetime, 300, 31_536_000),
            SamlAssertionLifetime = Seconds(configuration, "IDP_SAML_ASSERTION_LIFETIME_SECONDS", defaults.SamlAssertionLifetime, 30, 3600),
            LoginMaxFailures = Int(configuration, "IDP_LOGIN_MAX_FAILURES", defaults.LoginMaxFailures, 1, 100),
            LoginFailureWindow = Minutes(configuration, "IDP_LOGIN_FAILURE_WINDOW_MINUTES", defaults.LoginFailureWindow, 1, 1440),
            LoginLockoutDuration = Minutes(configuration, "IDP_LOGIN_LOCKOUT_MINUTES", defaults.LoginLockoutDuration, 1, 1440),
            Totp = new TotpPolicy(
                Enum(configuration, "IDP_TOTP_MODE", TotpMode.Auto),
                Enum(configuration, "SecondaryAuthentication:Mode", PleasanterSecondaryAuthenticationMode.None),
                tolerances),
            AdminRole = Enum(configuration, "IDP_ADMIN_ROLE", AdminRole.TenantManager),
            AdminAccessRules = AdminRules(configuration),
            AdminLoginIds =
            [
                .. (string.IsNullOrWhiteSpace(configuration["IDP_ADMIN_LOGIN_IDS"])
                        ? Values(configuration, "Admin:LoginIds")
                        : (configuration["IDP_ADMIN_LOGIN_IDS"] ?? string.Empty)
                            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Select(id => id.ToLowerInvariant()),
            ],
        };
    }

    /// <summary><c>Admin.json</c> の <c>Include</c> / <c>Exclude</c>（配列）か、環境変数の文字列から読む。</summary>
    /// <remarks>
    /// <b>環境変数（<c>IDP_ADMIN_INCLUDE</c> / <c>IDP_ADMIN_EXCLUDE</c>）があれば、その側（Include / Exclude）の
    /// ファイルの値の代わりに使う。</b>
    /// </remarks>
    private static List<AccessRule> AdminRules(IConfiguration configuration)
    {
        try
        {
            return
            [
                .. AdminRulesOf(configuration, "IDP_ADMIN_INCLUDE", "Admin:Include", AccessRules.Include),
                .. AdminRulesOf(configuration, "IDP_ADMIN_EXCLUDE", "Admin:Exclude", AccessRules.Exclude),
            ];
        }
        catch (FormatException exception)
        {
            // **誤った設定で黙って全員を通さない。** 起動で止める
            throw new InvalidOperationException("管理画面の Include / Exclude の設定: " + exception.Message, exception);
        }
    }

    private static IEnumerable<AccessRule> AdminRulesOf(IConfiguration configuration, string envKey, string section, string effect)
    {
        if (!string.IsNullOrWhiteSpace(configuration[envKey]))
        {
            return AccessRules.ParseList(configuration[envKey], effect);
        }

        return
        [
            .. Values(configuration, section + ":Users").Select(id => ParseId(id, AccessRules.User, effect)),
            .. Values(configuration, section + ":Depts").Select(id => ParseId(id, AccessRules.Dept, effect)),
            .. Values(configuration, section + ":Groups").Select(id => ParseId(id, AccessRules.Group, effect)),
        ];
    }

    private static AccessRule ParseId(string text, string kind, string effect) =>
        AccessRules.ParseList($"{kind}:{text}", effect)[0];

    /// <summary>配列（<c>キー:0</c>, <c>キー:1</c>…）の値。空の要素は除く。</summary>
    private static IEnumerable<string> Values(IConfiguration configuration, string key) =>
        configuration.GetSection(key).GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!);

    /// <summary>1 以上の整数（上限は 1,000,000）。未設定なら既定値。</summary>
    public static int PositiveInt(IConfiguration configuration, string key, int fallback) =>
        Int(configuration, key, fallback, 1, 1_000_000);

    private static int Int(IConfiguration configuration, string key, int fallback, int min, int max)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
        {
            throw new InvalidOperationException($"{key} must be an integer from {min} to {max}. Current value: {raw}");
        }

        return value;
    }

    private static TimeSpan Minutes(IConfiguration configuration, string key, TimeSpan fallback, int min, int max) =>
        TimeSpan.FromMinutes(Int(configuration, key, (int)fallback.TotalMinutes, min, max));

    private static TimeSpan Seconds(IConfiguration configuration, string key, TimeSpan fallback, int min, int max) =>
        TimeSpan.FromSeconds(Int(configuration, key, (int)fallback.TotalSeconds, min, max));

    /// <summary>**知らない値は落とす。** 黙って既定へ落ちると、設定したつもりで効いていない。</summary>
    private static T Enum<T>(IConfiguration configuration, string key, T fallback)
        where T : struct, System.Enum
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!System.Enum.TryParse<T>(raw.Trim(), ignoreCase: true, out var value) || !System.Enum.IsDefined(value))
        {
            throw new InvalidOperationException(
                $"{key} must be one of: {string.Join(", ", System.Enum.GetNames<T>())}. Current value: {raw}");
        }

        return value;
    }
}
