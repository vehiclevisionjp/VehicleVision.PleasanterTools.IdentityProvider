using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>使える状態に読み込んだ署名鍵。</summary>
public sealed class LoadedSigningKey(
    string keyId,
    string status,
    RSA rsa,
    X509Certificate2 certificate) : IDisposable
{
    public string KeyId { get; } = keyId;

    public string Status { get; } = status;

    public RSA Rsa { get; } = rsa;

    /// <summary>秘密鍵付きの証明書（SAML の署名に使う）。</summary>
    public X509Certificate2 Certificate { get; } = certificate;

    public bool IsActive => Status == SigningKey.Active;

    /// <summary>JWT の署名に使う鍵。<c>kid</c> を付ける。</summary>
    public RsaSecurityKey SecurityKey { get; } = new(rsa) { KeyId = keyId };

    public void Dispose()
    {
        Certificate.Dispose();
        Rsa.Dispose();
    }
}

/// <summary>署名鍵の生成・読み込み・ローテーション。</summary>
/// <remarks>
/// <para>
/// **秘密鍵は暗号化して DB に置く**（<see cref="SecretProtector"/>、<c>IDP_SECRET_KEY</c>）。
/// 鍵束を共有しなくても、複数インスタンスが同じ鍵で署名できる。
/// </para>
/// <para>
/// **ローテーションしても、古い鍵（retired）は公開し続ける。** 発行済みのトークンの署名を
/// 検証できなくならないよう、管理画面から明示して消すまで JWKS / メタデータへ残す。
/// </para>
/// <para>
/// 読み込んだ鍵は短時間だけ覚える。別のインスタンスで行ったローテーションも、その時間内に反映される。
/// </para>
/// </remarks>
public sealed class SigningKeyService(
    ISigningKeyStore store,
    SecretProtector protector,
    IdpOptions options,
    TimeProvider timeProvider) : IDisposable
{
    /// <summary>RSA の鍵長。</summary>
    private const int RsaBits = 2048;

    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (DateTime LoadedAt, IReadOnlyList<LoadedSigningKey> Keys)> cache =
        new(StringComparer.Ordinal);

    /// <summary>用途ごとに、無ければ作る。起動時に呼ぶ。</summary>
    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        foreach (var purpose in new[] { SigningKey.Oidc, SigningKey.Saml })
        {
            var keys = await store.ListWithMaterialAsync(purpose, cancellationToken).ConfigureAwait(false);
            if (!keys.Any(key => key.Meta.Status == SigningKey.Active))
            {
                await RotateAsync(purpose, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>署名に使う鍵（active）。</summary>
    public async Task<LoadedSigningKey> GetActiveAsync(string purpose, CancellationToken cancellationToken = default)
    {
        var keys = await GetAllAsync(purpose, cancellationToken).ConfigureAwait(false);
        return keys.FirstOrDefault(key => key.IsActive)
            ?? throw new InvalidOperationException($"No active signing key for purpose '{purpose}'.");
    }

    /// <summary>公開する鍵（active と retired）。</summary>
    public async Task<IReadOnlyList<LoadedSigningKey>> GetAllAsync(
        string purpose,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(purpose, out var cached) && now - cached.LoadedAt < CacheTime)
            {
                return cached.Keys;
            }

            var stored = await store.ListWithMaterialAsync(purpose, cancellationToken).ConfigureAwait(false);
            var loaded = new List<LoadedSigningKey>();
            foreach (var key in stored)
            {
                var secret = protector.Unprotect(key.PrivateKeyEncrypted)
                    ?? throw new InvalidOperationException(
                        $"Signing key '{key.Meta.KeyId}' cannot be decrypted. Check IDP_SECRET_KEY.");
                var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(secret), out _);
                var certificate = X509Certificate2.CreateFromPem(key.CertificatePem).CopyWithPrivateKey(rsa);
                loaded.Add(new LoadedSigningKey(key.Meta.KeyId, key.Meta.Status, rsa, certificate));
            }

            cache[purpose] = (now, loaded);
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>新しい鍵を active にし、同じ用途の旧 active を retired にする。</summary>
    public async Task<SigningKey> RotateAsync(string purpose, CancellationToken cancellationToken = default)
    {
        if (purpose is not (SigningKey.Oidc or SigningKey.Saml))
        {
            throw new ArgumentException("purpose must be 'oidc' or 'saml'.", nameof(purpose));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        using var rsa = RSA.Create(RsaBits);
        var keyId = Guid.NewGuid().ToString("N")[..16];
        var host = new Uri(options.Issuer).Host;

        var request = new CertificateRequest($"CN={host}, O=IdentityProvider", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var notBefore = now.AddMinutes(-5);
        var notAfter = now.AddYears(10);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);

        var key = new StoredSigningKey(
            new SigningKey(
                keyId,
                purpose,
                "RS256",
                SigningKey.Active,
                certificate.Thumbprint.ToLowerInvariant(),
                notBefore,
                notAfter,
                now),
            protector.Protect(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())),
            certificate.ExportCertificatePem());

        await store.AddActiveAsync(key, cancellationToken).ConfigureAwait(false);
        await InvalidateAsync(purpose, cancellationToken).ConfigureAwait(false);
        return key.Meta;
    }

    /// <summary>キャッシュを捨てる（この instance。別 instance は <see cref="CacheTime"/> 以内に追随する）。</summary>
    public async Task InvalidateAsync(string purpose, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cache.Remove(purpose);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        gate.Dispose();
        foreach (var (_, entry) in cache)
        {
            foreach (var key in entry.Keys)
            {
                key.Dispose();
            }
        }
    }
}
