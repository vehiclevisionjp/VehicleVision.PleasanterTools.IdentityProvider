using System.Data.Common;
using Dapper;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>結合テストで使う DB の接続先と、Pleasanter 相当の表。</summary>
/// <remarks>
/// <para>
/// **SQLite は常に動く**（ファイルを作るだけ）。SQL Server / PostgreSQL / MySQL は
/// <c>IDP_INTEGRATION=1</c> のときだけ実機へ当てる。
/// **設定していないと、実機の試験は何も検証せずに緑になる。その緑を「通った」と読まないこと。**
/// CI は必ず設定する（<c>.github/workflows/integration.yml</c>）。
/// </para>
/// <para>
/// 接続先は <c>compose.yaml</c> と <c>integration.yml</c> の値（localhost の 11433 / 15432 / 13306）。
/// パスワードは検証環境用の固定値で、本番の資格情報ではない。
/// </para>
/// </remarks>
internal static class TestDatabases
{
    public static bool RealDatabasesEnabled =>
        Environment.GetEnvironmentVariable("IDP_INTEGRATION") == "1";

    private static string Password =>
        Environment.GetEnvironmentVariable("TESTENV_SA_PASSWORD") ?? "IdentityProvider#Test1";

    /// <summary>この実行で試す RDBMS。</summary>
    public static IEnumerable<object[]> Providers()
    {
        yield return [DatabaseProvider.Sqlite];

        if (RealDatabasesEnabled)
        {
            yield return [DatabaseProvider.SqlServer];
            yield return [DatabaseProvider.PostgreSql];
            yield return [DatabaseProvider.MySql];
        }
    }

    /// <summary>実機の DB の接続文字列（SQLite は呼び出しごとに新しいファイル）。</summary>
    public static string ConnectionString(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            $"Server=localhost,11433;Database=IdentityProvider;UID=sa;Password={Password};TrustServerCertificate=True",
        DatabaseProvider.PostgreSql =>
            $"Host=localhost;Port=15432;Database=idp;Username=postgres;Password={Password}",
        DatabaseProvider.MySql =>
            $"Server=localhost;Port=13306;Database=idp;Uid=root;Password={Password};AllowUserVariables=True",
        DatabaseProvider.Sqlite =>
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Path.GetTempPath(), $"idp-test-{Guid.NewGuid():N}.db"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ConnectionString,
        _ => throw new NotSupportedException(provider.ToString()),
    };

    /// <summary>
    /// IdP 自身の表を空から作り直す。
    /// **実機の DB は、前の実行の表が残っているので、全部落としてから当てる。**
    /// </summary>
    public static async Task<DbConnectionFactory> FreshIdpDatabaseAsync(DatabaseProvider provider)
    {
        var connectionString = ConnectionString(provider);
        var factory = new DbConnectionFactory(provider, connectionString);
        if (provider is not DatabaseProvider.Sqlite)
        {
            await DropAllAsync(factory, IdpTables).ConfigureAwait(false);
            await DropAllAsync(factory, ["VersionInfo"]).ConfigureAwait(false);
        }

        await DatabaseMigrator.MigrateUpWithLockAsync(
            provider, connectionString, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        return factory;
    }

    private static readonly string[] IdpTables =
    [
        "AuditLogs", "PendingRequests", "TotpUsedSteps", "LoginFailures", "RefreshTokens",
        "AuthorizationCodes", "SessionParticipants", "SsoSessions", "SigningKeys", "SamlProviders", "OidcClients",
    ];

    private static async Task DropAllAsync(DbConnectionFactory factory, IEnumerable<string> tables)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        foreach (var table in tables)
        {
            await connection.ExecuteAsync(SqlDialect.Format(
                factory.Provider, $"DROP TABLE IF EXISTS [{table}]")).ConfigureAwait(false);
        }
    }
}

/// <summary>Pleasanter の <c>Users</c> などと同じ列名の表（この IdP が読む列だけ）。</summary>
/// <remarks>
/// 表と列の名前は <c>_reference/Implem.Pleasanter</c> の定義（<c>Definition_Column</c>）に合わせてある。
/// 型は RDBMS ごとに Pleasanter が使うものへ寄せた。
/// </remarks>
internal static class PleasanterSchema
{
    private static readonly string[] Tables = ["GroupMembers", "Groups", "Depts", "MailAddresses", "Users"];

