using System.Text.Json;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>パスワードは通ったが、TOTP がまだの状態（cookie に持つ）。</summary>
public sealed record PendingMfa(int TenantId, int UserId, DateTime ExpiresAtUtc);

/// <summary>SSO セッション（IdP へのログイン状態）の cookie と DB の橋渡し。</summary>
/// <remarks>
/// <para>
/// **cookie には署名付きのセッション ID だけを入れる**（<see cref="CookieSigner"/>）。
/// 本体は DB にあるので、管理画面や利用者の操作で即座に失効できる。
/// </para>
/// <para>
/// **ログインのたびに新しいセッション ID を発行する**（セッション固定化の対策）。
/// 以前の cookie が指していたセッションは、その場で失効させる。
/// </para>
/// <para>
/// **cookie は <c>HttpOnly</c> と <c>SameSite=Lax</c>。** RP からのリダイレクト（トップレベルの GET）では
/// 送られ、別サイトからの POST・埋め込みでは送られない。<c>Secure</c> は Issuer が https のときに付ける。
/// </para>
/// </remarks>
public sealed class SsoSessionService(
    ISsoSessionStore store,
    IRefreshTokenStore refreshTokens,
    CookieSigner signer,
    IdpOptions options,
    TimeProvider timeProvider)
{
    public const string SessionCookie = "idp_session";

    public const string MfaCookie = "idp_mfa";

    private const string SessionPurpose = "session";

    private const string MfaPurpose = "mfa";

    /// <summary>TOTP の入力を待つ時間。</summary>
    public static readonly TimeSpan MfaLifetime = TimeSpan.FromMinutes(5);

    /// <summary>最終アクセスの更新を間引く間隔。</summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private const string CacheKey = "idp.session";

    /// <summary>この要求のセッション。無ければ（失効・期限切れを含む）<c>null</c>。</summary>
    public async Task<SsoSessionEntry?> GetCurrentAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(CacheKey, out var cached))
        {
            return cached as SsoSessionEntry;
        }

        SsoSessionEntry? session = null;
        if (http.Request.Cookies.TryGetValue(SessionCookie, out var value)
            && signer.Verify(SessionPurpose, value) is { } id
            && Guid.TryParse(id, out var sessionId))
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            session = await store.FindActiveAsync(sessionId, now, options.SessionIdleTimeout, http.RequestAborted)
                .ConfigureAwait(false);

            if (session is not null && now - session.LastSeenAt > TouchInterval)
            {
                await store.TouchAsync(sessionId, now, http.RequestAborted).ConfigureAwait(false);
            }
        }

        http.Items[CacheKey] = session;
        return session;
    }

    /// <summary>新しいセッションを作り、cookie を発行する。</summary>
    public async Task<SsoSessionEntry> SignInAsync(
        HttpContext http,
        PleasanterUser user,
        IReadOnlyList<string> authMethods)
    {
        // 以前のセッションがあれば閉じる（固定化の対策）
        if (await GetCurrentAsync(http).ConfigureAwait(false) is { } previous)
        {
            await RevokeAsync(previous.SessionId, http.RequestAborted).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entry = new SsoSessionEntry
        {
            SessionId = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = user.UserId,
            LoginId = user.LoginId,
            UserName = user.Name,
            AuthMethods = authMethods,
            AuthTime = now,
            LastSeenAt = now,
            ExpiresAt = now + options.SessionLifetime,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = http.Request.Headers.UserAgent.ToString(),
        };

        await store.CreateAsync(entry, http.RequestAborted).ConfigureAwait(false);
        http.Response.Cookies.Append(
            SessionCookie,
            signer.Sign(SessionPurpose, entry.SessionId.ToString()),
            CookieOptions(http, maxAge: null));
        ClearPendingMfa(http);
        http.Items[CacheKey] = entry;
        return entry;
    }

    /// <summary>現在のセッションを失効させ、cookie を消す。</summary>
    public async Task SignOutAsync(HttpContext http)
    {
        if (await GetCurrentAsync(http).ConfigureAwait(false) is { } session)
        {
            await RevokeAsync(session.SessionId, http.RequestAborted).ConfigureAwait(false);
        }

        http.Response.Cookies.Delete(SessionCookie, CookieOptions(http, maxAge: null));
        http.Items[CacheKey] = null;
    }

    /// <summary>セッションを失効させ、そこから出たリフレッシュトークンも使えなくする。</summary>
    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await store.RevokeAsync(sessionId, now, cancellationToken).ConfigureAwait(false);
        await refreshTokens.RevokeSessionAsync(sessionId, now, cancellationToken).ConfigureAwait(false);
    }

    public void SetPendingMfa(HttpContext http, PleasanterUser user)
    {
        var expires = timeProvider.GetUtcNow().UtcDateTime + MfaLifetime;
        var payload = JsonSerializer.Serialize(new PendingMfa(user.TenantId, user.UserId, expires));
        http.Response.Cookies.Append(MfaCookie, signer.Sign(MfaPurpose, payload), CookieOptions(http, MfaLifetime));
    }

    public PendingMfa? ReadPendingMfa(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(MfaCookie, out var value)
            || signer.Verify(MfaPurpose, value) is not { } payload)
        {
            return null;
        }

        try
        {
            var pending = JsonSerializer.Deserialize<PendingMfa>(payload);
            return pending is not null && pending.ExpiresAtUtc > timeProvider.GetUtcNow().UtcDateTime
                ? pending
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void ClearPendingMfa(HttpContext http) =>
        http.Response.Cookies.Delete(MfaCookie, CookieOptions(http, maxAge: null));

    /// <summary>セッション ID が現在も有効か（トークンの <c>sid</c> の確認に使う）。</summary>
    public Task<SsoSessionEntry?> FindActiveAsync(Guid sessionId, CancellationToken cancellationToken) =>
        store.FindActiveAsync(sessionId, timeProvider.GetUtcNow().UtcDateTime, options.SessionIdleTimeout, cancellationToken);

    /// <summary>cookie の付け方。<c>Path</c> は PathBase（サブパス配置でも他のアプリと cookie を混ぜない）。</summary>
    public CookieOptions CookieOptions(HttpContext http, TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = options.IsSecure,
        SameSite = SameSiteMode.Lax,
        Path = http.Request.PathBase.HasValue ? http.Request.PathBase.Value : "/",
        MaxAge = maxAge,
        IsEssential = true,
    };

    /// <summary>ログイン後の戻り先として安全か。**同じオリジン内の絶対パスだけ。**</summary>
    /// <remarks>
    /// オープンリダイレクト（<c>//evil.example</c>・<c>/\evil.example</c>・スキーム付き）を弾く。
    /// </remarks>
    public static bool IsSafeReturnUrl(string? returnUrl, PathString pathBase)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl.Length > 8192 || returnUrl[0] != '/')
        {
            return false;
        }

        if (returnUrl.Length > 1 && returnUrl[1] is '/' or '\\')
        {
            return false;
        }

        if (returnUrl.Any(char.IsControl) || returnUrl.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        // サブパス配置では、戻り先もサブパスの中だけ
        return !pathBase.HasValue
            || returnUrl.StartsWith(pathBase.Value!, StringComparison.Ordinal);
    }
}
