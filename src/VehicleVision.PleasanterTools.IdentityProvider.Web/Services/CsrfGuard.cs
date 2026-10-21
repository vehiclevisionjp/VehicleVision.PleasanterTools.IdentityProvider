using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>画面のフォーム（ログイン・サインアウト）を CSRF から守る。</summary>
/// <remarks>
/// <para>
/// **二重送信 cookie 方式。** フォームを出すときに乱数を cookie と hidden に入れ、
/// 送信時に両者の一致を見る。別サイトは、この IdP の cookie を読めないので合わせられない。
/// </para>
/// <para>
/// **加えて <c>Origin</c> を見る。** ヘッダがあれば、この IdP の origin と一致しなければ拒否する
/// （<c>Referer</c> も同じ扱い）。古いブラウザ向けに、無いときは cookie の一致だけで通す。
/// </para>
/// </remarks>
public sealed class CsrfGuard(IdpOptions options)
{
    public const string CookieName = "idp_csrf";

    public const string FormField = "csrf";

    private readonly string issuerOrigin = new Uri(options.Issuer).GetLeftPart(UriPartial.Authority);

    /// <summary>フォームへ埋める値を用意し、cookie を発行する。</summary>
    public string Issue(HttpContext http)
    {
        var token = Pkce.Base64Url(RandomNumberGenerator.GetBytes(32));
        http.Response.Cookies.Append(
            CookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = options.IsSecure,
                SameSite = SameSiteMode.Strict,
                Path = http.Request.PathBase.HasValue ? http.Request.PathBase.Value : "/",
                MaxAge = TimeSpan.FromHours(2),
                IsEssential = true,
            });
        return token;
    }

    public bool Validate(HttpContext http, string? formToken)
    {
        if (string.IsNullOrEmpty(formToken)
            || !http.Request.Cookies.TryGetValue(CookieName, out var cookieToken)
            || string.IsNullOrEmpty(cookieToken)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(cookieToken), Encoding.UTF8.GetBytes(formToken)))
        {
            return false;
        }

        return SameOrigin(http.Request.Headers.Origin.ToString())
            && SameOrigin(http.Request.Headers.Referer.ToString());
    }

    /// <summary>ヘッダが無ければ通す。あれば origin が IdP のものと一致すること。</summary>
    private bool SameOrigin(string header)
    {
        // **`Origin: null` は通さない。** 値があるのに origin として読めないものは別の出所
        if (string.IsNullOrEmpty(header))
        {
            return true;
        }

        return Uri.TryCreate(header, UriKind.Absolute, out var uri)
            && string.Equals(uri.GetLeftPart(UriPartial.Authority), issuerOrigin, StringComparison.OrdinalIgnoreCase);
    }
}
