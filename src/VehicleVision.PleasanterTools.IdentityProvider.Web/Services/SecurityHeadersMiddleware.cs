namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>セキュリティヘッダと、キャッシュの禁止を付ける。</summary>
/// <remarks>
/// <para>
/// **CSP は厳しい固定値。** この IdP は外部のスクリプト・画像・フォントを読まない。
/// ログインと管理画面は同じ origin の資産だけで動く。クリックジャッキングを避けるため
/// <c>frame-ancestors 'none'</c>（ログイン画面を他所の frame に入れさせない）。
/// </para>
/// <para>
/// **動的な応答（トークン・ログイン・管理 API）は <c>no-store</c>。** 資格情報や認可コードを含み得るので、
/// ブラウザや中間のキャッシュに残さない。静的な資産は、それぞれが自分で指示を付ける
/// （<see cref="StaticCachePolicy"/>）。
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
        + "base-uri 'self'; object-src 'none'; frame-ancestors 'none'";

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        // ⚠️ **`no-referrer` にしない。** ブラウザは `Referrer-Policy: no-referrer` のとき、
        // 同じオリジンへのフォーム送信でも `Origin: null` を送る（Fetch 標準）。
        // CSRF の検査（`CsrfGuard`）が `Origin` を見るので、**ログインの POST が全て断られる**
        // （実ブラウザで確認）。`same-origin` なら、同じオリジンへは本来の `Origin` を送り、
        // 他所（SP・RP）へは何も送らない
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;

        context.Response.OnStarting(() =>
        {
            // 自分で指示を付けた応答（静的ファイル・ログイン画面の CSS）には触れない
            if (!headers.ContainsKey("Cache-Control"))
            {
                headers["Cache-Control"] = "no-store";
                headers["Pragma"] = "no-cache";
            }

            return Task.CompletedTask;
        });

        await next(context);
    }
}
