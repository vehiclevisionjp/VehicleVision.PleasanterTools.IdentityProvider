using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>起動時マイグレーションを 1 インスタンスだけに絞る。</summary>
internal sealed class DatabaseMigrationLock : IAsyncDisposable
{
    private const string LockName = "idp-startup-migrations";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    private readonly DatabaseProvider provider;
    private readonly DbConnection? connection;
    private readonly bool sqliteTransaction;
    private bool disposed;

    private DatabaseMigrationLock(
        DatabaseProvider provider,
        DbConnection? connection,
        bool sqliteTransaction)
    {
        this.provider = provider;
        this.connection = connection;
        this.sqliteTransaction = sqliteTransaction;
    }

    /// <summary>期限までロックを待つ。</summary>
    public static async Task<DatabaseMigrationLock> AcquireAsync(
        DatabaseProvider provider,
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "待機時間は 0 より大きくする");
        }

        return provider is DatabaseProvider.Sqlite
            ? await AcquireSqliteAsync(connectionString, timeout, cancellationToken)
                .ConfigureAwait(false)
            : await AcquireDatabaseAsync(provider, connectionString, timeout, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<DatabaseMigrationLock> AcquireDatabaseAsync(
        DatabaseProvider provider,
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var connection = new DbConnectionFactory(
            provider,
            WithoutPooling(provider, connectionString)).Create();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var parameters = new
            {
                Name = DatabaseScopedLockName(connection.Database),
                Key = StableLockKey(LockName),
            };
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                var result = await connection.ExecuteScalarAsync<object?>(
                    new CommandDefinition(
                        SqlDialect.TryAcquireMigrationLock(provider),
                        parameters,
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                if (SqlDialect.MigrationLockAcquired(provider, result))
                {
                    return new DatabaseMigrationLock(provider, connection, false);
                }

                if (stopwatch.Elapsed >= timeout)
                {
                    throw new TimeoutException(
                        $"Migration lock was not acquired within {timeout.TotalSeconds:0} seconds.");
                }

                await Task.Delay(
                    RemainingDelay(timeout, stopwatch.Elapsed),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<DatabaseMigrationLock> AcquireSqliteAsync(
        string connectionString,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
        {
            throw new InvalidOperationException(
                "SQLite auto migration requires a file-backed database.");
        }

        var lockPath = Path.GetFullPath(dataSource) + ".migration-lock.sqlite";
        var lockConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = lockPath,
            Pooling = false,
            DefaultTimeout = 1,
        }.ConnectionString;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            var connection = new SqliteConnection(lockConnectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 1;
                // 空の SQLite ファイルを有効な DB に初期化してから、書き込み予約を取る。
                command.CommandText = "PRAGMA user_version = 1;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = "BEGIN IMMEDIATE;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return new DatabaseMigrationLock(DatabaseProvider.Sqlite, connection, true);
            }
            catch (SqliteException exception)
                when (exception.SqliteErrorCode == 5 && stopwatch.Elapsed < timeout)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(
                    RemainingDelay(timeout, stopwatch.Elapsed),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 5)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new TimeoutException(
                    $"Migration lock was not acquired within {timeout.TotalSeconds:0} seconds.",
                    exception);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (connection is not null)
            {
                if (sqliteTransaction)
                {
                    await connection.ExecuteAsync("ROLLBACK;").ConfigureAwait(false);
                }
                else
                {
                    await connection.ExecuteScalarAsync<object?>(
                        SqlDialect.ReleaseMigrationLock(provider),
                        new
                        {
                            Name = DatabaseScopedLockName(connection.Database),
                            Key = StableLockKey(LockName),
                        }).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan RemainingDelay(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining < RetryInterval ? remaining : RetryInterval;
    }

    private static string DatabaseScopedLockName(string database)
    {
        var suffix = $":{StableLockKey(database):x16}";
        var prefixLength = Math.Min(LockName.Length, 64 - suffix.Length);
        return LockName[..prefixLength] + suffix;
    }

    private static long StableLockKey(string value)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            hash ^= octet;
            hash *= prime;
        }

        return unchecked((long)hash);
    }

    private static string WithoutPooling(DatabaseProvider provider, string connectionString) =>
        provider switch
        {
            DatabaseProvider.SqlServer => new SqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
            }.ConnectionString,
            DatabaseProvider.PostgreSql => new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
            }.ConnectionString,
            DatabaseProvider.MySql => new MySqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
            }.ConnectionString,
            _ => connectionString,
        };
}
