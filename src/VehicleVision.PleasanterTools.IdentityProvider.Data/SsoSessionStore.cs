using Dapper;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>IdP へのログイン状態（SSO セッション）。</summary>
public sealed record SsoSessionEntry
{
    public Guid SessionId { get; init; }

    public int TenantId { get; init; }

    public int UserId { get; init; }

    public string LoginId { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    /// <summary><c>pwd</c> / <c>otp</c>。</summary>
    public IReadOnlyList<string> AuthMethods { get; init; } = [];

    public DateTime AuthTime { get; init; }

    public DateTime LastSeenAt { get; init; }

    public DateTime ExpiresAt { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }
}

/// <summary>このセッションで、どの RP / SP へ発行したか。</summary>
public sealed record SessionParticipant(
    Guid SessionId,
    string Kind,
    string ClientKey,
    string? SessionIndex,
    string? NameId)
{
    public const string Oidc = "oidc";

    public const string Saml = "saml";
}

public interface ISsoSessionStore
{
    Task CreateAsync(SsoSessionEntry entry, CancellationToken cancellationToken = default);

    /// <summary>失効しておらず、絶対期限・アイドル期限のどちらも過ぎていないものだけ。</summary>
    Task<SsoSessionEntry?> FindActiveAsync(
        Guid sessionId,
        DateTime nowUtc,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken = default);

