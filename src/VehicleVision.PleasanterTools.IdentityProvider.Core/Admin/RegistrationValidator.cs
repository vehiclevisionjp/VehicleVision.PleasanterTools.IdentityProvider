using System.Security.Cryptography.X509Certificates;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;

/// <summary>管理画面から来た登録内容の検証。</summary>
/// <remarks>
/// **入力が来たところ（管理 API）で必ず通す。** 通らなかった内容は保存しない。
/// 戻り値は利用者へそのまま見せられる日本語の理由（空なら問題なし）。
/// </remarks>
public static class RegistrationValidator
{
    public const int MaximumClientIdLength = 128;

    public static IReadOnlyList<string> Validate(OidcClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(client.ClientId)
            || client.ClientId.Length > MaximumClientIdLength
            || !client.ClientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            errors.Add($"クライアント ID は英数字と - _ . だけで {MaximumClientIdLength} 文字以内にする");
        }

        if (string.IsNullOrWhiteSpace(client.DisplayName) || client.DisplayName.Length > 256)
        {
            errors.Add("表示名は 1〜256 文字で入力する");
        }

        if (client.ClientType is not (OidcClient.Confidential or OidcClient.Public))
        {
            errors.Add("クライアント種別は confidential か public");
        }

        if (client.RedirectUris.Count == 0)
        {
            errors.Add("リダイレクト URI を 1 つ以上登録する");
        }

        foreach (var uri in client.RedirectUris.Concat(client.PostLogoutRedirectUris))
        {
            if (RedirectUris.ValidateRegistration(uri, client.IsPublic) is { } reason)
            {
                errors.Add(reason);
            }
        }

        if (client.AllowedScopes.Any(scope => !Scopes.Supported.Contains(scope, StringComparer.Ordinal)))
        {
            errors.Add("扱えないスコープが含まれている（" + string.Join(" ", Scopes.Supported) + " のみ）");
        }

        if (!client.AllowedScopes.Contains(Scopes.OpenId, StringComparer.Ordinal))
        {
            errors.Add("スコープに openid が必要");
        }

        errors.AddRange(Admin.AccessRules.Validate(client.AccessRules));

        if (client.AllowedTenantIds.Any(id => id <= 0))
        {
            errors.Add("テナント ID は 1 以上の整数");
        }

        if (client.AccessTokenLifetimeSeconds is < 60 or > 86400)
        {
            errors.Add("アクセストークンの有効期間は 60〜86400 秒");
        }

        if (client.RefreshTokenLifetimeSeconds is < 300 or > 31_536_000)
        {
            errors.Add("リフレッシュトークンの有効期間は 300〜31536000 秒");
        }

        return errors;
    }

    public static IReadOnlyList<string> Validate(SamlProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(provider.EntityId) || provider.EntityId.Length > 512)
        {
            errors.Add("エンティティ ID は 1〜512 文字で入力する");
        }

        if (string.IsNullOrWhiteSpace(provider.DisplayName) || provider.DisplayName.Length > 256)
        {
            errors.Add("表示名は 1〜256 文字で入力する");
        }

        if (!IsHttpUrl(provider.AcsUrl))
        {
            errors.Add("ACS URL は https（または localhost の http）の絶対 URL");
        }

        if (!string.IsNullOrWhiteSpace(provider.SloUrl) && !IsHttpUrl(provider.SloUrl))
        {
            errors.Add("SLO URL は https（または localhost の http）の絶対 URL");
        }

        if (provider.NameIdFormat is not ("emailAddress" or "persistent" or "unspecified" or "transient"))
        {
            errors.Add("NameID 形式は emailAddress / persistent / unspecified / transient");
        }

        if (provider.NameIdSource is not ("loginId" or "email" or "userId" or "globalId"))
        {
            errors.Add("NameID の元は loginId / email / userId / globalId");
        }

        foreach (var mapping in provider.AttributeMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.AttributeName) || mapping.AttributeName.Length > 256)
            {
                errors.Add("属性名は 1〜256 文字で入力する");
            }

            if (!SamlAttributeSources.All.Contains(mapping.Source, StringComparer.Ordinal))
            {
                errors.Add($"属性の元が不正: {mapping.Source}");
            }
        }

        if (!string.IsNullOrWhiteSpace(provider.SigningCertificate)
            && CertificateText.TryParse(provider.SigningCertificate) is null)
        {
            errors.Add("署名用証明書を読み取れない（PEM または Base64 の DER）");
        }

        if (provider.RequireSignedRequests && string.IsNullOrWhiteSpace(provider.SigningCertificate))
        {
            errors.Add("署名付き要求を必須にするには、署名用証明書の登録が要る");
        }

        errors.AddRange(Admin.AccessRules.Validate(provider.AccessRules));

        if (provider.AllowedTenantIds.Any(id => id <= 0))
        {
            errors.Add("テナント ID は 1 以上の整数");
        }

        return errors;
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp
                && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))))
        && string.IsNullOrEmpty(uri.Fragment);
}

/// <summary>SAML の属性に載せられる情報の名前。</summary>
public static class SamlAttributeSources
{
    public static readonly IReadOnlyList<string> All =
        ["loginId", "email", "name", "userId", "userCode", "dept", "groups", "tenantId", "language"];
}

/// <summary>PEM / Base64 の証明書文字列を読む。</summary>
public static class CertificateText
{
    /// <summary>読めなければ <c>null</c>。</summary>
    public static X509Certificate2? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var trimmed = text.Trim();
            if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                return X509Certificate2.CreateFromPem(trimmed);
            }

            var compact = new string(trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(compact));
        }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
