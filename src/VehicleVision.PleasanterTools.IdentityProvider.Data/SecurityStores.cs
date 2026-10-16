using Dapper;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>ログイン失敗の記録の状態。</summary>
public sealed record LoginFailureState(int FailedCount, DateTime? LockedUntil);

/// <summary>締め出しと TOTP の使用済みの記録。</summary>
/// <remarks>
/// **Pleasanter の DB へは書かない**ので、Pleasanter の <c>Lockout</c> / <c>LockoutCounter</c> とは別に、
/// この IdP 自身の DB で持つ。複数インスタンスで動かしても共有される。
/// </remarks>
public interface ILoginSecurityStore
{
    /// <summary>締め出し中ならその解除時刻。</summary>
    Task<DateTime?> GetLockedUntilAsync(string loginKey, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>失敗を 1 つ数える。<paramref name="window"/> より前の失敗は数えない。しきい値に達したら締め出す。</summary>
    Task<LoginFailureState> RecordFailureAsync(
        string loginKey,
        DateTime nowUtc,
        int threshold,
        TimeSpan window,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken = default);

    Task ResetAsync(string loginKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// TOTP の時間枠を「使った」と記録する。**同じ枠、またはそれより古い枠は <c>false</c>。**
    /// 盗み見られた数字を、有効な 30 秒の間に使い回されないようにする。
    /// </summary>
    Task<bool> TryUseTotpStepAsync(
        string userKey,
        long timeStep,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class LoginSecurityStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), ILoginSecurityStore
{
    public async Task<DateTime?> GetLockedUntilAsync(
        string loginKey,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var lockedUntil = await connection.QueryFirstOrDefaultAsync<DateTime?>(Sql(
            "SELECT [LockedUntil] FROM [LoginFailures] WHERE [LoginKey] = @LoginKey",
            new { LoginKey = Key(loginKey) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return lockedUntil is { } value && DbTime.AsUtc(value) > nowUtc ? DbTime.AsUtc(value) : null;
    }

    public async Task<LoginFailureState> RecordFailureAsync(
        string loginKey,
        DateTime nowUtc,
        int threshold,
        TimeSpan window,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken = default)
    {
        var parameters = new
        {
            LoginKey = Key(loginKey),
            Now = Db(nowUtc),
            WindowStart = Db(nowUtc - window),
            Threshold = threshold,
            LockedUntil = Db(nowUtc + lockoutDuration),
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // ⚠️ **代入の順番を変えないこと。** MySQL は左から順に更新後の値を見る。
        // LockedUntil → FailedCount → LastFailedAt の順なら、3 つの RDBMS とも更新前の値で判定できる
        const string update =
            "UPDATE [LoginFailures] SET "
            + "[LockedUntil] = CASE WHEN (CASE WHEN [LastFailedAt] < @WindowStart THEN 1 ELSE [FailedCount] + 1 END) "
            + ">= @Threshold THEN @LockedUntil ELSE [LockedUntil] END, "
            + "[FailedCount] = CASE WHEN [LastFailedAt] < @WindowStart THEN 1 ELSE [FailedCount] + 1 END, "
            + "[LastFailedAt] = @Now WHERE [LoginKey] = @LoginKey";

        var affected = await connection.ExecuteAsync(Sql(update, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (affected == 0)
        {
            try
            {
                await connection.ExecuteAsync(Sql(
                    "INSERT INTO [LoginFailures] ([LoginKey], [FailedCount], [LockedUntil], [LastFailedAt]) "
                    + "VALUES (@LoginKey, 1, CASE WHEN @Threshold <= 1 THEN @LockedUntil ELSE NULL END, @Now)",
                    parameters,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            catch (System.Data.Common.DbException exception) when (IsDuplicateKey(exception))
            {
                // 同時に初回の失敗が来た。相手が先に入れたので、こちらは更新として数える
                await connection.ExecuteAsync(Sql(update, parameters, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        var row = await connection.QueryFirstAsync<StateRow>(Sql(
            "SELECT [FailedCount], [LockedUntil] FROM [LoginFailures] WHERE [LoginKey] = @LoginKey",
            new { parameters.LoginKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new LoginFailureState(row.FailedCount, DbTime.AsUtc(row.LockedUntil));
    }

    public async Task ResetAsync(string loginKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "DELETE FROM [LoginFailures] WHERE [LoginKey] = @LoginKey",
            new { LoginKey = Key(loginKey) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> TryUseTotpStepAsync(
        string userKey,
        long timeStep,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var parameters = new { UserKey = userKey, Step = timeStep, Now = Db(nowUtc) };
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var updated = await connection.ExecuteAsync(Sql(
            "UPDATE [TotpUsedSteps] SET [LastTimeStep] = @Step, [UpdatedAt] = @Now "
            + "WHERE [UserKey] = @UserKey AND [LastTimeStep] < @Step",
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (updated == 1)
        {
            return true;
        }

        try
        {
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [TotpUsedSteps] ([UserKey], [LastTimeStep], [UpdatedAt]) VALUES (@UserKey, @Step, @Now)",
                parameters,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (System.Data.Common.DbException exception) when (IsDuplicateKey(exception))
        {
            // 行はあるが、記録済みの枠が同じか新しい。使用済み
            return false;
        }
    }

    /// <summary>
    /// 鍵の正規化。**大文字小文字・前後の空白で別人扱いにしない。**
    /// 長すぎる入力で列を溢れさせないよう、先頭 256 文字で切る。
    /// </summary>
    private static string Key(string loginKey)
    {
        var key = loginKey.Trim().ToLowerInvariant();
        return key.Length <= 256 ? key : key[..256];
    }

    private sealed class StateRow
    {
        public int FailedCount { get; set; }

        public DateTime? LockedUntil { get; set; }
    }
}

/// <summary>ログインの途中で預かる要求（SAML の POST バインディングなど、URL へ載せきれないもの）。</summary>
public interface IPendingRequestStore
{
    Task<Guid> SaveAsync(
        string kind,
        string payload,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>取り出す。**1 度だけ。** 期限切れ・取得済みは <c>null</c>。</summary>
    Task<string?> TakeAsync(
        Guid requestId,
        string kind,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class PendingRequestStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), IPendingRequestStore
{
    public async Task<Guid> SaveAsync(
        string kind,
        string payload,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(Sql(
            "INSERT INTO [PendingRequests] ([RequestId], [Kind], [Payload], [ExpiresAt]) "
            + "VALUES (@RequestId, @Kind, @Payload, @ExpiresAt)",
            new { RequestId = requestId, Kind = kind, Payload = payload, ExpiresAt = Db(expiresAtUtc) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return requestId;
    }

    public async Task<string?> TakeAsync(
        Guid requestId,
        string kind,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<PendingRow>(Sql(
            "SELECT [Payload], [ExpiresAt] FROM [PendingRequests] WHERE [RequestId] = @RequestId AND [Kind] = @Kind",
            new { RequestId = requestId, Kind = kind },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        // 消せた（件数 1）ほうだけが受け取れる
        var deleted = await connection.ExecuteAsync(Sql(
            "DELETE FROM [PendingRequests] WHERE [RequestId] = @RequestId",
            new { RequestId = requestId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return deleted == 1 && DbTime.AsUtc(row.ExpiresAt) > nowUtc ? row.Payload : null;
    }

    private sealed class PendingRow
    {
        public string Payload { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }
    }
}

/// <summary>期限の過ぎた行を消す。</summary>
public interface IExpiredDataStore
{
    /// <returns>表ごとに消した件数。</returns>
    Task<IReadOnlyDictionary<string, int>> DeleteExpiredAsync(
        DateTime nowUtc,
        TimeSpan sessionRetention,
        CancellationToken cancellationToken = default);
}

public sealed class ExpiredDataStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), IExpiredDataStore
{
    public async Task<IReadOnlyDictionary<string, int>> DeleteExpiredAsync(
        DateTime nowUtc,
        TimeSpan sessionRetention,
        CancellationToken cancellationToken = default)
    {
        var now = Db(nowUtc);
        var sessionLimit = Db(nowUtc - sessionRetention);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        // 認可コードは短命。使用済みの記録（使い回しの検出）に要る間だけ残せば足りる
        result["AuthorizationCodes"] = await Delete(
            connection,
            "DELETE FROM [AuthorizationCodes] WHERE [ExpiresAt] < @Limit",
            new { Limit = sessionLimit },
            cancellationToken).ConfigureAwait(false);
        result["RefreshTokens"] = await Delete(
            connection,
            "DELETE FROM [RefreshTokens] WHERE [ExpiresAt] < @Limit",
            new { Limit = sessionLimit },
            cancellationToken).ConfigureAwait(false);
        result["PendingRequests"] = await Delete(
            connection,
            "DELETE FROM [PendingRequests] WHERE [ExpiresAt] < @Now",
            new { Now = now },
            cancellationToken).ConfigureAwait(false);
        result["SessionParticipants"] = await Delete(
            connection,
            "DELETE FROM [SessionParticipants] WHERE [SessionId] IN "
            + "(SELECT [SessionId] FROM [SsoSessions] WHERE [ExpiresAt] < @Limit)",
            new { Limit = sessionLimit },
            cancellationToken).ConfigureAwait(false);
        result["SsoSessions"] = await Delete(
            connection,
            "DELETE FROM [SsoSessions] WHERE [ExpiresAt] < @Limit",
            new { Limit = sessionLimit },
            cancellationToken).ConfigureAwait(false);
        result["LoginFailures"] = await Delete(
            connection,
            "DELETE FROM [LoginFailures] WHERE [LastFailedAt] < @Limit AND ([LockedUntil] IS NULL OR [LockedUntil] < @Now)",
            new { Limit = sessionLimit, Now = now },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<int> Delete(
        System.Data.Common.DbConnection connection,
        string sql,
        object parameters,
        CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(Sql(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
}
