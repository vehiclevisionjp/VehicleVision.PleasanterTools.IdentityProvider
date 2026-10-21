using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>Web アプリ起動時のマイグレーションを制御する。</summary>
public static class DatabaseStartupMigration
{
    public const string AutoMigrateSetting = "IDP_DB_AUTO_MIGRATE";
    public const string LockTimeoutSetting = "IDP_DB_MIGRATION_LOCK_TIMEOUT_SECONDS";
    public const string SkipCheckSetting = "IDP_DB_SKIP_MIGRATION_CHECK";
    public const int DefaultLockTimeoutSeconds = 300;

    /// <summary>マイグレーション完了前にも応答する生存確認経路か。</summary>
    public static bool IsAvailableBeforeMigration(PathString path) =>
        string.Equals(path.Value, "/healthz", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path.Value, "/ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>設定に従って自動適用または未適用検査を行う。</summary>
    public static async Task RunAsync(
        IConfiguration configuration,
        DatabaseProvider provider,
        string connectionString,
        DatabaseStartupState state,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);

        var autoMigrateSetting = configuration[AutoMigrateSetting];
        var autoMigrate = ReadAutoMigrate(autoMigrateSetting);
        var skipCheck = IsTrue(configuration[SkipCheckSetting]);

        try
        {
            if (autoMigrate)
            {
                if (string.IsNullOrWhiteSpace(autoMigrateSetting))
                {
                    logger.LogInformation(
                        "Automatic database migration is enabled by default. Set {Setting}=false to disable it.",
                        AutoMigrateSetting);
                }

                var timeout = ReadLockTimeout(configuration[LockTimeoutSetting]);
                var result = await IdentityProviderDatabase.Instance.Migrator.MigrateUpWithLockAsync(
                    provider,
                    connectionString,
                    timeout,
                    progress => LogProgress(logger, progress, timeout),
                    cancellationToken).ConfigureAwait(false);

                logger.LogInformation(
                    "Database migration completed successfully. Applied {MigrationCount} migration(s).",
                    result.AppliedCount);
            }
            else if (!skipCheck)
            {
                var pendingMigrations = IdentityProviderDatabase.Instance.Migrator.PendingMigrations(provider, connectionString);
                if (pendingMigrations.Count > 0)
                {
                    throw new InvalidOperationException(
                        "The database schema is out of date. Pending migrations: "
                        + string.Join(" / ", pendingMigrations)
                        + $". Set {AutoMigrateSetting}=true or run this executable with --migrate"
                        + " (see _documents/導入-更新運用手順書.md).");
                }

                logger.LogInformation("Database schema is up to date. No migrations were applied.");
            }
            else
            {
                logger.LogWarning(
                    "Database migration check is disabled by {Setting}.",
                    SkipCheckSetting);
            }

            var migrationStatus = skipCheck && !autoMigrate
                ? null
                : IdentityProviderDatabase.Instance.Migrator.GetStatus(provider, connectionString);
            state.MarkReady(migrationStatus);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogCritical(exception, "Database startup migration failed. Application startup is aborted.");
            throw;
        }
    }

    /// <summary>ロック待機時間の設定値を読む。</summary>
    public static TimeSpan ReadLockTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.FromSeconds(DefaultLockTimeoutSeconds);
        }

        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds)
            || seconds <= 0)
        {
            throw new InvalidOperationException(
                $"{LockTimeoutSetting} must be a positive integer.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>起動時の自動マイグレーション設定を読む。</summary>
    public static bool ReadAutoMigrate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException(
            $"{AutoMigrateSetting} must be either true or false.");
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static void LogProgress(
        ILogger logger,
        MigrationProgress progress,
        TimeSpan timeout)
    {
        switch (progress.Kind)
        {
            case MigrationProgressKind.WaitingForLock:
                logger.LogInformation(
                    "Waiting for the database migration lock for up to {TimeoutSeconds} seconds.",
                    timeout.TotalSeconds);
                break;
            case MigrationProgressKind.LockAcquired:
                logger.LogInformation("Database migration lock acquired.");
                break;
            case MigrationProgressKind.Applying:
                logger.LogInformation(
                    "Applying {MigrationCount} database migration(s).",
                    progress.MigrationCount);
                break;
        }
    }
}
