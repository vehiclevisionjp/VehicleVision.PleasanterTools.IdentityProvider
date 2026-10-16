namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

/// <summary>OIDC のスコープ。</summary>
public static class Scopes
{
    public const string OpenId = "openid";

    public const string Profile = "profile";

    public const string Email = "email";

    /// <summary>Pleasanter のグループ名を <c>groups</c> に載せる（独自）。</summary>
    public const string Groups = "groups";

    public const string OfflineAccess = "offline_access";

    /// <summary>この IdP が扱うスコープ。</summary>
    public static readonly IReadOnlyList<string> Supported =
        [OpenId, Profile, Email, Groups, OfflineAccess];

    /// <summary>空白区切りの文字列を、重複を除いた順序付きの一覧へ。</summary>
    public static IReadOnlyList<string> Parse(string? scope) =>
        string.IsNullOrWhiteSpace(scope)
            ? []
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();

    public static string Join(IEnumerable<string> scopes) => string.Join(' ', scopes);

    /// <summary>要求のスコープのうち、この IdP が扱い、かつクライアントに許したものだけ。</summary>
    public static IReadOnlyList<string> Grant(IEnumerable<string> requested, IEnumerable<string> allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        return requested
            .Where(scope => Supported.Contains(scope, StringComparer.Ordinal) && allowedSet.Contains(scope))
            .ToArray();
    }

    /// <summary>要求したのに許されなかったスコープ。</summary>
    public static IReadOnlyList<string> Denied(IEnumerable<string> requested, IEnumerable<string> allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        return requested
            .Where(scope => !Supported.Contains(scope, StringComparer.Ordinal) || !allowedSet.Contains(scope))
            .ToArray();
    }
}
