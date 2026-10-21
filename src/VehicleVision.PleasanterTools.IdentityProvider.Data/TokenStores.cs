using Dapper;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>認可コード 1 件。</summary>
public sealed record AuthorizationCodeEntry
{
    public string CodeHash { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public Guid SessionId { get; init; }

    public int TenantId { get; init; }

    public int UserId { get; init; }

    public string RedirectUri { get; init; } = string.Empty;

    public string Scope { get; init; } = string.Empty;

    public string CodeChallenge { get; init; } = string.Empty;

    public string? Nonce { get; init; }

    public DateTime AuthTime { get; init; }

    public string AuthMethods { get; init; } = string.Empty;

    public DateTime ExpiresAt { get; init; }

    /// <summary>このコードから出たトークンの系統。使い回しを検出したら、この系統を失効させる。</summary>
    public Guid FamilyId { get; init; }
}

public enum CodeConsumeStatus
{
    Consumed,
    NotFound,
    Expired,

    /// <summary>既に使われたコード。**盗まれて再利用された疑い。**</summary>
    Replayed,
}

public sealed record CodeConsumeResult(CodeConsumeStatus Status, AuthorizationCodeEntry? Entry);

/// <summary>認可コードの入れ物。**コードそのものは持たず、ハッシュだけ持つ。**</summary>
public interface IAuthorizationCodeStore
{
    Task CreateAsync(AuthorizationCodeEntry entry, CancellationToken cancellationToken = default);