    Task TouchAsync(Guid sessionId, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// 失効（ログアウト・管理者による失効）済みか。**行が無いものは失効扱いにしない。**
    /// 古い行は掃除で消えるが、保持期間はアクセストークンの最長の寿命より長い（<c>ExpiredDataStore</c>）。
    /// </summary>
    Task<bool> IsRevokedAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(Guid sessionId, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>同じ利用者の有効なセッションをすべて失効させる。</summary>
    Task<int> RevokeAllForUserAsync(int userId, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SsoSessionEntry>> ListActiveAsync(
        string? loginId,
        DateTime nowUtc,
        TimeSpan idleTimeout,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    Task AddParticipantAsync(SessionParticipant participant, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionParticipant>> ListParticipantsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}

public sealed class SsoSessionStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), ISsoSessionStore
{
    private const string Columns =
        "[SessionId], [TenantId], [UserId], [LoginId], [UserName], [AuthMethods], [AuthTime], "
        + "[LastSeenAt], [ExpiresAt], [IpAddress], [UserAgent]";

    public async Task CreateAsync(SsoSessionEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [SsoSessions] (" + Columns + ") VALUES (@SessionId, @TenantId, @UserId, @LoginId, "
            + "@UserName, @AuthMethods, @AuthTime, @LastSeenAt, @ExpiresAt, @IpAddress, @UserAgent)",
            new
            {
                entry.SessionId,
                entry.TenantId,
                entry.UserId,
                entry.LoginId,
                UserName = Trim(entry.UserName, 256) ?? string.Empty,
                AuthMethods = string.Join(',', entry.AuthMethods),
                AuthTime = Db(entry.AuthTime),
                LastSeenAt = Db(entry.LastSeenAt),
                ExpiresAt = Db(entry.ExpiresAt),
                IpAddress = Trim(entry.IpAddress, 64),
                UserAgent = Trim(entry.UserAgent, 512),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SsoSessionEntry?> FindActiveAsync(
        Guid sessionId,
        DateTime nowUtc,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT " + Columns + " FROM [SsoSessions] WHERE [SessionId] = @SessionId "
            + "AND [RevokedAt] IS NULL AND [ExpiresAt] > @Now AND [LastSeenAt] > @IdleLimit",
            new { SessionId = sessionId, Now = Db(nowUtc), IdleLimit = Db(nowUtc - idleTimeout) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToEntry(row);
    }

    public async Task TouchAsync(Guid sessionId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "UPDATE [SsoSessions] SET [LastSeenAt] = @Now WHERE [SessionId] = @SessionId AND [RevokedAt] IS NULL",
            new { SessionId = sessionId, Now = Db(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> IsRevokedAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var count = await connection.ExecuteScalarAsync<int>(Sql(
            "SELECT COUNT(*) FROM [SsoSessions] WHERE [SessionId] = @SessionId AND [RevokedAt] IS NOT NULL",
            new { SessionId = sessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    public async Task<bool> RevokeAsync(Guid sessionId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [SsoSessions] SET [RevokedAt] = @Now WHERE [SessionId] = @SessionId AND [RevokedAt] IS NULL",
            new { SessionId = sessionId, Now = Db(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<int> RevokeAllForUserAsync(
        int userId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "UPDATE [SsoSessions] SET [RevokedAt] = @Now WHERE [UserId] = @UserId AND [RevokedAt] IS NULL",
            new { UserId = userId, Now = Db(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SsoSessionEntry>> ListActiveAsync(
        string? loginId,
        DateTime nowUtc,
        TimeSpan idleTimeout,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var filter = string.IsNullOrWhiteSpace(loginId)
            ? string.Empty
            : $" AND LOWER([LoginId]) LIKE @LoginLike ESCAPE '{SqlDialect.LikeEscape}'";
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(Sql(
            "SELECT " + Columns + " FROM [SsoSessions] WHERE [RevokedAt] IS NULL AND [ExpiresAt] > @Now "
            + "AND [LastSeenAt] > @IdleLimit" + filter
            + " ORDER BY [LastSeenAt] DESC, [SessionId] " + SqlDialect.Page(Provider),
            new
            {
                Now = Db(nowUtc),
                IdleLimit = Db(nowUtc - idleTimeout),
                LoginLike = string.IsNullOrWhiteSpace(loginId) ? null : $"%{SqlDialect.EscapeLike(loginId.Trim().ToLowerInvariant())}%",
                Limit = limit,
                Offset = offset,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(ToEntry)];
    }

    public async Task AddParticipantAsync(
        SessionParticipant participant,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // 同じ RP / SP へ何度発行しても 1 行（ログアウト時の通知先を重複させない）
        var exists = await connection.ExecuteScalarAsync<int>(Sql(
            "SELECT COUNT(*) FROM [SessionParticipants] "
            + "WHERE [SessionId] = @SessionId AND [Kind] = @Kind AND [ClientKey] = @ClientKey",
            new { participant.SessionId, participant.Kind, participant.ClientKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists > 0)
        {
            return;
        }

        await connection.ExecuteAsync(Sql(
            "INSERT INTO [SessionParticipants] "
            + "([ParticipantId], [SessionId], [Kind], [ClientKey], [SessionIndex], [NameId], [CreatedAt]) "
            + "VALUES (@ParticipantId, @SessionId, @Kind, @ClientKey, @SessionIndex, @NameId, @CreatedAt)",
            new
            {
                ParticipantId = Guid.NewGuid(),
                participant.SessionId,
                participant.Kind,
                ClientKey = Trim(participant.ClientKey, 512),
                SessionIndex = Trim(participant.SessionIndex, 128),
                NameId = Trim(participant.NameId, 512),
                CreatedAt = Db(nowUtc),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionParticipant>> ListParticipantsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ParticipantRow>(Sql(
            "SELECT [SessionId], [Kind], [ClientKey], [SessionIndex], [NameId] FROM [SessionParticipants] "
            + "WHERE [SessionId] = @SessionId ORDER BY [CreatedAt]",
            new { SessionId = sessionId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(row => new SessionParticipant(
            row.SessionId, row.Kind, row.ClientKey, row.SessionIndex, row.NameId))];
    }

    private static string? Trim(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private static SsoSessionEntry ToEntry(Row row) => new()
    {
        SessionId = row.SessionId,
        TenantId = row.TenantId,
        UserId = row.UserId,
        LoginId = row.LoginId,
        UserName = row.UserName,
        AuthMethods = row.AuthMethods.Split(',', StringSplitOptions.RemoveEmptyEntries),
        AuthTime = DbTime.AsUtc(row.AuthTime),
        LastSeenAt = DbTime.AsUtc(row.LastSeenAt),
        ExpiresAt = DbTime.AsUtc(row.ExpiresAt),
        IpAddress = row.IpAddress,
        UserAgent = row.UserAgent,
    };

    private sealed class Row
    {
        public Guid SessionId { get; set; }

        public int TenantId { get; set; }

        public int UserId { get; set; }

        public string LoginId { get; set; } = string.Empty;

        public string UserName { get; set; } = string.Empty;

        public string AuthMethods { get; set; } = string.Empty;

        public DateTime AuthTime { get; set; }

        public DateTime LastSeenAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public string? IpAddress { get; set; }

        public string? UserAgent { get; set; }
    }

    private sealed class ParticipantRow
    {
        public Guid SessionId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string ClientKey { get; set; } = string.Empty;

        public string? SessionIndex { get; set; }

        public string? NameId { get; set; }
    }
}
