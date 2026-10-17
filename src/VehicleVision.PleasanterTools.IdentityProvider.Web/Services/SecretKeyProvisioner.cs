using System.Text.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary><c>IDP_SECRET_KEY</c>（<c>Service.json</c> の <c>SecretKey</c>）の用意。無ければ作ってローカルのファイルへ残す。</summary>
/// <remarks>
/// <para>
/// **既に与えられていれば何もしない**（環境変数・Key Vault・<c>.local.json</c> が先）。
/// 無いときだけ新しい鍵を作り、<c>Service.local.json</c>（git 管理外）の <c>SecretKey</c> へ書く。
/// 次の起動からは、そのファイルを読む。
/// </para>
/// <para>
/// ⚠️ **自動で作った鍵は、IdP の DB に署名鍵が無いときだけ使ってよい。** 鍵を失ったまま作り直すと、DB の署名鍵が復号できず、
/// 黙って別の鍵で動くと発行済みのトークンが全て検証できなくなる。DB に署名鍵があるのに鍵が自動で作られたときは、
/// 作った鍵をファイルから消して起動を止める（<see cref="DiscardAsync"/>）。
/// </para>
/// <para>
/// ⚠️ **複数インスタンス・使い捨てのファイルシステム（コンテナなど）では、明示的に与えること。**
/// インスタンスごとに別の鍵ができたり、再作成で鍵が消えたりする。
/// </para>
/// </remarks>
public static class SecretKeyProvisioner
{
    public const string Setting = "IDP_SECRET_KEY";

    public const string FileName = "Service.local.json";

    private const string JsonKey = "SecretKey";

    /// <summary>鍵の文字列。<paramref name="generatedAt"/> は、今回作ったときだけ書いたファイルのパス。</summary>
    public static string Resolve(IConfiguration configuration, string directory, out string? generatedAt)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var given = configuration[Setting];
        if (!string.IsNullOrWhiteSpace(given))
        {
            generatedAt = null;
            return given;
        }

        var key = SecretProtector.GenerateKey();
        var path = Path.Combine(directory, FileName);
        try
        {
            Directory.CreateDirectory(directory);
            var root = Read(path);
            root[JsonKey] = key;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException(
                $"{Setting} is not set, and a new key could not be saved to {path} ({exception.Message}). "
                + $"Set {Setting} (Base64 of 32 bytes; generate with --generate-secret-key) or make the directory writable.",
                exception);
        }

        generatedAt = path;
        return key;
    }

    /// <summary>自動で作った鍵を、ファイルから消す（DB の署名鍵が読めなくなる事故を避けるため）。</summary>
    public static Task DiscardAsync(string path)
    {
        var root = Read(path);
        root.Remove(JsonKey);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return Task.CompletedTask;
    }

    private static JsonObject Read(string path) =>
        File.Exists(path) && File.ReadAllText(path) is { } text && !string.IsNullOrWhiteSpace(text)
            ? JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                as JsonObject ?? throw new JsonException($"{path} is not a JSON object.")
            : [];
}
