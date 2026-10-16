using Dapper;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>残す操作 1 件。</summary>
/// <param name="OccurredAt">起きた時刻（UTC）。</param>
/// <param name="ActorUserKey">誰が（<c>{TenantId}:{UserId}</c>）。**認証を通っていない試みでは <c>null</c>。**</param>
/// <param name="ActorLoginId">誰が（ログイン ID。存在しない ID での試みも残す）。</param>
/// <param name="Action">何を。<c>auth.login.failure</c> / <c>PUT /api/admin/oidc-clients/{clientId}</c> のような形。</param>
/// <param name="StatusCode">結果の HTTP 状態。**失敗も残す。**</param>
/// <param name="TargetType">対象の種類。</param>
/// <param name="TargetId">対象の識別子。</param>
/// <param name="DetailJson">補足。**資格情報・シークレット・トークン・TOTP の数字を入れない。**</param>
/// <param name="IpAddress">送信元。</param>
public sealed record AuditEntry(
    DateTime OccurredAt,
    string? ActorUserKey,
    string? ActorLoginId,
    string Action,
    int? StatusCode,
    string? TargetType = null,
    string? TargetId = null,
    string? DetailJson = null,
    string? IpAddress = null);

/// <summary>認証と管理操作の記録を読み書きする。</summary>
/// <remarks>
/// <para>
/// **入れないことを設計で担保する。** 記録するのは操作の名前・結果・対象の識別子だけで、
/// 要求の本文には触れない。触らない限り、パスワードもシークレットも入りようがない。
/// </para>
/// <para>**失敗した操作も残す。** 成功だけ残すと、試みられたことが分からない。</para>
/// </remarks>
public interface IAuditLogStore
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>新しい順に読む。</summary>
    Task<IReadOnlyList<AuditLogRow>> ListAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>指定した時刻より古い記録を消す。</summary>
    Task<int> DeleteOlderThanAsync(DateTime threshold, CancellationToken cancellationToken = default);
}

/// <summary>記録を読むときの絞り込み。既定は「直近を新しい順に」。</summary>
public sealed record AuditLogQuery
{
    public int Limit { get; init; } = 100;

    public int Offset { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    /// <summary>ログイン ID に含まれる文字。</summary>
    public string? Actor { get; init; }

    /// <summary>断られた操作だけ（400 以上）。</summary>
    public bool FailedOnly { get; init; }

    /// <summary>操作の名前に含まれる文字。</summary>
    public string? ActionContains { get; init; }
}

/// <summary>画面に出す記録 1 件。</summary>
public sealed record AuditLogRow(
    DateTime OccurredAt,
    string? ActorUserKey,
    string? ActorLoginId,
    string Action,
    int? StatusCode,
    string? TargetType,
    string? TargetId,
    string? DetailJson,
    string? IpAddress);

public sealed class AuditLogStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), IAuditLogStore
{
    /// <summary>列の桁。**溢れさせずに切り詰める。** 記録できなかったより、切り詰めた方がまし。</summary>
    private const int ActionLength = 128;

    private const int TargetTypeLength = 128;

    private const int TargetIdLength = 512;

    private const int LoginIdLength = 256;

    private const int UserKeyLength = 64;

    private const int IpAddressLength = 64;

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AuditLogs] "
            + "([AuditLogId], [OccurredAt], [ActorUserKey], [ActorLoginId], [Action], [StatusCode], "
            + " [TargetType], [TargetId], [DetailJson], [IpAddress]) "
            + "VALUES (@AuditLogId, @OccurredAt, @ActorUserKey, @ActorLoginId, @Action, @StatusCode, "
            + "        @TargetType, @TargetId, @DetailJson, @IpAddress)",
            new
            {
                AuditLogId = Guid.NewGuid(),
                OccurredAt = Db(entry.OccurredAt),
                ActorUserKey = Trim(entry.ActorUserKey, UserKeyLength),
                ActorLoginId = Trim(entry.ActorLoginId, LoginIdLength),
                Action = Trim(entry.Action, ActionLength),
                entry.StatusCode,
                TargetType = Trim(entry.TargetType, TargetTypeLength),
                TargetId = Trim(entry.TargetId, TargetIdLength),
                entry.DetailJson,
                IpAddress = Trim(entry.IpAddress, IpAddressLength),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditLogRow>> ListAsync(
        AuditLogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);

        // **組み立てるのは条件の「形」だけで、値は必ず引数で渡す。**
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("Limit", query.Limit);
        parameters.Add("Offset", query.Offset);

        if (query.From is { } from)
        {
            conditions.Add("[OccurredAt] >= @From");
            parameters.Add("From", Db(from));
        }

        if (query.To is { } to)
        {
            conditions.Add("[OccurredAt] < @To");
            parameters.Add("To", Db(to));
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            conditions.Add($"[ActorLoginId] LIKE @ActorLike ESCAPE '{SqlDialect.LikeEscape}'");
            parameters.Add("ActorLike", $"%{SqlDialect.EscapeLike(query.Actor.Trim())}%");
        }

        if (query.FailedOnly)
        {
            conditions.Add("[StatusCode] >= 400");
        }

        if (!string.IsNullOrWhiteSpace(query.ActionContains))
        {
            conditions.Add($"[Action] LIKE @ActionLike ESCAPE '{SqlDialect.LikeEscape}'");
            parameters.Add("ActionLike", $"%{SqlDialect.EscapeLike(query.ActionContains.Trim())}%");
        }

        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions) + " ";
        var sql =
            "SELECT [OccurredAt], [ActorUserKey], [ActorLoginId], [Action], [StatusCode], [TargetType], "
            + "[TargetId], [DetailJson], [IpAddress] FROM [AuditLogs] "
            + where
            + "ORDER BY [OccurredAt] DESC, [AuditLogId] DESC "
            + SqlDialect.Page(Provider);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AuditLogRow>(
            Sql(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(row => row with { OccurredAt = DbTime.AsUtc(row.OccurredAt) })];
    }

    public async Task<int> DeleteOlderThanAsync(DateTime threshold, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [AuditLogs] WHERE [OccurredAt] < @Threshold",
            new { Threshold = Db(threshold) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static string? Trim(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