    public static async Task CreateAsync(DbConnectionFactory factory)
    {
        var provider = factory.Provider;
        string Bool() => provider switch
        {
            DatabaseProvider.SqlServer => "bit",
            DatabaseProvider.PostgreSql => "boolean",
            DatabaseProvider.MySql => "tinyint(1)",
            _ => "INTEGER",
        };

        string False() => provider is DatabaseProvider.PostgreSql ? "false" : "0";

        string Text(int length) => provider switch
        {
            DatabaseProvider.SqlServer => $"nvarchar({length})",
            DatabaseProvider.Sqlite => "TEXT",
            _ => $"varchar({length})",
        };

        string Time() => provider switch
        {
            DatabaseProvider.PostgreSql => "timestamp",
            DatabaseProvider.Sqlite => "TEXT",
            _ => "datetime",
        };

        var statements = new[]
        {
            $"CREATE TABLE [Users] ([TenantId] int NOT NULL, [UserId] int NOT NULL PRIMARY KEY, [LoginId] {Text(256)} NOT NULL, "
            + $"[GlobalId] {Text(36)} NULL, [Name] {Text(128)} NULL, [UserCode] {Text(32)} NULL, [Language] {Text(32)} NULL, "
            + $"[TimeZone] {Text(32)} NULL, [DeptId] int NOT NULL DEFAULT 0, [Disabled] {Bool()} NOT NULL DEFAULT {False()}, "
            + $"[Lockout] {Bool()} NOT NULL DEFAULT {False()}, [TenantManager] {Bool()} NOT NULL DEFAULT {False()}, "
            + $"[ServiceManager] {Bool()} NOT NULL DEFAULT {False()}, [Password] {Text(128)} NULL, [PasswordExpirationTime] {Time()} NULL, "
            + $"[LoginExpirationLimit] {Time()} NULL, [LoginExpirationPeriod] int NULL DEFAULT 0, [LastLoginTime] {Time()} NULL, "
            + $"[SecretKey] {Text(128)} NULL, [EnableSecretKey] {Bool()} NOT NULL DEFAULT {False()}, "
            + $"[EnableSecondaryAuthentication] {Bool()} NOT NULL DEFAULT {False()}, "
            + $"[DisableSecondaryAuthentication] {Bool()} NOT NULL DEFAULT {False()})",
            $"CREATE TABLE [MailAddresses] ([MailAddressId] bigint NOT NULL PRIMARY KEY, [OwnerId] bigint NOT NULL, "
            + $"[OwnerType] {Text(32)} NOT NULL, [MailAddress] {Text(2048)} NOT NULL)",
            $"CREATE TABLE [Depts] ([TenantId] int NOT NULL, [DeptId] int NOT NULL PRIMARY KEY, [DeptName] {Text(1024)} NOT NULL, "
            + $"[Disabled] {Bool()} NOT NULL DEFAULT {False()})",
            $"CREATE TABLE [Groups] ([TenantId] int NOT NULL, [GroupId] int NOT NULL PRIMARY KEY, [GroupName] {Text(256)} NOT NULL, "
            + $"[Disabled] {Bool()} NOT NULL DEFAULT {False()})",
            $"CREATE TABLE [GroupMembers] ([GroupId] int NOT NULL, [DeptId] int NOT NULL DEFAULT 0, "
            + $"[UserId] int NOT NULL DEFAULT 0, [ChildGroup] {Bool()} NOT NULL DEFAULT {False()}, [Admin] {Bool()} NOT NULL DEFAULT {False()})",
        };

        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        foreach (var table in Tables)
        {
            await connection.ExecuteAsync(SqlDialect.Format(provider, $"DROP TABLE IF EXISTS [{table}]")).ConfigureAwait(false);
        }

        foreach (var statement in statements.Reverse())
        {
            await connection.ExecuteAsync(SqlDialect.Format(provider, statement)).ConfigureAwait(false);
        }
    }
}

/// <summary>Pleasanter 相当の表へ、テスト用のユーザを入れる。</summary>
internal sealed class PleasanterSeeder(DbConnectionFactory factory)
{
    private int nextUserId = 100;
    private long nextMailId = 1000;

    public DbConnectionFactory Factory => factory;

