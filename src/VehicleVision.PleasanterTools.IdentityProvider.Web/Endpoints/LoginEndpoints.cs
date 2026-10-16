using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Pages;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Endpoints;

/// <summary>サインイン（パスワード → 必要なら TOTP）とサインアウトの画面。</summary>
/// <remarks>
/// <para>
/// OIDC / SAML / 管理画面のどれも、ここで作った SSO セッションを使う。
/// 戻り先（<c>returnUrl</c>）は<b>同じオリジンの絶対パスだけ</b>
/// （<see cref="SsoSessionService.IsSafeReturnUrl"/>）。
/// </para>
/// <para>
/// **パスワードが通った時点では、まだセッションを作らない。** TOTP が要る人は、
/// 署名付きの短命な cookie（<see cref="PendingMfa"/>）だけを持たせ、TOTP が通って初めて作る。
/// </para>
/// </remarks>
public static class LoginEndpoints
{
    public const string RateLimitPolicy = "login";

    public static IEndpointRouteBuilder MapLoginEndpoints(this IEndpointRouteBuilder app)
    {
        // 中身が変わらない資産は、短く持たせてよい（既定は no-store）
        app.MapGet(HtmlPages.CssPath, (HttpContext http) => Asset(http, HtmlPages.Css, "text/css; charset=utf-8"))
            .ExcludeFromDescription();
        app.MapGet(HtmlPages.PostScriptPath, (HttpContext http) => Asset(http, HtmlPages.PostScript, "text/javascript; charset=utf-8"))
            .ExcludeFromDescription();

        app.MapGet("/login", ShowLoginAsync).ExcludeFromDescription();
        app.MapPost("/login", SubmitLoginAsync).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
        app.MapGet("/login/totp", ShowTotp).ExcludeFromDescription();
        app.MapPost("/login/totp", SubmitTotpAsync).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
        app.MapGet("/logout", ShowLogoutAsync).ExcludeFromDescription();
        app.MapPost("/logout", SubmitLogoutAsync).ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> ShowLoginAsync(
        HttpContext http,
        SsoSessionService sessions,
        CsrfGuard csrf,
        AdminPathOptions adminPath,
        string? returnUrl)
    {
        var target = SafeReturnUrl(http, returnUrl, adminPath);
        if (await sessions.GetCurrentAsync(http).ConfigureAwait(false) is not null)
        {
            return Results.Redirect(target);
        }

        return Html(HtmlPages.Login(Language(http), BasePath(http), target, csrf.Issue(http)));
    }

    private static async Task<IResult> SubmitLoginAsync(
        HttpContext http,
        AuthenticationService authentication,
        SsoSessionService sessions,
        CsrfGuard csrf,
        AuditWriter audit,
        AdminPathOptions adminPath)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
        var language = Language(http);
        var basePath = BasePath(http);
        var returnUrl = SafeReturnUrl(http, form["returnUrl"], adminPath);
        var loginId = form["loginId"].ToString();
        var password = form["password"].ToString();

        if (!csrf.Validate(http, form[CsrfGuard.FormField]))
        {
            return Html(
                HtmlPages.Login(language, basePath, returnUrl, csrf.Issue(http), loginId, UiText.CsrfFailed),
                StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(loginId) || loginId.Length > 256 || password.Length is 0 or > 256)
        {
            return Html(HtmlPages.Login(language, basePath, returnUrl, csrf.Issue(http), loginId, UiText.InvalidCredentials));
        }

        var result = await authentication.CheckPasswordAsync(loginId, password, http.RequestAborted).ConfigureAwait(false);
        switch (result.Status)
        {
            case PasswordStatus.Success:
                await audit.WriteAsync(
                    http, "auth.login.success", StatusCodes.Status200OK,
                    result.User!.UserKey, result.User.LoginId, "User", result.User.UserKey,
                    new { methods = new[] { "pwd" } }).ConfigureAwait(false);
                await sessions.SignInAsync(http, result.User, ["pwd"]).ConfigureAwait(false);
                return Results.Redirect(returnUrl);

            case PasswordStatus.TotpRequired:
                sessions.SetPendingMfa(http, result.User!);
                return Results.Redirect(BasePath(http) + "/login/totp?returnUrl=" + Uri.EscapeDataString(returnUrl));

            case PasswordStatus.TotpNotRegistered:
                await audit.WriteAsync(
                    http, "auth.login.denied", StatusCodes.Status403Forbidden,
                    result.User!.UserKey, result.User.LoginId, "User", result.User.UserKey,
                    new { reason = "totp-not-registered" }).ConfigureAwait(false);
                return Html(
                    HtmlPages.Message(language, basePath, UiText.Of(language, UiText.LoginTitle), UiText.Of(language, UiText.TotpNotRegistered), basePath + "/login"),
                    StatusCodes.Status403Forbidden);

            case PasswordStatus.Denied:
                await audit.WriteAsync(
                    http, "auth.login.denied", StatusCodes.Status403Forbidden,
                    result.User!.UserKey, result.User.LoginId, "User", result.User.UserKey,
                    new { reason = result.Denial.ToString() }).ConfigureAwait(false);
                var key = result.Denial switch
                {
                    LoginDenial.PasswordExpired => UiText.PasswordExpired,
                    LoginDenial.LoginExpired => UiText.LoginExpired,
                    _ => UiText.CannotSignIn,
                };
                return Html(
                    HtmlPages.Message(language, basePath, UiText.Of(language, UiText.LoginTitle), UiText.Of(language, key), basePath + "/login"),
                    StatusCodes.Status403Forbidden);

            case PasswordStatus.LockedOut:
                await audit.WriteAsync(http, "auth.login.failure", StatusCodes.Status429TooManyRequests,
                    actorLoginId: loginId, detail: new { reason = "locked-out" }).ConfigureAwait(false);
                return Html(
                    HtmlPages.Login(language, basePath, returnUrl, csrf.Issue(http), loginId, UiText.LockedOut),
                    StatusCodes.Status429TooManyRequests);

            default:
                await audit.WriteAsync(http, "auth.login.failure", StatusCodes.Status401Unauthorized,
                    actorLoginId: loginId, detail: new { reason = "invalid-credentials" }).ConfigureAwait(false);
                return Html(HtmlPages.Login(language, basePath, returnUrl, csrf.Issue(http), loginId, UiText.InvalidCredentials));
        }
    }

    private static IResult ShowTotp(
        HttpContext http,
        SsoSessionService sessions,
        CsrfGuard csrf,
        AdminPathOptions adminPath,
        string? returnUrl)
    {
        var target = SafeReturnUrl(http, returnUrl, adminPath);
        if (sessions.ReadPendingMfa(http) is null)
        {
            return Html(HtmlPages.Login(Language(http), BasePath(http), target, csrf.Issue(http), errorKey: UiText.TotpSessionExpired));
        }

        return Html(HtmlPages.Totp(Language(http), BasePath(http), target, csrf.Issue(http)));
    }

    private static async Task<IResult> SubmitTotpAsync(
        HttpContext http,
        IPleasanterUserStore users,
        AuthenticationService authentication,
        SsoSessionService sessions,
        CsrfGuard csrf,
        AuditWriter audit,
        AdminPathOptions adminPath)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
        var language = Language(http);
        var basePath = BasePath(http);
        var returnUrl = SafeReturnUrl(http, form["returnUrl"], adminPath);

        if (!csrf.Validate(http, form[CsrfGuard.FormField]))
        {
            return Html(HtmlPages.Totp(language, basePath, returnUrl, csrf.Issue(http), UiText.CsrfFailed), StatusCodes.Status400BadRequest);
        }

        var pending = sessions.ReadPendingMfa(http);
        if (pending is null)
        {
            return Html(HtmlPages.Login(language, basePath, returnUrl, csrf.Issue(http), errorKey: UiText.TotpSessionExpired));
        }

        // **cookie の中身を信じ切らず、Pleasanter から引き直す。** この間に無効化されていれば通さない
        var user = await users.FindByUserIdAsync(pending.UserId, http.RequestAborted).ConfigureAwait(false);
        if (user is null || user.TenantId != pending.TenantId || LoginGate.Check(user, DateTime.Now) is not LoginDenial.None)
        {
            sessions.ClearPendingMfa(http);
            return Html(HtmlPages.Message(language, basePath, UiText.Of(language, UiText.LoginTitle), UiText.Of(language, UiText.CannotSignIn), basePath + "/login"),
                StatusCodes.Status403Forbidden);
        }

        switch (await authentication.VerifyTotpAsync(user, form["code"].ToString(), http.RequestAborted).ConfigureAwait(false))
        {
            case TotpStatus.Success:
                await audit.WriteAsync(
                    http, "auth.login.success", StatusCodes.Status200OK,
                    user.UserKey, user.LoginId, "User", user.UserKey,
                    new { methods = new[] { "pwd", "otp" } }).ConfigureAwait(false);
                await sessions.SignInAsync(http, user, ["pwd", "otp"]).ConfigureAwait(false);
                return Results.Redirect(returnUrl);

            case TotpStatus.LockedOut:
                sessions.ClearPendingMfa(http);
                await audit.WriteAsync(http, "auth.totp.failure", StatusCodes.Status429TooManyRequests,
                    user.UserKey, user.LoginId, "User", user.UserKey, new { reason = "locked-out" }).ConfigureAwait(false);
                return Html(HtmlPages.Login(language, basePath, returnUrl, csrf.Issue(http), user.LoginId, UiText.LockedOut),
                    StatusCodes.Status429TooManyRequests);

            default:
                await audit.WriteAsync(http, "auth.totp.failure", StatusCodes.Status401Unauthorized,
                    user.UserKey, user.LoginId, "User", user.UserKey, new { reason = "invalid-code" }).ConfigureAwait(false);
                return Html(HtmlPages.Totp(language, basePath, returnUrl, csrf.Issue(http), UiText.TotpInvalid));
        }
    }

