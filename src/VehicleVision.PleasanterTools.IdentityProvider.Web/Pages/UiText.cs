using System.Collections.Frozen;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Pages;

/// <summary>サーバが描く画面（ログインなど）の文言。日本語と英語。</summary>
/// <remarks>
/// **言語は <c>Accept-Language</c> だけで決める。** 管理画面（SPA）は別に自分の文言を持つ。
/// 未翻訳の言語は英語へ落とす。
/// </remarks>
public static class UiText
{
    public const string Japanese = "ja";

    public const string English = "en";

    public const string LoginTitle = "login.title";
    public const string LoginId = "login.loginId";
    public const string Password = "login.password";
    public const string SignIn = "login.signIn";
    public const string InvalidCredentials = "login.invalidCredentials";
    public const string LockedOut = "login.lockedOut";
    public const string CannotSignIn = "login.cannotSignIn";
    public const string LoginExpired = "login.loginExpired";
    public const string PasswordExpired = "login.passwordExpired";
    public const string TotpTitle = "totp.title";
    public const string TotpPrompt = "totp.prompt";
    public const string TotpCode = "totp.code";
    public const string TotpVerify = "totp.verify";
    public const string TotpInvalid = "totp.invalid";
    public const string TotpNotRegistered = "totp.notRegistered";
    public const string TotpSessionExpired = "totp.sessionExpired";
    public const string Cancel = "common.cancel";
    public const string BackToLogin = "common.backToLogin";
    public const string LogoutTitle = "logout.title";
    public const string LogoutPrompt = "logout.prompt";
    public const string LogoutButton = "logout.button";
    public const string LoggedOut = "logout.done";
    public const string RequestErrorTitle = "error.title";
    public const string InvalidRequest = "error.invalidRequest";
    public const string UnknownClient = "error.unknownClient";
    public const string TenantNotAllowed = "error.tenantNotAllowed";
    public const string CsrfFailed = "error.csrf";
    public const string RedirectingTitle = "saml.redirecting";
    public const string RedirectingBody = "saml.redirectingBody";
    public const string Continue = "common.continue";
    public const string SignedInAs = "login.signedInAs";
    public const string NoMailForSaml = "saml.noMail";

    private static readonly FrozenDictionary<string, (string Ja, string En)> Texts =
        new Dictionary<string, (string, string)>
        {
            [LoginTitle] = ("サインイン", "Sign in"),
            [LoginId] = ("ログイン ID", "Login ID"),
            [Password] = ("パスワード", "Password"),
            [SignIn] = ("サインイン", "Sign in"),
            [InvalidCredentials] = ("ログイン ID またはパスワードが正しくありません。", "The login ID or password is incorrect."),
            [LockedOut] = (
                "試行が続いたため、しばらくサインインできません。時間をおいてやり直してください。",
                "Too many attempts. Sign-in is temporarily unavailable. Please try again later."),
            [CannotSignIn] = (
                "このアカウントではサインインできません。管理者へ連絡してください。",
                "This account cannot sign in. Please contact your administrator."),
            [LoginExpired] = (
                "ログインの有効期限が切れています。管理者へ連絡してください。",
                "Your login has expired. Please contact your administrator."),
            [PasswordExpired] = (
                "パスワードの有効期限が切れています。Pleasanter でパスワードを変更してから、もう一度サインインしてください。",
                "Your password has expired. Change it in Pleasanter, then sign in again."),
            [TotpTitle] = ("二段階認証", "Two-factor authentication"),
            [TotpPrompt] = (
                "認証アプリに表示されている 6 桁の数字を入力してください。",
                "Enter the 6-digit code shown in your authenticator app."),
            [TotpCode] = ("認証コード", "Authentication code"),
            [TotpVerify] = ("確認", "Verify"),
            [TotpInvalid] = ("認証コードが正しくありません。", "The authentication code is incorrect."),
            [TotpNotRegistered] = (
                "二段階認証が必要ですが、Pleasanter で認証アプリが登録されていません。Pleasanter で登録してから、もう一度サインインしてください。",
                "Two-factor authentication is required, but no authenticator is registered in Pleasanter. Register one in Pleasanter, then sign in again."),
            [TotpSessionExpired] = (
                "操作の有効時間が切れました。最初からやり直してください。",
                "The session timed out. Please start again."),
            [Cancel] = ("キャンセル", "Cancel"),
            [BackToLogin] = ("サインイン画面へ戻る", "Back to sign in"),
            [LogoutTitle] = ("サインアウト", "Sign out"),
            [LogoutPrompt] = ("サインアウトしますか？", "Do you want to sign out?"),
            [LogoutButton] = ("サインアウト", "Sign out"),
            [LoggedOut] = ("サインアウトしました。", "You have been signed out."),
            [RequestErrorTitle] = ("要求を処理できません", "The request cannot be processed"),
            [InvalidRequest] = ("要求が正しくありません。", "The request is invalid."),
            [UnknownClient] = (
                "このアプリケーションは登録されていないか、無効です。",
                "This application is not registered or is disabled."),
            [TenantNotAllowed] = (
                "このアプリケーションは、あなたのテナントでは使えません。",
                "This application is not available for your tenant."),
            [CsrfFailed] = (
                "画面の有効時間が切れました。もう一度やり直してください。",
                "The page has expired. Please try again."),
            [RedirectingTitle] = ("移動しています", "Redirecting"),
            [RedirectingBody] = (
                "自動で移動しない場合は、下のボタンを押してください。",
                "If you are not redirected automatically, press the button below."),
            [Continue] = ("続ける", "Continue"),
            [SignedInAs] = ("サインイン中: {0}", "Signed in as {0}"),
            [NoMailForSaml] = (
                "このアプリケーションはメールアドレスを必要としますが、Pleasanter にメールアドレスが登録されていません。",
                "This application requires an email address, but none is registered in Pleasanter."),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary><c>Accept-Language</c> から、画面の言語を決める。</summary>
    public static string Resolve(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return Japanese;
        }

        // 品質値は見ず、先頭に近い方を優先する（ja / en 以外は既定へ落とす）
        foreach (var part in acceptLanguage.Split(','))
        {
            var tag = part.Split(';')[0].Trim();
            if (tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            {
                return Japanese;
            }

            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return English;
            }
        }

        return English;
    }

    public static string Of(string language, string key, params object[] arguments)
    {
        if (!Texts.TryGetValue(key, out var text))
        {
            return key;
        }

        var value = language == Japanese ? text.Ja : text.En;
        return arguments.Length == 0
            ? value
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, value, arguments);
    }
}
