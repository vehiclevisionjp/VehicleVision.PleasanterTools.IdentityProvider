using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

/// <summary>Pleasanter のユーザから、OIDC / SAML の属性を作る。</summary>
/// <remarks>
/// **載せるのはスコープ（SAML は属性の対応表）で許された分だけ。**
/// パスワードのハッシュ・TOTP の共有鍵・API キーは、どのスコープでも載せない。
/// </remarks>
public static class UserClaims
{
    /// <summary><c>sub</c>。<c>Users.UserId</c> は Pleasanter の DB 全体で一意で、ログイン ID の変更にも追従する。</summary>
    public static string Subject(PleasanterUser user) =>
        user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>UserInfo / ID トークンのクレーム（<c>sub</c> を含む）。</summary>
    public static IReadOnlyDictionary<string, object> ForScopes(
        PleasanterUserProfile profile,
        IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var granted = scopes.ToHashSet(StringComparer.Ordinal);
        var user = profile.User;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = Subject(user),
        };

        if (granted.Contains(Scopes.Profile))
        {
            claims["name"] = user.Name;
            claims["preferred_username"] = user.LoginId;
            claims["tenant_id"] = user.TenantId;

            if (!string.IsNullOrWhiteSpace(user.Language))
            {
                claims["locale"] = user.Language;
            }

            if (!string.IsNullOrWhiteSpace(user.TimeZone))
            {
                claims["zoneinfo"] = user.TimeZone;
            }

            if (!string.IsNullOrWhiteSpace(profile.DeptName))
            {
                claims["dept"] = profile.DeptName;
            }

            if (!string.IsNullOrWhiteSpace(user.UserCode))
            {
                claims["user_code"] = user.UserCode;
            }

            claims["tenant_manager"] = user.TenantManager;
            claims["service_manager"] = user.ServiceManager;
        }

        if (granted.Contains(Scopes.Email) && profile.PrimaryMail is { } mail)
        {
            claims["email"] = mail;

            // **Pleasanter はメールアドレスの所有確認をしない。** 確認済みとは言わない
            claims["email_verified"] = false;
        }

        if (granted.Contains(Scopes.Groups))
        {
            claims["groups"] = profile.Groups;
        }

        return claims;
    }

    /// <summary>SAML の属性値（<see cref="Admin.SamlAttributeSources"/> の名前から引く）。複数値になり得る。</summary>
    public static IReadOnlyList<string> SamlValues(PleasanterUserProfile profile, string source)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var user = profile.User;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        return source switch
        {
            "loginId" => [user.LoginId],
            "email" => profile.MailAddresses.Count > 0 ? [profile.MailAddresses[0]] : [],
            "name" => [user.Name],
            "userId" => [user.UserId.ToString(invariant)],
            "userCode" => string.IsNullOrWhiteSpace(user.UserCode) ? [] : [user.UserCode],
            "dept" => string.IsNullOrWhiteSpace(profile.DeptName) ? [] : [profile.DeptName],
            "groups" => profile.Groups,
            "tenantId" => [user.TenantId.ToString(invariant)],
            "language" => string.IsNullOrWhiteSpace(user.Language) ? [] : [user.Language],
            _ => [],
        };
    }

    /// <summary>SAML の NameID の値。取れなければ <c>null</c>（メール未登録など）。</summary>
    public static string? SamlNameId(PleasanterUserProfile profile, string nameIdSource) =>
        nameIdSource switch
        {
            "loginId" => profile.User.LoginId,
            "email" => profile.PrimaryMail,
            "userId" => Subject(profile.User),
            "globalId" => string.IsNullOrWhiteSpace(profile.User.GlobalId) ? null : profile.User.GlobalId,
            _ => null,
        };
}
