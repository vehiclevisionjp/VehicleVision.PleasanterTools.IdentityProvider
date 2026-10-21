using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>ブラウザ上で動く RP（SPA）が、IdP の OIDC エンドポイントを呼べるようにする（CORS）。</summary>
/// <remarks>
/// <para>
/// **公開クライアント（SPA）は、ブラウザからトークンエンドポイントと UserInfo を直接呼ぶ。**
/// CORS が無いと、ブラウザが応答を読ませない。
/// </para>
/// <list type="bullet">
/// <item>
/// **ディスカバリと JWKS は誰にでも読ませる**（<c>*</c>）。公開の情報で、資格情報を伴わない。
/// </item>
/// <item>
/// **トークン・UserInfo・失効は、登録されたクライアントのリダイレクト URI のオリジンだけ**を許す。
/// 登録の無いオリジンからのスクリプトには、応答を読ませない。
/// <b>cookie や資格情報を伴う要求は許さない</b>（<c>Access-Control-Allow-Credentials</c> を付けない）。
/// </item>
/// </list>
/// <para>
/// ログイン・認可・管理 API・SAML には CORS を付けない（ブラウザのナビゲーションで使うもの、
/// または同じオリジンだけで使うもの）。
/// </para>
/// </remarks>
public sealed class OidcCorsMiddleware(
    RequestDelegate next,
    IOidcClientStore clients,
    TimeProvider timeProvider)
{
    private static readonly string[] OpenPaths = ["/.well-known/openid-configuration", "/oidc/jwks"];

    private static readonly string[] ClientPaths = ["/oidc/token", "/oidc/userinfo", "/oidc/revocation"];

    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);

    /// <summary>知らないオリジンが来たとき、登録を引き直すまでの最短の間隔（登録した直後でも使えるように）。</summary>
    private static readonly TimeSpan RetryTime = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim gate = new(1, 1);
    private HashSet<string> origins = new(StringComparer.OrdinalIgnoreCase);
    private DateTime loadedAt = DateTime.MinValue;

    public async Task InvokeAsync(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var path = context.Request.Path.Value ?? string.Empty;
        var isOpen = OpenPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
        var isClient = !isOpen && ClientPaths.Contains(path, StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(origin) || (!isOpen && !isClient))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var headers = context.Response.Headers;
        headers.Append("Vary", "Origin");

        var allowed = isOpen || await IsAllowedAsync(origin, context.RequestAborted).ConfigureAwait(false);
        if (allowed)
        {
            headers["Access-Control-Allow-Origin"] = isOpen ? "*" : origin;
        }

        if (HttpMethods.IsOptions(context.Request.Method)
            && context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            // プリフライト。許さないオリジンへは、許可のヘッダを付けずに返す（ブラウザが本要求を止める）
            if (allowed)
            {
                headers["Access-Control-Allow-Methods"] = isOpen ? "GET, OPTIONS" : "GET, POST, OPTIONS";
                headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                headers["Access-Control-Max-Age"] = "600";
            }

            context.Response.StatusCode = allowed ? StatusCodes.Status204NoContent : StatusCodes.Status403Forbidden;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private async Task<bool> IsAllowedAsync(string origin, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (Stale(origin, now))
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Stale(origin, now))
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var client in await clients.ListAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (!client.Enabled)
                        {
                            continue;
                        }

                        foreach (var uri in client.RedirectUris)
                        {
                            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                                && parsed.Scheme is "http" or "https")
                            {
                                set.Add(parsed.GetLeftPart(UriPartial.Authority));
                            }
                        }
                    }

                    origins = set;
                    loadedAt = now;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return origins.Contains(origin);
    }

    private bool Stale(string origin, DateTime now) =>
        now - loadedAt > CacheTime || (now - loadedAt > RetryTime && !origins.Contains(origin));
}
