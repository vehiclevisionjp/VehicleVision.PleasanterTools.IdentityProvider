namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;

/// <summary>OIDC のクライアント登録。</summary>
/// <remarks>
/// 管理 API の JSON はこの型の形（<c>_documents/管理API仕様.md</c>）。
/// **シークレットは持たない**（ハッシュは DB の列にだけある）。
/// </remarks>
public sealed record OidcClient
{
    public const string Confidential = "confidential";

    public const string Public = "public";

    public string ClientId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ClientType { get; init; } = Confidential;

    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    public IReadOnlyList<string> PostLogoutRedirectUris { get; init; } = [];

    public IReadOnlyList<string> AllowedScopes { get; init; } = ["openid", "profile", "email"];

    /// <summary>空なら全テナント。</summary>
    public IReadOnlyList<int> AllowedTenantIds { get; init; } = [];

    public int? AccessTokenLifetimeSeconds { get; init; }

    public int? RefreshTokenLifetimeSeconds { get; init; }

    public bool Enabled { get; init; } = true;

    public bool HasSecret { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    public bool IsPublic => string.Equals(ClientType, Public, StringComparison.Ordinal);
}

/// <summary>SAML のサービスプロバイダ登録。</summary>
public sealed record SamlProvider
{
    public string ProviderId { get; init; } = string.Empty;

    public string EntityId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string AcsUrl { get; init; } = string.Empty;

    public string? SloUrl { get; init; }

    /// <summary>SP の署名用証明書（PEM または Base64）。署名付き要求の検証と、SLO の検証に使う。</summary>
    public string? SigningCertificate { get; init; }

    public bool RequireSignedRequests { get; init; }

    public string NameIdFormat { get; init; } = "emailAddress";

    public string NameIdSource { get; init; } = "email";

    public IReadOnlyList<SamlAttributeMapping> AttributeMappings { get; init; } = [];

    public bool SignAssertion { get; init; } = true;

    public bool SignResponse { get; init; } = true;

    public IReadOnlyList<int> AllowedTenantIds { get; init; } = [];

    public bool Enabled { get; init; } = true;

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }
}

public sealed record SamlAttributeMapping(string AttributeName, string Source);

/// <summary>署名鍵（秘密鍵は含めない）。</summary>
public sealed record SigningKey(
    string KeyId,
    string Purpose,
    string Algorithm,
    string Status,
    string Thumbprint,
    DateTime NotBefore,
    DateTime NotAfter,
    DateTime CreatedAt)
{
    public const string Oidc = "oidc";

    public const string Saml = "saml";

    public const string Active = "active";

    public const string Retired = "retired";
}

/// <summary>SSO セッション。</summary>
public sealed record SsoSession(
    string SessionId,
    int TenantId,
    int UserId,
    string LoginId,
    string Name,
    DateTime AuthTime,
    DateTime LastSeenAt,
    DateTime ExpiresAt,
    string? IpAddress,
    string? UserAgent,
    IReadOnlyList<string> AuthMethods,
    bool Current);
