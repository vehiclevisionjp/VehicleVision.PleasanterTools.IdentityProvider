using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Tests;

public class SigningKeyServiceTests
{
    private static readonly string SecretKey = SecretProtector.GenerateKey();

    private static (SigningKeyService Service, FakeSigningKeyStore Store) Create()
    {
        var store = new FakeSigningKeyStore();
        var options = new IdpOptions { Issuer = "https://idp.example.com" };
        return (new SigningKeyService(store, new SecretProtector(SecretKey), options, TimeProvider.System), store);
    }

    [Fact]
    public async Task 初回は用途ごとに鍵を作り秘密鍵は暗号化して保存する()
    {
        var (service, store) = Create();

        await service.EnsureAsync();

        Assert.Equal(2, store.Count);
        foreach (var purpose in new[] { SigningKey.Oidc, SigningKey.Saml })
        {
            var stored = Assert.Single(await store.ListWithMaterialAsync(purpose));
            Assert.Equal(SigningKey.Active, stored.Meta.Status);
            Assert.DoesNotContain("PRIVATE", stored.PrivateKeyEncrypted, StringComparison.Ordinal);
            Assert.StartsWith("v1$", stored.PrivateKeyEncrypted, StringComparison.Ordinal);
            Assert.StartsWith("-----BEGIN CERTIFICATE-----", stored.CertificatePem, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE", stored.CertificatePem, StringComparison.Ordinal);
        }

        // 2 度呼んでも増えない
        await service.EnsureAsync();
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task 保存した鍵を読み戻して署名と検証ができる()
    {
        var (service, _) = Create();
        await service.EnsureAsync();

        var key = await service.GetActiveAsync(SigningKey.Oidc);
        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "i",
            Audience = "a",
            Subject = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "1")]),
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.RsaSha256),
        });

        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "i",
            ValidAudience = "a",
            IssuerSigningKey = key.SecurityKey,
        });
        Assert.True(result.IsValid);
        Assert.Equal(key.KeyId, new JsonWebToken(token).Kid);
        Assert.True(key.Certificate.HasPrivateKey);
    }

    [Fact]
    public async Task 鍵が違えば復号できず例外になる()
    {
        var (service, store) = Create();
        await service.EnsureAsync();
        var other = new SigningKeyService(store, new SecretProtector(SecretProtector.GenerateKey()), new IdpOptions { Issuer = "https://idp.example.com" }, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => other.GetActiveAsync(SigningKey.Oidc));
    }

    [Fact]
    public async Task ローテーションすると新しい鍵が有効になり旧鍵も公開に残る()
    {
        var (service, _) = Create();
        await service.EnsureAsync();
        var before = await service.GetActiveAsync(SigningKey.Saml);

        var rotated = await service.RotateAsync(SigningKey.Saml);

        var all = await service.GetAllAsync(SigningKey.Saml);
        Assert.Equal(2, all.Count);
        Assert.Equal(rotated.KeyId, (await service.GetActiveAsync(SigningKey.Saml)).KeyId);
        Assert.Contains(all, key => key.KeyId == before.KeyId && !key.IsActive);
    }

    [Fact]
    public async Task 用途が不正ならローテーションできない()
    {
        var (service, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => service.RotateAsync("other"));
    }
}

public class OidcTokenServiceTests
{
    private static readonly OidcClient Client = new()
    {
        ClientId = "app",
        DisplayName = "app",
        RedirectUris = ["https://app.example.com/cb"],
    };

    private static async Task<(OidcTokenService Tokens, SigningKeyService Keys)> CreateAsync(FakeTimeProvider? time = null)
    {
        var options = new IdpOptions { Issuer = "https://idp.example.com" };
        var keys = new SigningKeyService(new FakeSigningKeyStore(), new SecretProtector(SecretProtector.GenerateKey()), options, TimeProvider.System);
        await keys.EnsureAsync();
        return (new OidcTokenService(keys, options, time ?? new FakeTimeProvider(DateTimeOffset.UtcNow)), keys);
    }

    [Fact]
    public async Task アクセストークンを発行して検証できる()
    {
        var (tokens, _) = await CreateAsync();
        var session = Guid.NewGuid();

        var (token, lifetime) = await tokens.CreateAccessTokenAsync(Client, "42", ["openid", "profile"], session, CancellationToken.None);
        var info = await tokens.ValidateAccessTokenAsync(token, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("42", info.Subject);
        Assert.Equal("app", info.ClientId);
        Assert.Equal(["openid", "profile"], info.Scopes);
        Assert.Equal(session, info.SessionId);
        Assert.Equal(TimeSpan.FromMinutes(10), lifetime);
        Assert.Equal("at+jwt", new JsonWebToken(token).Typ);
    }

    [Fact]
    public async Task クライアントごとの有効期間を使える()
    {
        var (tokens, _) = await CreateAsync();

        var (_, lifetime) = await tokens.CreateAccessTokenAsync(
            Client with { AccessTokenLifetimeSeconds = 120 }, "1", ["openid"], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(120), lifetime);
    }

    [Fact]
    public async Task 期限が切れたアクセストークンは通らない()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(-2));
        var (tokens, _) = await CreateAsync(time);
        var (token, _) = await tokens.CreateAccessTokenAsync(Client, "1", ["openid"], Guid.NewGuid(), CancellationToken.None);

