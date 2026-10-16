using Dapper;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>保存された署名鍵（秘密鍵は暗号化されたまま）。</summary>
public sealed record StoredSigningKey(SigningKey Meta, string PrivateKeyEncrypted, string CertificatePem);

/// <summary>署名鍵の入れ物。</summary>
public interface ISigningKeyStore
{
    /// <summary>メタ情報だけ（新しい順）。</summary>
    Task<IReadOnlyList<SigningKey>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>秘密鍵と証明書を含む。署名と JWKS / メタデータの公開に使う。</summary>
    Task<IReadOnlyList<StoredSigningKey>> ListWithMaterialAsync(
        string purpose,
        CancellationToken cancellationToken = default);

    Task<StoredSigningKey?> FindAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>新しい鍵を active として入れ、同じ用途の既存の active を retired へ落とす。</summary>
    Task AddActiveAsync(StoredSigningKey key, CancellationToken cancellationToken = default);

    /// <returns>retired を消せたら <c>Deleted</c>。active は <c>StillActive</c>。</returns>
    Task<SigningKeyDeleteResult> DeleteRetiredAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>退役から日数が経った鍵を消す。</summary>
    Task<int> DeleteRetiredOlderThanAsync(DateTime threshold, CancellationToken cancellationToken = default);
}

public enum SigningKeyDeleteResult
{
    Deleted,
    NotFound,
    StillActive,
}

public sealed class SigningKeyStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), ISigningKeyStore
{
    private const string MetaColumns =
        "[KeyId], [Purpose], [Algorithm], [Status], [Thumbprint], [NotBefore], [NotAfter], [CreatedAt]";

    public async Task<IReadOnlyList<SigningKey>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<MetaRow>(Sql(
            "SELECT " + MetaColumns + " FROM [SigningKeys] ORDER BY [CreatedAt] DESC, [KeyId]",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(ToMeta)];
    }

    public async Task<IReadOnlyList<StoredSigningKey>> ListWithMaterialAsync(
        string purpose,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<FullRow>(Sql(
            "SELECT " + MetaColumns + ", [PrivateKeyEncrypted], [CertificatePem] FROM [SigningKeys] "
            + "WHERE [Purpose] = @Purpose ORDER BY [CreatedAt] DESC, [KeyId]",
            new { Purpose = purpose },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(ToStored)];
    }

    public async Task<StoredSigningKey?> FindAsync(string keyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<FullRow>(Sql(
            "SELECT " + MetaColumns + ", [PrivateKeyEncrypted], [CertificatePem] FROM [SigningKeys] "
            + "WHERE [KeyId] = @KeyId",
            new { KeyId = keyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToStored(row);
    }

    public async Task AddActiveAsync(StoredSigningKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "UPDATE [SigningKeys] SET [Status] = @Retired WHERE [Purpose] = @Purpose AND [Status] = @Active",
            new { Retired = SigningKey.Retired, key.Meta.Purpose, Active = SigningKey.Active },
            transaction,
            cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(Sql(
            "INSERT INTO [SigningKeys] (" + MetaColumns + ", [PrivateKeyEncrypted], [CertificatePem]) "
            + "VALUES (@KeyId, @Purpose, @Algorithm, @Status, @Thumbprint, @NotBefore, @NotAfter, "
            + "@CreatedAt, @PrivateKeyEncrypted, @CertificatePem)",
            new
            {
                key.Meta.KeyId,
                key.Meta.Purpose,
                key.Meta.Algorithm,
                Status = SigningKey.Active,
                key.Meta.Thumbprint,
                NotBefore = Db(key.Meta.NotBefore),
                NotAfter = Db(key.Meta.NotAfter),
                CreatedAt = Db(key.Meta.CreatedAt),
                key.PrivateKeyEncrypted,
                key.CertificatePem,
            },
            transaction,
            cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SigningKeyDeleteResult> DeleteRetiredAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await connection.ExecuteAsync(Sql(
            "DELETE FROM [SigningKeys] WHERE [KeyId] = @KeyId AND [Status] = @Retired",
            new { KeyId = keyId, Retired = SigningKey.Retired },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (deleted == 1)
        {
            return SigningKeyDeleteResult.Deleted;
        }

        var exists = await connection.ExecuteScalarAsync<int>(Sql(
            "SELECT COUNT(*) FROM [SigningKeys] WHERE [KeyId] = @KeyId",
            new { KeyId = keyId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return exists > 0 ? SigningKeyDeleteResult.StillActive : SigningKeyDeleteResult.NotFound;
    }

    public async Task<int> DeleteRetiredOlderThanAsync(
        DateTime threshold,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Sql(
            "DELETE FROM [SigningKeys] WHERE [Status] = @Retired AND [NotAfter] < @Threshold",
            new { Retired = SigningKey.Retired, Threshold = Db(threshold) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static SigningKey ToMeta(MetaRow row) => new(
        row.KeyId,
        row.Purpose,
        row.Algorithm,
        row.Status,
        row.Thumbprint,
        DbTime.AsUtc(row.NotBefore),
        DbTime.AsUtc(row.NotAfter),
        DbTime.AsUtc(row.CreatedAt));

    private static StoredSigningKey ToStored(FullRow row) =>
        new(ToMeta(row), row.PrivateKeyEncrypted, row.CertificatePem);

    private class MetaRow
    {
        public string KeyId { get; set; } = string.Empty;

        public string Purpose { get; set; } = string.Empty;

        public string Algorithm { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string Thumbprint { get; set; } = string.Empty;

        public DateTime NotBefore { get; set; }

        public DateTime NotAfter { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    private sealed class FullRow : MetaRow
    {
        public string PrivateKeyEncrypted { get; set; } = string.Empty;

        public string CertificatePem { get; set; } = string.Empty;
    }
}
