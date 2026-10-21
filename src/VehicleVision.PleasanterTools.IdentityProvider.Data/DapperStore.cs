using System.Data.Common;
using Dapper;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>Dapper を使う各ストアの共通部分。</summary>
/// <remarks>
/// **SQL は角括弧で識別子を囲んで書く。** <see cref="SqlDialect.Format"/> が RDBMS ごとの引用符へ直す。
/// 値は必ず引数で渡す（文字列連結で SQL を作らない）。
/// </remarks>
public abstract class DapperStore(IDbConnectionFactory connectionFactory)
{
    protected DatabaseProvider Provider => connectionFactory.Provider;

    protected CommandDefinition Sql(
        string sql,
        object? parameters = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default) =>
        new(SqlDialect.Format(Provider, sql), parameters, transaction, cancellationToken: cancellationToken);

    protected async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory.Create();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>キーの重複で失敗したか（INSERT の競合を見分ける）。</summary>
    protected static bool IsDuplicateKey(DbException exception) =>
        exception switch
        {
            Microsoft.Data.SqlClient.SqlException sql => sql.Number is 2627 or 2601,
            Npgsql.PostgresException pg => pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation,
            MySqlConnector.MySqlException my => my.ErrorCode == MySqlConnector.MySqlErrorCode.DuplicateKeyEntry,
            Microsoft.Data.Sqlite.SqliteException lite => lite.SqliteErrorCode == 19,
            _ => false,
        };

    /// <summary>DB へ渡す時刻（UTC・秒未満を切り捨て）。</summary>
    protected static DateTime Db(DateTime utc) => DbTime.ForDb(utc);
}