    private static async Task<IResult> ShowLogoutAsync(HttpContext http, SsoSessionService sessions, CsrfGuard csrf)
    {
        var session = await sessions.GetCurrentAsync(http).ConfigureAwait(false);
        return Html(HtmlPages.LogoutConfirm(Language(http), BasePath(http), csrf.Issue(http), session?.UserName, null, null, "/logout"));
    }

    private static async Task<IResult> SubmitLogoutAsync(
        HttpContext http,
        SsoSessionService sessions,
        CsrfGuard csrf,
        AuditWriter audit)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
        var language = Language(http);
        if (!csrf.Validate(http, form[CsrfGuard.FormField]))
        {
            return Html(HtmlPages.Message(language, BasePath(http), UiText.Of(language, UiText.RequestErrorTitle), UiText.Of(language, UiText.CsrfFailed), BasePath(http) + "/login"),
                StatusCodes.Status400BadRequest);
        }

        var session = await sessions.GetCurrentAsync(http).ConfigureAwait(false);
        await sessions.SignOutAsync(http).ConfigureAwait(false);
        if (session is not null)
        {
            await audit.WriteAsync(http, "auth.logout", StatusCodes.Status200OK,
                $"{session.TenantId}:{session.UserId}", session.LoginId, "SsoSession", session.SessionId.ToString()).ConfigureAwait(false);
        }

        return Html(HtmlPages.Message(language, BasePath(http), UiText.Of(language, UiText.LogoutTitle), UiText.Of(language, UiText.LoggedOut), BasePath(http) + "/login"));
    }

    private static IResult Asset(HttpContext http, string content, string contentType)
    {
        http.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Text(content, contentType);
    }

    internal static string Language(HttpContext http) => UiText.Resolve(http.Request.Headers.AcceptLanguage.ToString());

    internal static string BasePath(HttpContext http) => http.Request.PathBase.Value ?? string.Empty;

    internal static IResult Html(string html, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);

    /// <summary>戻り先。安全でなければ管理画面の入口へ。</summary>
    private static string SafeReturnUrl(HttpContext http, string? returnUrl, AdminPathOptions adminPath) =>
        SsoSessionService.IsSafeReturnUrl(returnUrl, http.Request.PathBase)
            ? returnUrl!
            : http.Request.PathBase.Add(adminPath.Path).Value!;
}
