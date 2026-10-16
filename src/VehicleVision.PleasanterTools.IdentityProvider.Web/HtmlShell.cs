using System.Collections.Concurrent;
using System.Text.Encodings.Web;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>画面の入口の HTML（<c>index.html</c> / <c>admin.html</c>）へ、配置先の経路を埋めて返す。</summary>
/// <remarks>
/// <para>
/// **画面を組み立て直さずに、配置先のサブパスを変えられるようにする**。
/// 画面の成果物は資産を<c>__IDP_BASE_PATH__/assets/...</c> で指しており
/// （<c>vite.config.ts</c>）、ここで要求の PathBase に差し替える。
/// 画面の JavaScript は同じ値を <c>meta name="idp-base-path"</c> から読む。
/// </para>
/// <para>
/// **インラインのスクリプトは使わない。** CSP の <c>script-src 'self'</c> を緩めずに済ませるため、
/// 値は meta 要素と属性だけで渡す（<see cref="Services.SecurityHeadersMiddleware"/>）。
/// </para>
/// <para>
/// **要求の PathBase を使う。** 設定（<see cref="PathBaseOptions"/>）でも IIS の ANCM でも同じ場所に入る。
/// どちらも配置で決まる固定の値で、利用者が要求で変えられる値ではない。
/// </para>
/// <para>
/// ⚠️ **起動時に読まない。** <c>wwwroot</c> はフロントエンドを組み立てて初めて出来るもので、
/// **無い状態でも起動はできなければならない**（<c>dotnet run</c> だけした手元）。
/// 1 度読んだら覚えておき、2 度目からはディスクを触らない。
/// </para>
/// </remarks>
public sealed class HtmlShell(IWebHostEnvironment environment, AdminPathOptions adminPath)
{
    /// <summary>配置先のサブパス（無ければ空）を埋める印。</summary>
    public const string BasePathPlaceholder = "__IDP_BASE_PATH__";

    /// <summary>管理画面の入口（サブパス込み）を埋める印。</summary>
    public const string AdminPathPlaceholder = "__IDP_ADMIN_PATH__";

    public const string IndexFile = "index.html";
    public const string AdminFile = "admin.html";

    private readonly ConcurrentDictionary<string, Lazy<string?>> templates = new(StringComparer.Ordinal);

    /// <summary>
    /// 差し替えた結果。**PathBase ごとに 1 つ。** PathBase は配置で決まるので、実際には 1 つか 2 つしか出来ない。
    /// </summary>
    private readonly ConcurrentDictionary<(string File, string PathBase), string?> rendered = new();

    /// <summary>入口の HTML（index.html）。**置かれていなければ <c>null</c>。**</summary>
    public string? Index(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Render(IndexFile, context.Request.PathBase);
    }

    /// <summary>管理画面の入口。**置かれていなければ <c>null</c>。**</summary>
    public string? Admin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Render(AdminFile, context.Request.PathBase);
    }

    /// <summary>印を差し替える。**テストから直接確かめる。**</summary>
    /// <remarks>
    /// **印が無いのは組み立ての不備。** 既定の <c>/</c> なら動いてしまうので黙らせない。
    /// </remarks>
    public static string Apply(string template, string file, PathString pathBase, string adminPath)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (!template.Contains(BasePathPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{file} に配置先の経路の埋め込み先がありません。");
        }

        var encoder = HtmlEncoder.Default;
        var html = template.Replace(
            BasePathPlaceholder,
            encoder.Encode(pathBase.Value ?? string.Empty),
            StringComparison.Ordinal);

        if (!string.Equals(file, AdminFile, StringComparison.Ordinal))
        {
            return html;
        }

        if (!html.Contains(AdminPathPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{file} に管理画面パスの埋め込み先がありません。");
        }

        // **管理画面の入口はサブパス込みで渡す。** 画面はこれを URL の先頭としてそのまま使う
        return html.Replace(
            AdminPathPlaceholder,
            encoder.Encode(pathBase.Add(adminPath).Value ?? adminPath),
            StringComparison.Ordinal);
    }

    private string? Render(string file, PathString pathBase) =>
        rendered.GetOrAdd((file, pathBase.Value ?? string.Empty), key =>
            Template(key.File) is { } template
                ? Apply(template, key.File, pathBase, adminPath.Path)
                : null);

    private string? Template(string file) =>
        templates.GetOrAdd(file, name => new Lazy<string?>(() =>
        {
            var webRoot = environment.WebRootPath;
            if (string.IsNullOrEmpty(webRoot))
            {
                return null;
            }

            var path = Path.Combine(webRoot, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        })).Value;
}
