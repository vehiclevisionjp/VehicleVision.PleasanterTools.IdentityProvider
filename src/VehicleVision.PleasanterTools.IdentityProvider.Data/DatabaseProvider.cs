using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>対応する RDBMS。</summary>
/// <remarks>
/// **原則は Pleasanter が対応する 3 つに揃える**（<c>_documents/アーキテクチャ方針.md</c> 13 章）。
/// 導入先の Pleasanter が使う RDBMS に合わせられないと、DB サーバを別に立てることになる。
/// SQLite は簡易セットアップとデバッグだけに使う例外で、本番運用には使わない。
/// </remarks>
public enum DatabaseProvider
{
    SqlServer,
    PostgreSql,
    MySql,
    Sqlite,
}

/// <summary>DB 接続文字列の既定値を決める。</summary>
public static class DatabaseConnectionString
{
    /// <summary>SQLite の既定ファイル名。</summary>
    public const string DefaultSqliteFileName = "idp.db";

    /// <summary>設定値を解決する。SQLite だけは App_Data 配下のファイルを既定に持つ。</summary>
    public static string Resolve(
        DatabaseProvider provider,
        string? configured,
        string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (provider is not DatabaseProvider.Sqlite)
        {
            throw new InvalidOperationException("IDP_DB_CONNECTIONSTRING が設定されていない");
        }

        var appDataPath = Path.Combine(contentRootPath, "App_Data");
        Directory.CreateDirectory(appDataPath);
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(appDataPath, DefaultSqliteFileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString;
    }
}

/// <summary>接続を作る。</summary>
public interface IDbConnectionFactory
{
    DatabaseProvider Provider { get; }

    DbConnection Create();
}

/// <summary>設定から接続を作る。</summary>
/// <remarks>
/// <para>
/// **接続文字列は設定ファイルへ直書きしない。** 環境変数か Key Vault から読む
/// （<c>App_Data/Parameters/README.md</c>）。
/// </para>
/// <para>
/// **暗号化を既定値任せにしない。** 与えられた接続文字列が黙っているときは
/// こちらで <c>Encrypt=True</c> を立てる。ドライバの既定は版で変わり得るし
/// （Microsoft.Data.SqlClient は 4.0 で <c>true</c> へ変わった）、
/// **書かれていないものは読む側にも伝わらない**。
/// 切られていないかの検査は <see cref="ConnectionSecurity"/> が起動時に行う。
/// </para>
/// </remarks>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private const int SqliteBusyTimeoutSeconds = 30;
    private readonly string _connectionString;

    public DbConnectionFactory(DatabaseProvider provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        Provider = provider;
        _connectionString = provider switch
        {
            DatabaseProvider.SqlServer => Encrypted(connectionString),
            DatabaseProvider.Sqlite => ConfigureSqlite(connectionString),
            _ => connectionString,
        };
    }

    public DatabaseProvider Provider { get; }

    public DbConnection Create() => Provider switch
    {
        DatabaseProvider.SqlServer => new SqlConnection(_connectionString),
        DatabaseProvider.PostgreSql => new NpgsqlConnection(_connectionString),
        DatabaseProvider.MySql => new MySqlConnection(_connectionString),
        DatabaseProvider.Sqlite => CreateSqliteConnection(),
        _ => throw new NotSupportedException($"対応していない RDBMS: {Provider}"),
    };

    /// <summary>SQLite の待機時間を接続文字列へ強制する。</summary>
    private static string ConfigureSqlite(string connectionString)
    {
        SqliteTypeHandlers.Register();
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            DefaultTimeout = SqliteBusyTimeoutSeconds,
        };
        return builder.ConnectionString;
    }

    /// <summary>SQLite の同時書き込み設定を、開くたびに確実に適用する。</summary>
    private SqliteConnection CreateSqliteConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState is not ConnectionState.Open)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {SqliteBusyTimeoutSeconds * 1000};";
            command.ExecuteNonQuery();

            command.CommandText = "PRAGMA journal_mode = WAL;";
            var journalMode = command.ExecuteScalar()?.ToString();
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQLite WAL mode could not be enabled. Current journal mode: {journalMode ?? "(unknown)"}.");
            }

            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
        };
        return connection;
    }

    /// <summary>SQL Server 向けに暗号化を立てる。</summary>
    /// <remarks>
    /// <para>
    /// **<c>Encrypt=False</c> と書かれていても暗号化する。** ここは譲らない。
    /// 「暗号化しない」という選択肢を残すと、逃げ道の設定 1 つで平文になる。
    /// </para>
    /// <para>
    /// **<c>IDP_DB_ALLOW_INSECURE</c> が緩めるのは証明書の確認であって、
    /// 暗号化ではない。** 検証環境が自己署名の証明書を使うための逃げ道なので、
    /// <c>TrustServerCertificate</c> はそのまま通す。
    /// </para>
    /// <para>
    /// なお <c>Encrypt=False</c> と書いてあること自体は
    /// <see cref="ConnectionSecurity"/> が起動時に咎める。
    /// **できない指定を黙って読み替えるのではなく、言ってから守る。**
    /// </para>
    /// </remarks>
    private static string Encrypted(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.Encrypt = true;
        return builder.ConnectionString;
    }
}
