using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>認証・トークン発行・管理操作を Core.Data の共有表 <c>Core_AuditLogs</c> へ残す。</summary>
/// <remarks>
/// <para>
/// **補足（<c>detail</c>）に、パスワード・シークレット・トークン・認可コード・TOTP の数字・
/// 共有鍵を入れないこと。** 呼び出し側が、識別子と理由の符号だけを渡す。
/// </para>
/// <para>
/// **記録が失敗しても、操作は通す。** 操作はもう済んでおり、ここで例外にすると
/// 「通ったのに失敗と伝える」ことになる。失敗はログへ出す（握り潰さない）。
/// </para>
/// </remarks>
public sealed class AuditWriter(
    IAuditLogStore store,
    TimeProvider timeProvider,
    ILogger<AuditWriter> logger)
{
    /// <summary>日本語をそのまま入れる。HTML に効く文字は逃がしたままにする。</summary>
    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public async Task WriteAsync(
        HttpContext? http,
        string action,
        int? statusCode = null,
        string? actorUserKey = null,
        string? actorLoginId = null,
        string? targetType = null,
        string? targetId = null,
        object? detail = null)
    {
        try
        {
            await store.WriteAsync(
                new AuditEntry(
                    timeProvider.GetUtcNow(),
                    action,
                    // 結果の HTTP 状態が無い操作、または 400 未満は成功（失敗は 400 以上）
                    statusCode is null or < 400,
                    statusCode,
                    actorUserKey,
                    actorLoginId,
                    targetType,
                    targetId,
                    detail is null ? null : JsonSerializer.Serialize(detail, DetailOptions),
                    IpAddressOf(http)),
                http?.RequestAborted ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "監査ログを残せなかった（{Action}）", LogSafe.Text(action));
        }
    }

    /// <summary>送信元。**IPv4 を IPv6 の形で残さない。**</summary>
    public static string? IpAddressOf(HttpContext? http)
    {
        var address = http?.Connection.RemoteIpAddress;
        return address is null
            ? null
            : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}
