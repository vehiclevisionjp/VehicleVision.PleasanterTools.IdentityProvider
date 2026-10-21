using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>期限の過ぎた行と、古い監査ログを定期的に消す。</summary>
/// <remarks>
/// <para>
/// **マイグレーションが済むまで動かさない**（<see cref="DatabaseStartupState"/>）。
/// 表がまだ無い間に走ると、起動が落ちる。
/// </para>
/// <para>
/// セッションは、期限（<c>ExpiresAt</c>）から<b>2 日</b>残す。アクセストークンの寿命の上限（24 時間）より
/// 長く持つことで、失効済みのセッションから出たトークンが、掃除のあとで有効に戻らないようにする
/// （<see cref="ISsoSessionStore.IsRevokedAsync"/>）。
/// </para>
/// </remarks>
public sealed class ExpiredDataCleanupService(
    IExpiredDataStore expired,
    IAuditLogStore audit,
    DatabaseStartupState startup,
    TimeProvider timeProvider,
    AuditRetention retention,
    ILogger<ExpiredDataCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan SessionRetention = TimeSpan.FromDays(2);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await startup.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var deleted = await expired.DeleteExpiredAsync(now, SessionRetention, cancellationToken).ConfigureAwait(false);
            var auditDeleted = retention.Days > 0
                ? await audit.DeleteOlderThanAsync(now.AddDays(-retention.Days), cancellationToken).ConfigureAwait(false)
                : 0;

            if (deleted.Values.Sum() + auditDeleted > 0)
            {
                logger.LogInformation(
                    "期限切れのデータを削除した: {Deleted}、監査ログ {AuditDeleted} 件",
                    string.Join(", ", deleted.Where(pair => pair.Value > 0).Select(pair => $"{pair.Key}={pair.Value}")),
                    auditDeleted);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **止めない。** 次の周期でやり直す
            logger.LogError(exception, "期限切れのデータの削除に失敗した。");
        }
    }
}

/// <summary>監査ログを残す日数。0 なら消さない。</summary>
public sealed record AuditRetention(int Days)
{
    public const string Setting = "IDP_AUDIT_RETENTION_DAYS";

    public const int DefaultDays = 365;

    public static AuditRetention FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration[Setting];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new AuditRetention(DefaultDays);
        }

        return int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var days) && days is >= 0 and <= 36500
            ? new AuditRetention(days)
            : throw new InvalidOperationException($"{Setting} must be an integer from 0 to 36500.");
    }
}
