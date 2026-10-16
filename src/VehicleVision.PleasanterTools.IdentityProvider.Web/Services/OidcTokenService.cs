using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>検証したアクセストークンの中身。</summary>
public sealed record AccessTokenInfo(string Subject, string ClientId, IReadOnlyList<string> Scopes, Guid SessionId);

/// <summary>検証した <c>id_token_hint</c> の中身。</summary>
public sealed record IdTokenHint(string Subject, string ClientId, Guid? SessionId);

/// <summary>公開鍵 1 つ（JWKS の要素）。</summary>
public sealed record JsonWebKeyInfo(string Kty, string Use, string Alg, string Kid, string N, string E);

/// <summary>OIDC の ID トークン・アクセストークン（JWT, RS256）の発行と検証。</summary>
/// <remarks>
/// <para>
/// **署名アルゴリズムは RS256 だけを受ける。** <c>alg: none</c> や、公開鍵を HMAC の秘密に使わせる
/// すり替えを許さない（<see cref="TokenValidationParameters.ValidAlgorithms"/>）。
/// </para>
/// <para>
/// アクセストークンも JWT（<c>typ: at+jwt</c>）。UserInfo は署名・有効期限に加え、
/// 発行元のセッションが失効していないかを確かめる（<c>sid</c>）。
/// </para>
/// </remarks>
public sealed class OidcTokenService(SigningKeyService keys, IdpOptions options, TimeProvider timeProvider)
{
    public const string AccessTokenType = "at+jwt";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private readonly JsonWebTokenHandler handler = new() { SetDefaultTimesOnTokenCreation = false };

    public string UserInfoAudience => options.Issuer;

    public async Task<string> CreateIdTokenAsync(
        OidcClient client,
        IReadOnlyDictionary<string, object> userClaims,
        string? nonce,
        DateTime authTime,
        IReadOnlyList<string> authMethods,
        Guid sessionId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var key = await keys.GetActiveAsync(Core.Admin.SigningKey.Oidc, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var claims = new Dictionary<string, object>(userClaims, StringComparer.Ordinal)
        {
            ["auth_time"] = new DateTimeOffset(authTime, TimeSpan.Zero).ToUnixTimeSeconds(),
            ["amr"] = authMethods.Select(AmrOf).Distinct(StringComparer.Ordinal).ToArray(),
            ["sid"] = sessionId.ToString(),
            ["at_hash"] = AtHash(accessToken),
            ["azp"] = client.ClientId,
        };
        if (!string.IsNullOrEmpty(nonce))
        {
            claims["nonce"] = nonce;
        }

        // 多要素なら、その旨を acr で伝える（RFC 8176 の amr と合わせて使う）
        claims["acr"] = authMethods.Contains("otp", StringComparer.Ordinal) ? "mfa" : "pwd";

        var lifetime = options.IdTokenLifetime;
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = client.ClientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.RsaSha256),
            TokenType = "JWT",
        });
    }

    public async Task<(string Token, TimeSpan Lifetime)> CreateAccessTokenAsync(
        OidcClient client,
        string subject,
        IReadOnlyList<string> scopes,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var key = await keys.GetActiveAsync(Core.Admin.SigningKey.Oidc, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lifetime = client.AccessTokenLifetimeSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : options.AccessTokenLifetime;

        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = UserInfoAudience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = subject,
                ["client_id"] = client.ClientId,
                ["scope"] = Scopes.Join(scopes),
                ["sid"] = sessionId.ToString(),
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.RsaSha256),
            TokenType = AccessTokenType,
        });
        return (token, lifetime);
    }

    public async Task<AccessTokenInfo?> ValidateAccessTokenAsync(string token, CancellationToken cancellationToken)
    {
        var result = await handler.ValidateTokenAsync(token, await ParametersAsync(
            audience: UserInfoAudience, validateLifetime: true, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        if (!result.IsValid
            || result.SecurityToken is not JsonWebToken jwt
            || !string.Equals(jwt.Typ, AccessTokenType, StringComparison.Ordinal)
            || !jwt.TryGetPayloadValue<string>("sub", out var subject)
            || !jwt.TryGetPayloadValue<string>("client_id", out var clientId)
            || !jwt.TryGetPayloadValue<string>("sid", out var sid)
            || !Guid.TryParse(sid, out var sessionId))
        {
            return null;
        }

        jwt.TryGetPayloadValue<string>("scope", out var scope);
        return new AccessTokenInfo(subject, clientId, Scopes.Parse(scope), sessionId);
    }

    /// <summary><c>id_token_hint</c> の検証。**署名と発行者・宛先だけを見て、期限は見ない**（OIDC RP-Initiated Logout 1.0）。</summary>
    public async Task<IdTokenHint?> ValidateIdTokenHintAsync(string token, CancellationToken cancellationToken)
    {
        var parameters = await ParametersAsync(audience: null, validateLifetime: false, cancellationToken).ConfigureAwait(false);
        var result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        if (!result.IsValid
            || result.SecurityToken is not JsonWebToken jwt
            || string.Equals(jwt.Typ, AccessTokenType, StringComparison.Ordinal)
            || !jwt.TryGetPayloadValue<string>("sub", out var subject)
            || jwt.Audiences.FirstOrDefault() is not { Length: > 0 } clientId)
        {
            return null;
        }

        Guid? sessionId = jwt.TryGetPayloadValue<string>("sid", out var sid) && Guid.TryParse(sid, out var parsed)
            ? parsed
            : null;
        return new IdTokenHint(subject, clientId, sessionId);
    }

    public async Task<IReadOnlyList<JsonWebKeyInfo>> GetJwksAsync(CancellationToken cancellationToken)
    {
        var all = await keys.GetAllAsync(Core.Admin.SigningKey.Oidc, cancellationToken).ConfigureAwait(false);
        return [.. all.Select(key =>
        {
            var parameters = key.Rsa.ExportParameters(includePrivateParameters: false);
            return new JsonWebKeyInfo(
                "RSA", "sig", "RS256", key.KeyId,
                Pkce.Base64Url(parameters.Modulus!), Pkce.Base64Url(parameters.Exponent!));
        })];
    }

    /// <summary><c>at_hash</c>。アクセストークンの SHA-256 の左半分（OIDC Core 3.1.3.6）。</summary>
    public static string AtHash(string accessToken)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Pkce.Base64Url(hash.AsSpan(0, hash.Length / 2));
    }

    private static string AmrOf(string method) => method switch
    {
        "pwd" => "pwd",
        "otp" => "otp",
        _ => method,
    };

    private async Task<TokenValidationParameters> ParametersAsync(
        string? audience,
        bool validateLifetime,
        CancellationToken cancellationToken)
    {
        var all = await keys.GetAllAsync(Core.Admin.SigningKey.Oidc, cancellationToken).ConfigureAwait(false);
        return new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidateIssuer = true,
            ValidAudience = audience,
            ValidateAudience = audience is not null,
            ValidateLifetime = validateLifetime,
            ClockSkew = ClockSkew,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = all.Select(key => (SecurityKey)key.SecurityKey).ToArray(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };
    }
}