    /// <summary>1 度だけ使える。同時に 2 つ来ても、成功するのは 1 つ。</summary>
    Task<CodeConsumeResult> ConsumeAsync(
        string codeHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class AuthorizationCodeStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), IAuthorizationCodeStore
{
    private const string Columns =
        "[CodeHash], [ClientId], [SessionId], [TenantId], [UserId], [RedirectUri], [Scope], "
        + "[CodeChallenge], [Nonce], [AuthTime], [AuthMethods], [ExpiresAt], [FamilyId]";

    public async Task CreateAsync(AuthorizationCodeEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [AuthorizationCodes] (" + Columns + ") VALUES (@CodeHash, @ClientId, @SessionId, "
            + "@TenantId, @UserId, @RedirectUri, @Scope, @CodeChallenge, @Nonce, @AuthTime, @AuthMethods, "
            + "@ExpiresAt, @FamilyId)",
            new
            {
                entry.CodeHash,
                entry.ClientId,
                entry.SessionId,
                entry.TenantId,
                entry.UserId,
                entry.RedirectUri,
                entry.Scope,
                entry.CodeChallenge,
                entry.Nonce,
                AuthTime = Db(entry.AuthTime),
                entry.AuthMethods,
                ExpiresAt = Db(entry.ExpiresAt),
                entry.FamilyId,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<CodeConsumeResult> ConsumeAsync(
        string codeHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT " + Columns + ", [ConsumedAt] FROM [AuthorizationCodes] WHERE [CodeHash] = @CodeHash",
            new { CodeHash = codeHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return new CodeConsumeResult(CodeConsumeStatus.NotFound, null);
        }

        var entry = ToEntry(row);
        if (row.ConsumedAt is not null)
        {
            return new CodeConsumeResult(CodeConsumeStatus.Replayed, entry);
        }

        if (entry.ExpiresAt <= nowUtc)
        {
            return new CodeConsumeResult(CodeConsumeStatus.Expired, entry);
        }

        // **条件付きの UPDATE で消費する。** 同時に来た 2 つのうち、1 つしか件数が 1 にならない
        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [AuthorizationCodes] SET [ConsumedAt] = @Now "
            + "WHERE [CodeHash] = @CodeHash AND [ConsumedAt] IS NULL",
            new { CodeHash = codeHash, Now = Db(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1
            ? new CodeConsumeResult(CodeConsumeStatus.Consumed, entry)
            : new CodeConsumeResult(CodeConsumeStatus.Replayed, entry);
    }

    private static AuthorizationCodeEntry ToEntry(Row row) => new()
    {
        CodeHash = row.CodeHash,
        ClientId = row.ClientId,
        SessionId = row.SessionId,
        TenantId = row.TenantId,
        UserId = row.UserId,
        RedirectUri = row.RedirectUri,
        Scope = row.Scope,
        CodeChallenge = row.CodeChallenge,
        Nonce = row.Nonce,
        AuthTime = DbTime.AsUtc(row.AuthTime),
        AuthMethods = row.AuthMethods,
        ExpiresAt = DbTime.AsUtc(row.ExpiresAt),
        FamilyId = row.FamilyId,
    };

    private sealed class Row
    {
        public string CodeHash { get; set; } = string.Empty;

        public string ClientId { get; set; } = string.Empty;

        public Guid SessionId { get; set; }

        public int TenantId { get; set; }

        public int UserId { get; set; }

        public string RedirectUri { get; set; } = string.Empty;

        public string Scope { get; set; } = string.Empty;

        public string CodeChallenge { get; set; } = string.Empty;

        public string? Nonce { get; set; }

        public DateTime AuthTime { get; set; }

        public string AuthMethods { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        public Guid FamilyId { get; set; }

        public DateTime? ConsumedAt { get; set; }
    }
}

/// <summary>リフレッシュトークン 1 件。</summary>
public sealed record RefreshTokenEntry
{
    public string TokenHash { get; init; } = string.Empty;

    public Guid FamilyId { get; init; }

    public string ClientId { get; init; } = string.Empty;

    public Guid SessionId { get; init; }

    public int TenantId { get; init; }

    public int UserId { get; init; }

    public string Scope { get; init; } = string.Empty;

    public DateTime AuthTime { get; init; }

    public string AuthMethods { get; init; } = string.Empty;

    public DateTime ExpiresAt { get; init; }
}

public enum RefreshConsumeStatus
{
    Consumed,
    NotFound,
    Expired,
    Revoked,

    /// <summary>既に使われたトークン。**盗まれて再利用された疑い。**</summary>
    Replayed,
}

public sealed record RefreshConsumeResult(RefreshConsumeStatus Status, RefreshTokenEntry? Entry);

/// <summary>リフレッシュトークンの入れ物。**使うたびに新しいものへ替える（ローテーション）。**</summary>
public interface IRefreshTokenStore
{
    Task CreateAsync(RefreshTokenEntry entry, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task<RefreshConsumeResult> ConsumeAsync(
        string tokenHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>同じ系統（コード 1 つから出たトークンの連なり）をすべて失効させる。</summary>
    Task RevokeFamilyAsync(Guid familyId, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task RevokeSessionAsync(Guid sessionId, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>失効の要求（RFC 7009）。自分のクライアントのものだけ。</summary>
    Task<bool> RevokeAsync(
        string tokenHash,
        string clientId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class RefreshTokenStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), IRefreshTokenStore
{
    private const string Columns =
        "[TokenHash], [FamilyId], [ClientId], [SessionId], [TenantId], [UserId], [Scope], "
        + "[AuthTime], [AuthMethods], [ExpiresAt]";

    public async Task CreateAsync(
        RefreshTokenEntry entry,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [RefreshTokens] (" + Columns + ", [CreatedAt]) VALUES (@TokenHash, @FamilyId, "
            + "@ClientId, @SessionId, @TenantId, @UserId, @Scope, @AuthTime, @AuthMethods, @ExpiresAt, @CreatedAt)",
            new
            {
                entry.TokenHash,
                entry.FamilyId,
                entry.ClientId,
                entry.SessionId,
                entry.TenantId,
                entry.UserId,
                entry.Scope,
                AuthTime = Db(entry.AuthTime),
                entry.AuthMethods,
                ExpiresAt = Db(entry.ExpiresAt),
                CreatedAt = Db(nowUtc),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<RefreshConsumeResult> ConsumeAsync(
        string tokenHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT " + Columns + ", [ConsumedAt], [RevokedAt] FROM [RefreshTokens] WHERE [TokenHash] = @TokenHash",
            new { TokenHash = tokenHash },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return new RefreshConsumeResult(RefreshConsumeStatus.NotFound, null);
        }

        var entry = ToEntry(row);
        if (row.RevokedAt is not null)
        {
            return new RefreshConsumeResult(RefreshConsumeStatus.Revoked, entry);
        }

        if (row.ConsumedAt is not null)
        {
            return new RefreshConsumeResult(RefreshConsumeStatus.Replayed, entry);
        }

        if (entry.ExpiresAt <= nowUtc)
        {
            return new RefreshConsumeResult(RefreshConsumeStatus.Expired, entry);
        }

        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [RefreshTokens] SET [ConsumedAt] = @Now "
            + "WHERE [TokenHash] = @TokenHash AND [ConsumedAt] IS NULL AND [RevokedAt] IS NULL",
            new { TokenHash = tokenHash, Now = Db(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected == 1
            ? new RefreshConsumeResult(RefreshConsumeStatus.Consumed, entry)
            : new RefreshConsumeResult(RefreshConsumeStatus.Replayed, entry);
    }

    public async Task RevokeFamilyAsync(Guid familyId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "UPDATE [RefreshTokens] SET [RevokedAt] = @Now WHERE [FamilyId] = @FamilyId AND [RevokedAt] IS NULL",
            new { FamilyId = familyId, Now = Db(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RevokeSessionAsync(Guid sessionId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "UPDATE [RefreshTokens] SET [RevokedAt] = @Now WHERE [SessionId] = @SessionId AND [RevokedAt] IS NULL",
            new { SessionId = sessionId, Now = Db(nowUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(
        string tokenHash,
        string clientId,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var familyId = await connection.QueryFirstOrDefaultAsync<Guid?>(Sql(
            "SELECT [FamilyId] FROM [RefreshTokens] WHERE [TokenHash] = @TokenHash AND [ClientId] = @ClientId",
            new { TokenHash = tokenHash, ClientId = clientId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (familyId is null)
        {
            return false;
        }

        await RevokeFamilyAsync(familyId.Value, nowUtc, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static RefreshTokenEntry ToEntry(Row row) => new()
    {
        TokenHash = row.TokenHash,
        FamilyId = row.FamilyId,
        ClientId = row.ClientId,
        SessionId = row.SessionId,
        TenantId = row.TenantId,
        UserId = row.UserId,
        Scope = row.Scope,
        AuthTime = DbTime.AsUtc(row.AuthTime),
        AuthMethods = row.AuthMethods,
        ExpiresAt = DbTime.AsUtc(row.ExpiresAt),
    };

    private sealed class Row
    {
        public string TokenHash { get; set; } = string.Empty;

        public Guid FamilyId { get; set; }

        public string ClientId { get; set; } = string.Empty;

        public Guid SessionId { get; set; }

        public int TenantId { get; set; }

        public int UserId { get; set; }

        public string Scope { get; set; } = string.Empty;

        public DateTime AuthTime { get; set; }

        public string AuthMethods { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }

        public DateTime? ConsumedAt { get; set; }

        public DateTime? RevokedAt { get; set; }
    }
}