    public async Task<int> AddUserAsync(
        string loginId,
        string password,
        string name = "テスト 太郎",
        int tenantId = 1,
        bool disabled = false,
        bool lockout = false,
        bool tenantManager = false,
        bool serviceManager = false,
        string? secretKey = null,
        bool enableSecretKey = false,
        bool enableSecondaryAuthentication = false,
        bool disableSecondaryAuthentication = false,
        DateTime? passwordExpirationTime = null,
        string? mail = null,
        int deptId = 0,
        string? passwordHash = null)
    {
        var userId = Interlocked.Increment(ref nextUserId);
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);

        await connection.ExecuteAsync(SqlDialect.Format(factory.Provider,
            "INSERT INTO [Users] ([TenantId], [UserId], [LoginId], [GlobalId], [Name], [UserCode], [Language], [TimeZone], "
            + "[DeptId], [Disabled], [Lockout], [TenantManager], [ServiceManager], [Password], [PasswordExpirationTime], "
            + "[SecretKey], [EnableSecretKey], [EnableSecondaryAuthentication], [DisableSecondaryAuthentication]) "
            + "VALUES (@TenantId, @UserId, @LoginId, @GlobalId, @Name, @UserCode, @Language, @TimeZone, "
            + "@DeptId, @Disabled, @Lockout, @TenantManager, @ServiceManager, @Password, @PasswordExpirationTime, "
            + "@SecretKey, @EnableSecretKey, @EnableSecondaryAuthentication, @DisableSecondaryAuthentication)"),
            new
            {
                TenantId = tenantId,
                UserId = userId,
                LoginId = loginId,
                GlobalId = Guid.NewGuid().ToString(),
                Name = name,
                UserCode = "C" + userId,
                Language = "ja",
                TimeZone = "Tokyo Standard Time",
                DeptId = deptId,
                Disabled = disabled,
                Lockout = lockout,
                TenantManager = tenantManager,
                ServiceManager = serviceManager,
                Password = passwordHash ?? Core.Pleasanter.PleasanterPassword.Hash(password),
                PasswordExpirationTime = passwordExpirationTime,
                SecretKey = secretKey,
                EnableSecretKey = enableSecretKey,
                EnableSecondaryAuthentication = enableSecondaryAuthentication,
                DisableSecondaryAuthentication = disableSecondaryAuthentication,
            }).ConfigureAwait(false);

        if (mail is not null)
        {
            await AddMailAsync(connection, userId, mail).ConfigureAwait(false);
        }

        return userId;
    }

    public async Task AddMailAsync(DbConnection connection, int userId, string mail)
    {
        await connection.ExecuteAsync(SqlDialect.Format(factory.Provider,
            "INSERT INTO [MailAddresses] ([MailAddressId], [OwnerId], [OwnerType], [MailAddress]) "
            + "VALUES (@MailAddressId, @OwnerId, @OwnerType, @MailAddress)"),
            new { MailAddressId = Interlocked.Increment(ref nextMailId), OwnerId = (long)userId, OwnerType = "Users", MailAddress = mail })
            .ConfigureAwait(false);
    }

    public async Task AddDeptAsync(int tenantId, int deptId, string name, bool disabled = false)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(SqlDialect.Format(factory.Provider,
            "INSERT INTO [Depts] ([TenantId], [DeptId], [DeptName], [Disabled]) VALUES (@TenantId, @DeptId, @Name, @Disabled)"),
            new { TenantId = tenantId, DeptId = deptId, Name = name, Disabled = disabled }).ConfigureAwait(false);
    }

    public async Task AddGroupAsync(int tenantId, int groupId, string name, bool disabled = false)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(SqlDialect.Format(factory.Provider,
            "INSERT INTO [Groups] ([TenantId], [GroupId], [GroupName], [Disabled]) VALUES (@TenantId, @GroupId, @Name, @Disabled)"),
            new { TenantId = tenantId, GroupId = groupId, Name = name, Disabled = disabled }).ConfigureAwait(false);
    }

    public async Task AddGroupMemberAsync(int groupId, int userId = 0, int deptId = 0)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.ExecuteAsync(SqlDialect.Format(factory.Provider,
            "INSERT INTO [GroupMembers] ([GroupId], [DeptId], [UserId], [ChildGroup], [Admin]) "
            + "VALUES (@GroupId, @DeptId, @UserId, @ChildGroup, @Admin)"),
            new { GroupId = groupId, DeptId = deptId, UserId = userId, ChildGroup = false, Admin = false }).ConfigureAwait(false);
    }
}
