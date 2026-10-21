namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>設定したサブパスを要求の PathBase へ移す。</summary>
/// <remarks>
/// <para>
/// **標準の <c>UsePathBase</c> を使わない理由。** あちらはサブパスで始まらない要求も素通しにするので、
/// <c>/idp/api/...</c> と <c>/api/...</c> の両方で同じ API が応答してしまう。
/// 同じホストに Pleasanter が居る構成では、**本アプリの外の経路へ応答しないこと**を保証したい。
/// </para>
/// <para>
/// **IIS のサブアプリケーション（ANCM）では PathBase が既に入っている。**
/// 同じ値ならそのまま通し、二重に剥がさない。
/// </para>
/// <para>
/// **生存確認（<c>/healthz</c>・<c>/ready</c>）だけはサブパスの外でも応答する。**
/// コンテナの HEALTHCHECK や Kubernetes の probe は、プロキシを通らず直接当たることが多い。
/// どちらも利用者の情報を返さないので、外に出しても失うものが無い。
/// </para>
/// <para>
/// ⚠️ **<c>UseRouting</c> より前に置くこと。** 経路の照合は PathBase を剥がした後の Path で行う。
/// </para>
/// </remarks>
public static class PathBaseMiddleware
{
    /// <summary>設定したサブパスを受け付ける。**未設定なら何もしない。**</summary>
    public static IApplicationBuilder UseIdpPathBase(
        this IApplicationBuilder app,
        PathBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            return app;
        }

        var pathBase = options.Value;
        return app.Use(async (context, next) =>
        {
            var request = context.Request;

            // **ANCM が既に渡してきた。** 同じ値なら、もう剥がしてある
            if (request.PathBase.Equals(pathBase))
            {
                await next(context);
                return;
            }

            if (!request.PathBase.HasValue
                && request.Path.StartsWithSegments(pathBase, out var remaining))
            {
                var originalPath = request.Path;
                var originalPathBase = request.PathBase;

                // **要求の綴りではなく、設定した綴りを PathBase にする。**
                // 照合は大文字小文字を区別しないので `/QUESTIONNAIRE/` も通る。
                // 要求の綴りを写すと cookie の Path（ブラウザは区別する）が綴りごとに割れ、
                // 入口 HTML のキャッシュも綴りの組み合わせだけ増える
                request.PathBase = originalPathBase.Add(pathBase);

                // **`/idp` ちょうどは根として扱う。** 空の Path は経路に当たらない
                request.Path = remaining.HasValue ? remaining : new PathString("/");
                try
                {
                    await next(context);
                }
                finally
                {
                    request.PathBase = originalPathBase;
                    request.Path = originalPath;
                }

                return;
            }

            if (!request.PathBase.HasValue && IsProbe(request.Path))
            {
                await next(context);
                return;
            }

            // **本アプリの外は知らないものとして返す。** 同じホストの Pleasanter の経路へ応答しない
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
    }

    /// <summary>サブパスの外でも応答する生存確認か。</summary>
    public static bool IsProbe(PathString path) =>
        path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/ready", StringComparison.OrdinalIgnoreCase);
}
