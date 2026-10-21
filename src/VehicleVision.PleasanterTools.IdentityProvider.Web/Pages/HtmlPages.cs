using System.Text;
using System.Text.Encodings.Web;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Pages;

/// <summary>サーバが描く画面（サインイン・二段階認証・サインアウト・エラー）。</summary>
/// <remarks>
/// <para>
/// **値は必ずエンコードして埋める。** 戻り先・メッセージ・フォームの値に外から来る文字列が入るため、
/// 1 か所でも生で出すと XSS になる。
/// </para>
/// <para>
/// **インラインの <c>&lt;style&gt;</c> と <c>&lt;script&gt;</c> を使わない。**
/// CSP（<c>script-src 'self'</c>）を緩めずに済ませるため、CSS と JS は同じ origin の別ファイルで配る
/// （<see cref="Css"/> と <see cref="PostScript"/>）。
/// </para>
/// </remarks>
public static class HtmlPages
{
    public const string CssPath = "/idp-assets/login.css";

    public const string PostScriptPath = "/idp-assets/post.js";

    /// <summary>SAML の POST バインディングで、フォームを自動送信するための 1 行。</summary>
    public const string PostScript = "document.getElementById('autoPost').submit();\n";

    public const string Css = """
        :root { color-scheme: light dark; --bg: #f3f4f6; --card: #ffffff; --text: #111827; --muted: #6b7280;
                --border: #d1d5db; --accent: #2563eb; --accent-text: #ffffff; --error: #b91c1c; --error-bg: #fef2f2; }
        @media (prefers-color-scheme: dark) {
          :root { --bg: #0f172a; --card: #1e293b; --text: #f1f5f9; --muted: #94a3b8; --border: #475569;
                  --accent: #60a5fa; --accent-text: #0f172a; --error: #fca5a5; --error-bg: #450a0a; }
        }
        * { box-sizing: border-box; }
        body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--text);
               font-family: system-ui, -apple-system, "Segoe UI", "Hiragino Sans", "Yu Gothic UI", Meiryo, sans-serif; }
        main { width: min(26rem, calc(100vw - 2rem)); background: var(--card); border: 1px solid var(--border);
               border-radius: 0.75rem; padding: 2rem; }
        h1 { margin: 0 0 1.25rem; font-size: 1.4rem; }
        p { line-height: 1.6; }
        label { display: block; margin: 1rem 0 0.35rem; font-weight: 600; }
        input[type=text], input[type=password] { width: 100%; padding: 0.65rem 0.75rem; font: inherit; color: inherit;
               background: transparent; border: 1px solid var(--border); border-radius: 0.5rem; }
        input:focus-visible, button:focus-visible, a:focus-visible { outline: 3px solid var(--accent); outline-offset: 2px; }
        button, .button { display: inline-block; margin-top: 1.5rem; padding: 0.7rem 1.25rem; font: inherit; font-weight: 600;
               color: var(--accent-text); background: var(--accent); border: 0; border-radius: 0.5rem; cursor: pointer;
               text-decoration: none; }
        button.secondary, .button.secondary { color: var(--text); background: transparent; border: 1px solid var(--border); }
        .row { display: flex; gap: 0.75rem; flex-wrap: wrap; }
        .error { margin: 0 0 1rem; padding: 0.75rem 1rem; color: var(--error); background: var(--error-bg); border-radius: 0.5rem; }
        .muted { color: var(--muted); font-size: 0.9rem; }
        a { color: var(--accent); }
        """;

    public static string Login(
        string language,
        string basePath,
        string returnUrl,
        string csrfToken,
        string? loginId = null,
        string? errorKey = null)
    {
        var body = new StringBuilder();
        AppendError(body, language, errorKey);
        body.Append("<form method=\"post\" action=\"").Append(Enc(basePath + "/login")).Append("\" autocomplete=\"on\">");
        Hidden(body, "returnUrl", returnUrl);
        Hidden(body, "csrf", csrfToken);
        body.Append("<label for=\"loginId\">").Append(Enc(UiText.Of(language, UiText.LoginId))).Append("</label>");
        body.Append("<input type=\"text\" id=\"loginId\" name=\"loginId\" autocomplete=\"username\" required autofocus maxlength=\"256\" value=\"")
            .Append(Enc(loginId ?? string.Empty)).Append("\">");
        body.Append("<label for=\"password\">").Append(Enc(UiText.Of(language, UiText.Password))).Append("</label>");
        body.Append("<input type=\"password\" id=\"password\" name=\"password\" autocomplete=\"current-password\" required maxlength=\"256\">");
        body.Append("<button type=\"submit\">").Append(Enc(UiText.Of(language, UiText.SignIn))).Append("</button>");
        body.Append("</form>");
        return Page(language, basePath, UiText.Of(language, UiText.LoginTitle), body.ToString());
    }

