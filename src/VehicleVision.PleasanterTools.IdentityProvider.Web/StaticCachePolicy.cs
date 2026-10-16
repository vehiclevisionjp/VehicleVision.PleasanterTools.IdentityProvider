using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>画面の配信にキャッシュの指示を付ける。</summary>
/// <remarks>
/// <para>
/// ⚠️ **付けないと、ブラウザが独自の判断で使い回す。**
/// `Cache-Control` が無いと `Last-Modified` からの推測で保持してよいことになっており、
/// **再検証すらしない場合がある。** 実際に**作り直した画面が出ず、
/// 9/17 のものを 1 週間見続けた**。
/// </para>
/// <para>
/// **入口の HTML と、中身を指す資産で扱いを分ける。**
/// </para>
/// <list type="bullet">
/// <item>
/// **入口（`admin.html` / `index.html`）は名前が変わらない。**
/// ここが古いと**古い資産を指し続ける**ので、毎回確かめさせる。
/// ⚠️ **`no-store` にはしない。** 再検証は許し、変わっていなければ 304 で済ませる
/// </item>
/// <item>
/// **資産（`/assets/`）は内容ハッシュ付きの名前。**
/// 中身が変われば名前が変わるので、**長く持たせても古いものを掴み続けない**
/// </item>
/// </list>
/// </remarks>
public static class StaticCachePolicy
{
    /// <summary>入口の HTML。**毎回確かめさせる。**</summary>
    public const string RevalidateValue = "no-cache";

    /// <summary>内容ハッシュ付きの資産。**1 年持たせる。**</summary>
    public const string ImmutableValue = "public, max-age=31536000, immutable";

    /// <summary>静的ファイルの応答へキャッシュの指示を付ける。</summary>
    public static void Apply(StaticFileResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Context.Response.Headers.CacheControl = ValueFor(context.Context.Request.Path);
    }

    /// <summary>経路に対する指示を返す。**テストから直接確かめる。**</summary>
    /// <remarks>
    /// ⚠️ **HTML かどうかで決める。** 拡張子の無い経路（`/admin` や `/f/xxx`）も
    /// 入口の HTML を返すため、**そこも確かめさせる側に倒す。**
    /// </remarks>
    public static string ValueFor(PathString path)
    {
        var value = path.Value ?? string.Empty;

        // **内容ハッシュ付きの名前だけを長く持たせる。**
        // ⚠️ **`/assets/` の外に置いた画像や書体は対象にしない。**
        // 名前が変わらないので、長く持たせると差し替えが効かなくなる
        return value.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? ImmutableValue
            : RevalidateValue;
    }
}
