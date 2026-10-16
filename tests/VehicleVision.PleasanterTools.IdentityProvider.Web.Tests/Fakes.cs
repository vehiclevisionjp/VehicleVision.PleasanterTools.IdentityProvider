using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Tests;

/// <summary>メモリ上のログイン失敗の記録（DB の実装と同じ約束を守る）。</summary>
internal sealed class FakeLoginSecurityStore : ILoginSecurityStore
{
    private readonly Dictionary<string, (int Count, DateTime? LockedUntil, DateTime LastFailedAt)> failures = [];
    private readonly Dictionary<string, long> steps = [];

    public Task<DateTime?> GetLockedUntilAsync(string loginKey, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var key = loginKey.Trim().ToLowerInvariant();
        return Task.FromResult(
            failures.TryGetValue(key, out var state) && state.LockedUntil > nowUtc ? state.LockedUntil : null);
    }

    public Task<LoginFailureState> RecordFailureAsync(
        string loginKey, DateTime nowUtc, int threshold, TimeSpan window, TimeSpan lockoutDuration, CancellationToken cancellationToken = default)
    {
        var key = loginKey.Trim().ToLowerInvariant();
        failures.TryGetValue(key, out var current);
        var count = current.LastFailedAt != default && current.LastFailedAt >= nowUtc - window ? current.Count + 1 : 1;
        var lockedUntil = count >= threshold ? nowUtc + lockoutDuration : current.LockedUntil;
        failures[key] = (count, lockedUntil, nowUtc);
        return Task.FromResult(new LoginFailureState(count, lockedUntil));
    }

    public Task ResetAsync(string loginKey, CancellationToken cancellationToken = default)
    {
        failures.Remove(loginKey.Trim().ToLowerInvariant());
        return Task.CompletedTask;
    }

    public Task<bool> TryUseTotpStepAsync(string userKey, long timeStep, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (steps.TryGetValue(userKey, out var last) && last >= timeStep)
        {
            return Task.FromResult(false);
        }

        steps[userKey] = timeStep;
        return Task.FromResult(true);
    }
}

/// <summary>メモリ上の Pleasanter のユーザ。</summary>
internal sealed class FakePleasanterUserStore(params PleasanterUser[] users) : IPleasanterUserStore
{
    public int Lookups { get; private set; }

    public Task<PleasanterUser?> FindByLoginIdAsync(string loginId, CancellationToken cancellationToken = default)
    {
        Lookups++;
        return Task.FromResult(users.FirstOrDefault(user =>
            string.Equals(user.LoginId, loginId.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public Task<PleasanterUser?> FindByUserIdAsync(int userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(users.FirstOrDefault(user => user.UserId == userId));

    public Task<PleasanterUserProfile> LoadProfileAsync(PleasanterUser user, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PleasanterUserProfile(user, [], [], null));

    public Task<long> CountUsersAsync(CancellationToken cancellationToken = default) => Task.FromResult((long)users.Length);
}

/// <summary>メモリ上の署名鍵。</summary>
internal sealed class FakeSigningKeyStore : ISigningKeyStore
{
    private readonly List<StoredSigningKey> keys = [];

    public int Count => keys.Count;

    public Task<IReadOnlyList<SigningKey>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SigningKey>>([.. keys.Select(key => key.Meta)]);

    public Task<IReadOnlyList<StoredSigningKey>> ListWithMaterialAsync(string purpose, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoredSigningKey>>(
            [.. keys.Where(key => key.Meta.Purpose == purpose).OrderByDescending(key => key.Meta.CreatedAt)]);

    public Task<StoredSigningKey?> FindAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(keys.FirstOrDefault(key => key.Meta.KeyId == keyId));

    public Task AddActiveAsync(StoredSigningKey key, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i].Meta.Purpose == key.Meta.Purpose && keys[i].Meta.Status == SigningKey.Active)
            {
                keys[i] = keys[i] with { Meta = keys[i].Meta with { Status = SigningKey.Retired } };
            }
        }

        keys.Add(key);
        return Task.CompletedTask;
    }

    public Task<SigningKeyDeleteResult> DeleteRetiredAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(SigningKeyDeleteResult.NotFound);

    public Task<int> DeleteRetiredOlderThanAsync(DateTime threshold, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

internal static class TestUsers
{
    public static PleasanterUser Valid(
        string loginId = "taro",
        string password = "correct-horse",
        int userId = 7,
        string? secretKey = null,
        bool enableSecretKey = false) => new()
        {
            TenantId = 1,
            UserId = userId,
            LoginId = loginId,
            Name = "山田 太郎",
            PasswordHash = PleasanterPassword.Hash(password),
            SecretKey = secretKey,
            EnableSecretKey = enableSecretKey,
        };
}
