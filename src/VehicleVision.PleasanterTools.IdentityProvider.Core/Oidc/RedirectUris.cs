namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

/// <summary>リダイレクト URI の登録の検証と照合。</summary>
/// <remarks>
/// **完全一致だけ。** ワイルドカードや前方一致は認めない（OAuth 2.0 Security BCP / RFC 9700）。
/// 任意の URI へ認可コードを送らせる道になる。
/// </remarks>
public static class RedirectUris
{
    /// <summary>登録時の検証。問題があれば理由を返す。</summary>
    public static string? ValidateRegistration(string uri, bool isPublicClient)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return $"絶対 URI で書く必要がある: {uri}";
        }

        if (!string.IsNullOrEmpty(parsed.Fragment))
        {
            return $"フラグメント（#）は使えない: {uri}";
        }

        if (uri.Contains('*', StringComparison.Ordinal))
        {
            return $"ワイルドカードは使えない: {uri}";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return $"ユーザ情報（user:pass@）は使えない: {uri}";
        }

        if (parsed.Scheme == Uri.UriSchemeHttps)
        {
            return null;
        }

        if (parsed.Scheme == Uri.UriSchemeHttp)
        {
            // **平文の HTTP は、同じ端末へ戻るループバックだけ。**
            // 認可コードが盗聴される経路を作らない
            return IsLoopback(parsed) ? null : $"HTTP は localhost / ループバックだけ使える: {uri}";
        }

        // ネイティブアプリのカスタムスキームは、公開クライアントだけ
        return isPublicClient && parsed.Scheme.Contains('.', StringComparison.Ordinal)
            ? null
            : $"https の URI だけ登録できる（公開クライアントは逆ドメイン形式のカスタムスキームも可）: {uri}";
    }

    /// <summary>要求の URI が、登録されたものと完全に一致するか。</summary>
    public static bool Matches(IEnumerable<string> registered, string? requested)
    {
        if (string.IsNullOrEmpty(requested))
        {
            return false;
        }

        return registered.Any(candidate => string.Equals(candidate, requested, StringComparison.Ordinal));
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}