    public static string Totp(
        string language,
        string basePath,
        string returnUrl,
        string csrfToken,
        string? errorKey = null)
    {
        var body = new StringBuilder();
        AppendError(body, language, errorKey);
        body.Append("<p>").Append(Enc(UiText.Of(language, UiText.TotpPrompt))).Append("</p>");
        body.Append("<form method=\"post\" action=\"").Append(Enc(basePath + "/login/totp")).Append("\">");
        Hidden(body, "returnUrl", returnUrl);
        Hidden(body, "csrf", csrfToken);
        body.Append("<label for=\"code\">").Append(Enc(UiText.Of(language, UiText.TotpCode))).Append("</label>");
        body.Append("<input type=\"text\" id=\"code\" name=\"code\" inputmode=\"numeric\" pattern=\"[0-9 ]*\" autocomplete=\"one-time-code\" required autofocus maxlength=\"12\">");
        body.Append("<div class=\"row\"><button type=\"submit\">").Append(Enc(UiText.Of(language, UiText.TotpVerify))).Append("</button>");
        body.Append("<a class=\"button secondary\" href=\"").Append(Enc(basePath + "/login?returnUrl=" + Uri.EscapeDataString(returnUrl)))
            .Append("\">").Append(Enc(UiText.Of(language, UiText.Cancel))).Append("</a></div>");
        body.Append("</form>");
        return Page(language, basePath, UiText.Of(language, UiText.TotpTitle), body.ToString());
    }

    /// <summary>メッセージだけの画面。<paramref name="linkUrl"/> があればボタンを 1 つ出す。</summary>
    public static string Message(
        string language,
        string basePath,
        string title,
        string message,
        string? linkUrl = null,
        string? linkText = null)
    {
        var body = new StringBuilder();
        body.Append("<p>").Append(Enc(message)).Append("</p>");
        if (linkUrl is not null)
        {
            body.Append("<a class=\"button\" href=\"").Append(Enc(linkUrl)).Append("\">")
                .Append(Enc(linkText ?? UiText.Of(language, UiText.BackToLogin))).Append("</a>");
        }

        return Page(language, basePath, title, body.ToString());
    }

    public static string LogoutConfirm(
        string language,
        string basePath,
        string csrfToken,
        string? userName,
        string? postLogoutRedirectUri,
        string? state,
        string actionPath = "/oidc/logout")
    {
        var body = new StringBuilder();
        body.Append("<p>").Append(Enc(UiText.Of(language, UiText.LogoutPrompt))).Append("</p>");
        if (!string.IsNullOrEmpty(userName))
        {
            body.Append("<p class=\"muted\">").Append(Enc(UiText.Of(language, UiText.SignedInAs, userName))).Append("</p>");
        }

        body.Append("<form method=\"post\" action=\"").Append(Enc(basePath + actionPath)).Append("\">");
        Hidden(body, "csrf", csrfToken);
        if (postLogoutRedirectUri is not null)
        {
            Hidden(body, "post_logout_redirect_uri", postLogoutRedirectUri);
        }

        if (state is not null)
        {
            Hidden(body, "state", state);
        }

        body.Append("<button type=\"submit\">").Append(Enc(UiText.Of(language, UiText.LogoutButton))).Append("</button>");
        body.Append("</form>");
        return Page(language, basePath, UiText.Of(language, UiText.LogoutTitle), body.ToString());
    }

    /// <summary>SAML の POST バインディング。自動送信し、JS が動かなくてもボタンで進める。</summary>
    public static string AutoPost(
        string language,
        string basePath,
        string action,
        IReadOnlyDictionary<string, string> fields)
    {
        var body = new StringBuilder();
        body.Append("<p>").Append(Enc(UiText.Of(language, UiText.RedirectingBody))).Append("</p>");
        body.Append("<form id=\"autoPost\" method=\"post\" action=\"").Append(Enc(action)).Append("\">");
        foreach (var (name, value) in fields)
        {
            Hidden(body, name, value);
        }

        body.Append("<button type=\"submit\">").Append(Enc(UiText.Of(language, UiText.Continue))).Append("</button></form>");
        body.Append("<script src=\"").Append(Enc(basePath + PostScriptPath)).Append("\"></script>");
        return Page(language, basePath, UiText.Of(language, UiText.RedirectingTitle), body.ToString());
    }

    private static string Page(string language, string basePath, string title, string body) =>
        $"""
        <!doctype html>
        <html lang="{Enc(language)}">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>{Enc(title)}</title>
        <link rel="stylesheet" href="{Enc(basePath + CssPath)}">
        </head>
        <body>
        <main>
        <h1>{Enc(title)}</h1>
        {body}
        </main>
        </body>
        </html>
        """;

    private static void AppendError(StringBuilder body, string language, string? errorKey)
    {
        if (errorKey is not null)
        {
            body.Append("<p class=\"error\" role=\"alert\">").Append(Enc(UiText.Of(language, errorKey))).Append("</p>");
        }
    }

    private static void Hidden(StringBuilder body, string name, string value) =>
        body.Append("<input type=\"hidden\" name=\"").Append(Enc(name)).Append("\" value=\"").Append(Enc(value)).Append("\">");

    /// <summary>日本語をそのまま出す（<c>&lt; &gt; &amp; " '</c> は逃がしたまま）。</summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

    private static string Enc(string value) => Encoder.Encode(value);
}