        Assert.Null(await tokens.ValidateAccessTokenAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task IDトークンはアクセストークンとして使えずその逆も通らない()
    {
        var (tokens, _) = await CreateAsync();
        var session = Guid.NewGuid();
        var (access, _) = await tokens.CreateAccessTokenAsync(Client, "1", ["openid"], session, CancellationToken.None);
        var id = await tokens.CreateIdTokenAsync(
            Client, new Dictionary<string, object> { ["sub"] = "1" }, "n", DateTime.UtcNow, ["pwd"], session, access, CancellationToken.None);

        // ID トークン（typ=JWT・aud=クライアント）は UserInfo のアクセストークンにならない
        Assert.Null(await tokens.ValidateAccessTokenAsync(id, CancellationToken.None));

        // アクセストークン（typ=at+jwt）は id_token_hint にならない
        Assert.Null(await tokens.ValidateIdTokenHintAsync(access, CancellationToken.None));
        Assert.NotNull(await tokens.ValidateIdTokenHintAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task IDトークンに必要なクレームが載る()
    {
        var (tokens, _) = await CreateAsync();
        var authTime = new DateTime(2026, 10, 17, 3, 0, 0, DateTimeKind.Utc);

        var id = await tokens.CreateIdTokenAsync(
            Client, new Dictionary<string, object> { ["sub"] = "42", ["name"] = "山田" }, "nonce-x", authTime, ["pwd", "otp"], Guid.NewGuid(), "access", CancellationToken.None);
        var jwt = new JsonWebToken(id);

        Assert.Equal("https://idp.example.com", jwt.Issuer);
        Assert.Equal("app", jwt.Audiences.Single());
        Assert.Equal("42", jwt.Subject);
        Assert.Equal("nonce-x", jwt.GetPayloadValue<string>("nonce"));
        Assert.Equal(new DateTimeOffset(authTime).ToUnixTimeSeconds(), jwt.GetPayloadValue<long>("auth_time"));
        Assert.Equal(["pwd", "otp"], jwt.GetPayloadValue<string[]>("amr"));
        Assert.Equal("mfa", jwt.GetPayloadValue<string>("acr"));
        Assert.Equal("app", jwt.GetPayloadValue<string>("azp"));
        Assert.Equal(OidcTokenService.AtHash("access"), jwt.GetPayloadValue<string>("at_hash"));
        Assert.Equal("RS256", jwt.Alg);
    }

    [Fact]
    public async Task 署名を外したトークンは通らない()
    {
        var (tokens, _) = await CreateAsync();
        var (token, _) = await tokens.CreateAccessTokenAsync(Client, "1", ["openid"], Guid.NewGuid(), CancellationToken.None);
        var parts = token.Split('.');
        var header = Pkce.Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"at+jwt\"}"));

        Assert.Null(await tokens.ValidateAccessTokenAsync($"{header}.{parts[1]}.", CancellationToken.None));
        Assert.Null(await tokens.ValidateAccessTokenAsync($"{parts[0]}.{parts[1]}.", CancellationToken.None));
    }

    [Fact]
    public async Task 公開鍵をHMACの秘密に使った偽造は通らない()
    {
        var (tokens, keys) = await CreateAsync();
        var key = await keys.GetActiveAsync(SigningKey.Oidc);
        var publicKey = key.Rsa.ExportSubjectPublicKeyInfo();

        // アルゴリズムのすり替え攻撃: 公開鍵の値を HS256 の共有秘密にして署名する
        var handler = new JsonWebTokenHandler();
        var forged = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://idp.example.com",
            Audience = "https://idp.example.com",
            Claims = new Dictionary<string, object> { ["sub"] = "1", ["client_id"] = "app", ["sid"] = Guid.NewGuid().ToString() },
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(publicKey) { KeyId = key.KeyId }, SecurityAlgorithms.HmacSha256),
            TokenType = "at+jwt",
        });

        Assert.Null(await tokens.ValidateAccessTokenAsync(forged, CancellationToken.None));
    }

    [Fact]
    public async Task 別の鍵で署名したトークンは通らない()
    {
        var (tokens, _) = await CreateAsync();
        using var rsa = RSA.Create(2048);
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://idp.example.com",
            Audience = "https://idp.example.com",
            Claims = new Dictionary<string, object> { ["sub"] = "1", ["client_id"] = "app", ["sid"] = Guid.NewGuid().ToString() },
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "other" }, SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
        });

        Assert.Null(await tokens.ValidateAccessTokenAsync(forged, CancellationToken.None));
    }

    [Fact]
    public async Task JWKSには公開鍵だけが載る()
    {
        var (tokens, keys) = await CreateAsync();
        await keys.RotateAsync(SigningKey.Oidc);

        var jwks = await tokens.GetJwksAsync(CancellationToken.None);

        Assert.Equal(2, jwks.Count);
        Assert.All(jwks, key =>
        {
            Assert.Equal("RSA", key.Kty);
            Assert.Equal("RS256", key.Alg);
            Assert.Equal("sig", key.Use);
            Assert.False(string.IsNullOrEmpty(key.Kid));
            Assert.True(key.N.Length > 300);
        });
        Assert.Equal(2, jwks.Select(key => key.Kid).Distinct().Count());
    }

    [Fact]
    public void at_hashはSHA256の左半分()
    {
        // OIDC Core 付録 A.3 の例ではなく、定義どおりに計算した値と一致することを見る
        var expected = Pkce.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes("jHkWEdUXMU1BwAsC4vtUsZwnNvTIxEl0z9K3vx5KF0Y"))[..16]);

        Assert.Equal(expected, OidcTokenService.AtHash("jHkWEdUXMU1BwAsC4vtUsZwnNvTIxEl0z9K3vx5KF0Y"));
        Assert.Equal("77QmUPtjPfzWtF2AnpK9RQ", OidcTokenService.AtHash("jHkWEdUXMU1BwAsC4vtUsZwnNvTIxEl0z9K3vx5KF0Y"));
    }
}
