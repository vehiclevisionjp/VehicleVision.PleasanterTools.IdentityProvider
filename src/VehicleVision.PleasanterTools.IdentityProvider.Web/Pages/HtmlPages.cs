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

    /// <summary>
    /// 同梱の書体（DesignReference の <c>app-fonts.css</c>）。管理画面と同じ 1 つを使う。
    /// フロントエンドの <c>public/css/</c> と <c>public/fonts/app/</c> が <c>wwwroot</c> へそのまま出る
    /// （組み立て前や配置に無いときは 404 になり、端末の書体で表示される）。
    /// </summary>
    public const string FontsCssPath = "/css/app-fonts.css";

    public const string PostScriptPath = "/idp-assets/post.js";

    /// <summary>SAML の POST バインディングで、フォームを自動送信するための 1 行。</summary>
    public const string PostScript = "document.getElementById('autoPost').submit();\n";

    /// <summary>
    /// サインイン・二段階認証・サインアウトなどの画面の CSS。
    /// **基準は VehicleVision.DesignReference 第 2.2 版（コミット 73943fd41880dd1144bae2f3a67a09e7f3bf6e32、参照日 2026-11-01）。**
    /// 値は <c>css/colors-explicit.css</c>（ライト）と <c>css/app-base.css</c>、部品は <c>components/app-ui.css</c> の複写。
    /// <para>
    /// **ライトだけ。** この画面には切り替えが無く、OS の <c>prefers-color-scheme</c> にも自動では従わない
    /// （明示型の既定。管理画面でダークを選んでも、この画面はライトのまま）。
    /// </para>
    /// </summary>
    public const string Css = """
        :root { color-scheme: light;
                --color-primary: #17184b; --color-primary-hover: #0e0f33; --color-primary-subtle: #e9e9f3;
                --color-primary-text: #17184b; --color-primary-edge: #17184b;
                --color-text: #0b1013; --color-text-muted: #595857; --color-border: #bdc0ba; --color-border-strong: #85887f;
                --color-surface: #fffffc; --color-bg: #f3f3f2; --color-on-fill: #ffffff;
                --color-danger-text: #b7282e; --color-danger-subtle: #fbeaea;
                --font-sans: "Noto Sans CJK JP", "Noto Sans JP", "Hiragino Sans", "Yu Gothic UI", "Yu Gothic", Meiryo, system-ui, sans-serif;
                --fs-xs: 12px; --fs-sm: 14px; --fs-md: 16px; --fs-xl: 28px;
                --space-1: 4px; --space-2: 8px; --space-3: 12px; --space-4: 16px; --space-6: 24px; --space-8: 32px;
                --radius-md: 8px; --radius-lg: 16px; --shadow-sm: 0 1px 2px rgba(0, 0, 0, .08);
                --control-h: 40px; --focus-outline: 3px solid var(--color-primary-text); }
        *, *::before, *::after { box-sizing: border-box; }
        html { -webkit-text-size-adjust: 100%; text-size-adjust: 100%; }
        body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: var(--space-4);
               background: var(--color-bg); color: var(--color-text);
               font-family: var(--font-sans); font-size: var(--fs-md); line-height: 1.7; }
        main { width: min(26rem, 100%); background: var(--color-surface); border: 1px solid var(--color-border);
               border-top: 6px solid var(--color-primary); border-radius: var(--radius-lg); box-shadow: var(--shadow-sm);
               padding: var(--space-8); }
        h1 { margin: 0 0 var(--space-4); font-size: 20px; line-height: 1.4; }
        p { margin: 0 0 var(--space-3); }
        label { display: block; margin: var(--space-4) 0 var(--space-1); font-size: var(--fs-sm); font-weight: 700; }
        input[type=text], input[type=password] { width: 100%; min-height: var(--control-h); padding: var(--space-2) var(--space-3);
               font: inherit; font-size: var(--fs-sm); color: var(--color-text); background: var(--color-surface);
               border: 1px solid var(--color-border-strong); border-radius: var(--radius-md); }
        input:hover { border-color: var(--color-text-muted); }
        input:focus-visible { border-color: var(--color-primary-text); }
        :focus-visible { outline: var(--focus-outline); outline-offset: 2px; }
        button, .button { display: inline-flex; align-items: center; justify-content: center; min-height: var(--control-h);
               margin-top: var(--space-6); padding: 0 var(--space-4); font: inherit; font-size: var(--fs-sm); font-weight: 700; line-height: 1.2;
               color: var(--color-on-fill); background: var(--color-primary); border: 1px solid var(--color-primary-edge);
               border-radius: var(--radius-md); cursor: pointer; text-decoration: none; white-space: nowrap; }
        button:hover, .button:hover { background: var(--color-primary-hover); }
        button.secondary, .button.secondary { color: var(--color-primary-text); background: var(--color-surface); border-color: var(--color-primary-text); }
        button.secondary:hover, .button.secondary:hover { background: var(--color-primary-subtle); }
        .row { display: flex; gap: var(--space-3); flex-wrap: wrap; }
        .error { display: flex; gap: var(--space-3); margin: 0 0 var(--space-4); padding: var(--space-3) var(--space-4);
               font-size: var(--fs-sm); color: var(--color-text); background: var(--color-danger-subtle);
               border: 1px solid var(--color-danger-text); border-left-width: 6px; border-radius: var(--radius-md); }
        .error::before { content: "\00d7"; flex: none; width: 1.5em; height: 1.5em; display: grid; place-items: center;
               font-size: var(--fs-sm); font-weight: 700; line-height: 1; color: var(--color-surface);
               background: var(--color-danger-text); border-radius: 50%; }
        .muted { color: var(--color-text-muted); font-size: var(--fs-sm); }
        a { color: var(--color-primary-text); }
        @media (prefers-reduced-motion: reduce) { *, *::before, *::after { animation-duration: .01ms !important; transition-duration: .01ms !important; } }
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
        <meta name="theme-color" content="#17184b">
        <link rel="icon" href="{Enc(basePath + "/favicon.svg")}" type="image/svg+xml">
        <link rel="icon" href="{Enc(basePath + "/favicon.ico")}" sizes="any">
        <title>{Enc(title)}</title>
        <link rel="stylesheet" href="{Enc(basePath + FontsCssPath)}">
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
