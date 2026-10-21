using System.Text.Json;
using Dapper;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>OIDC クライアントの登録を読み書きする。</summary>
public interface IOidcClientStore
{
    Task<IReadOnlyList<OidcClient>> ListAsync(CancellationToken cancellationToken = default);

    Task<OidcClient?> FindAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>登録する。<paramref name="secretHash"/> は公開クライアントでは <c>null</c>。</summary>
    /// <returns>同じクライアント ID が既にあれば <c>false</c>。</returns>
    Task<bool> CreateAsync(
        OidcClient client,
        string? secretHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <returns>見つからなければ <c>false</c>。クライアント ID と種別は変えない。</returns>
    Task<bool> UpdateAsync(
        OidcClient client,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<bool> SetSecretHashAsync(
        string clientId,
        string secretHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    Task<string?> GetSecretHashAsync(string clientId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string clientId, CancellationToken cancellationToken = default);
}

public sealed class OidcClientStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), IOidcClientStore
{
    private const string Columns =
        "[ClientId], [DisplayName], [ClientType], [SecretHash], [RedirectUrisJson], "
        + "[PostLogoutRedirectUrisJson], [AllowedScopesJson], [AllowedTenantIdsJson], [AccessRulesJson], "
        + "[AccessTokenLifetimeSeconds], [RefreshTokenLifetimeSeconds], [Enabled], "
        + "[CreatedAt], [UpdatedAt]";

    public async Task<IReadOnlyList<OidcClient>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(Sql(
            "SELECT " + Columns + " FROM [OidcClients] ORDER BY [DisplayName], [ClientId]",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(ToModel)];
    }

    public async Task<OidcClient?> FindAsync(string clientId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT " + Columns + " FROM [OidcClients] WHERE [ClientId] = @ClientId",
            new { ClientId = clientId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToModel(row);
    }

    public async Task<bool> CreateAsync(
        OidcClient client,
        string? secretHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [OidcClients] (" + Columns + ") VALUES (@ClientId, @DisplayName, @ClientType, "
                + "@SecretHash, @RedirectUrisJson, @PostLogoutRedirectUrisJson, @AllowedScopesJson, "
                + "@AllowedTenantIdsJson, @AccessRulesJson, @AccessTokenLifetimeSeconds, @RefreshTokenLifetimeSeconds, "
                + "@Enabled, @CreatedAt, @UpdatedAt)",
                new
                {
                    client.ClientId,
                    client.DisplayName,
                    client.ClientType,
                    SecretHash = secretHash,
                    RedirectUrisJson = Json(client.RedirectUris),
                    PostLogoutRedirectUrisJson = Json(client.PostLogoutRedirectUris),
                    AllowedScopesJson = Json(client.AllowedScopes),
                    AllowedTenantIdsJson = Json(client.AllowedTenantIds),
                    AccessRulesJson = Json(client.AccessRules),
                    client.AccessTokenLifetimeSeconds,
                    client.RefreshTokenLifetimeSeconds,
                    client.Enabled,
                    CreatedAt = Db(nowUtc),
                    UpdatedAt = Db(nowUtc),
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (System.Data.Common.DbException exception) when (IsDuplicateKey(exception))
        {
            return false;
        }
    }

    public async Task<bool> UpdateAsync(
        OidcClient client,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [OidcClients] SET [DisplayName] = @DisplayName, "
            + "[RedirectUrisJson] = @RedirectUrisJson, "
            + "[PostLogoutRedirectUrisJson] = @PostLogoutRedirectUrisJson, "
            + "[AllowedScopesJson] = @AllowedScopesJson, [AllowedTenantIdsJson] = @AllowedTenantIdsJson, [AccessRulesJson] = @AccessRulesJson, "
            + "[AccessTokenLifetimeSeconds] = @AccessTokenLifetimeSeconds, "
            + "[RefreshTokenLifetimeSeconds] = @RefreshTokenLifetimeSeconds, "
            + "[Enabled] = @Enabled, [UpdatedAt] = @UpdatedAt WHERE [ClientId] = @ClientId",
            new
            {
                client.ClientId,
                client.DisplayName,
                RedirectUrisJson = Json(client.RedirectUris),
                PostLogoutRedirectUrisJson = Json(client.PostLogoutRedirectUris),
                AllowedScopesJson = Json(client.AllowedScopes),
                AllowedTenantIdsJson = Json(client.AllowedTenantIds),
                AccessRulesJson = Json(client.AccessRules),
                client.AccessTokenLifetimeSeconds,
                client.RefreshTokenLifetimeSeconds,
                client.Enabled,
                UpdatedAt = Db(nowUtc),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<bool> SetSecretHashAsync(
        string clientId,
        string secretHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(Sql(
            "UPDATE [OidcClients] SET [SecretHash] = @SecretHash, [UpdatedAt] = @UpdatedAt "
            + "WHERE [ClientId] = @ClientId AND [ClientType] = @ClientType",
            new { ClientId = clientId, SecretHash = secretHash, UpdatedAt = Db(nowUtc), ClientType = OidcClient.Confidential },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<string?> GetSecretHashAsync(string clientId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<string?>(Sql(
            "SELECT [SecretHash] FROM [OidcClients] WHERE [ClientId] = @ClientId",
            new { ClientId = clientId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string clientId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(Sql(
            "DELETE FROM [OidcClients] WHERE [ClientId] = @ClientId",
            new { ClientId = clientId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected == 1;
    }

    internal static string Json<T>(IReadOnlyList<T> values) => JsonSerializer.Serialize(values);

    internal static IReadOnlyList<T> FromJson<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<T>>(json) ?? [];

    private static OidcClient ToModel(Row row) => new()
    {
        ClientId = row.ClientId,
        DisplayName = row.DisplayName,
        ClientType = row.ClientType,
        RedirectUris = FromJson<string>(row.RedirectUrisJson),
        PostLogoutRedirectUris = FromJson<string>(row.PostLogoutRedirectUrisJson),
        AllowedScopes = FromJson<string>(row.AllowedScopesJson),
        AllowedTenantIds = FromJson<int>(row.AllowedTenantIdsJson),
        AccessRules = FromJson<AccessRule>(row.AccessRulesJson),
        AccessTokenLifetimeSeconds = row.AccessTokenLifetimeSeconds,
        RefreshTokenLifetimeSeconds = row.RefreshTokenLifetimeSeconds,
        Enabled = row.Enabled,
        HasSecret = !string.IsNullOrEmpty(row.SecretHash),
        CreatedAt = DbTime.AsUtc(row.CreatedAt),
        UpdatedAt = DbTime.AsUtc(row.UpdatedAt),
    };

    private sealed class Row
    {
        public string ClientId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string ClientType { get; set; } = string.Empty;

        public string? SecretHash { get; set; }

        public string RedirectUrisJson { get; set; } = "[]";

        public string PostLogoutRedirectUrisJson { get; set; } = "[]";

        public string AllowedScopesJson { get; set; } = "[]";

        public string AllowedTenantIdsJson { get; set; } = "[]";


        public string? AccessRulesJson { get; set; }

        public int? AccessTokenLifetimeSeconds { get; set; }

        public int? RefreshTokenLifetimeSeconds { get; set; }

        public bool Enabled { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}

/// <summary>SAML サービスプロバイダの登録を読み書きする。</summary>
public interface ISamlProviderStore
{
    Task<IReadOnlyList<SamlProvider>> ListAsync(CancellationToken cancellationToken = default);

    Task<SamlProvider?> FindAsync(string providerId, CancellationToken cancellationToken = default);

    Task<SamlProvider?> FindByEntityIdAsync(string entityId, CancellationToken cancellationToken = default);

    /// <returns>同じエンティティ ID が既にあれば <c>false</c>。</returns>
    Task<bool> CreateAsync(SamlProvider provider, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <returns>見つからなければ <c>false</c>。エンティティ ID の重複は <see cref="InvalidOperationException"/>。</returns>
    Task<bool> UpdateAsync(SamlProvider provider, DateTime nowUtc, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

public sealed class SamlProviderStore(IDbConnectionFactory connectionFactory)
    : DapperStore(connectionFactory), ISamlProviderStore
{
    private const string Columns =
        "[ProviderId], [EntityId], [DisplayName], [AcsUrl], [SloUrl], [SigningCertificate], "
        + "[RequireSignedRequests], [NameIdFormat], [NameIdSource], [AttributeMappingsJson], "
        + "[SignAssertion], [SignResponse], [AllowedTenantIdsJson], [AccessRulesJson], [Enabled], [CreatedAt], [UpdatedAt]";

    public async Task<IReadOnlyList<SamlProvider>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(Sql(
            "SELECT " + Columns + " FROM [SamlProviders] ORDER BY [DisplayName], [EntityId]",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(ToModel)];
    }

    public async Task<SamlProvider?> FindAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(providerId, out var id))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT " + Columns + " FROM [SamlProviders] WHERE [ProviderId] = @ProviderId",
            new { ProviderId = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToModel(row);
    }

    public async Task<SamlProvider?> FindByEntityIdAsync(
        string entityId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QueryFirstOrDefaultAsync<Row>(Sql(
            "SELECT " + Columns + " FROM [SamlProviders] WHERE [EntityId] = @EntityId",
            new { EntityId = entityId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToModel(row);
    }

    public async Task<bool> CreateAsync(
        SamlProvider provider,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(Sql(
                "INSERT INTO [SamlProviders] (" + Columns + ") VALUES (@ProviderId, @EntityId, @DisplayName, "
                + "@AcsUrl, @SloUrl, @SigningCertificate, @RequireSignedRequests, @NameIdFormat, "
                + "@NameIdSource, @AttributeMappingsJson, @SignAssertion, @SignResponse, "
                + "@AllowedTenantIdsJson, @AccessRulesJson, @Enabled, @CreatedAt, @UpdatedAt)",
                Parameters(provider, Guid.Parse(provider.ProviderId), nowUtc, nowUtc),
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
        catch (System.Data.Common.DbException exception) when (IsDuplicateKey(exception))
        {
            return false;
        }
    }

    public async Task<bool> UpdateAsync(
        SamlProvider provider,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var affected = await connection.ExecuteAsync(Sql(
                "UPDATE [SamlProviders] SET [EntityId] = @EntityId, [DisplayName] = @DisplayName, "
                + "[AcsUrl] = @AcsUrl, [SloUrl] = @SloUrl, [SigningCertificate] = @SigningCertificate, "
                + "[RequireSignedRequests] = @RequireSignedRequests, [NameIdFormat] = @NameIdFormat, "
                + "[NameIdSource] = @NameIdSource, [AttributeMappingsJson] = @AttributeMappingsJson, "
                + "[SignAssertion] = @SignAssertion, [SignResponse] = @SignResponse, "
                + "[AllowedTenantIdsJson] = @AllowedTenantIdsJson, [AccessRulesJson] = @AccessRulesJson, [Enabled] = @Enabled, "
                + "[UpdatedAt] = @UpdatedAt WHERE [ProviderId] = @ProviderId",
                Parameters(provider, Guid.Parse(provider.ProviderId), nowUtc, nowUtc),
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return affected == 1;
        }
        catch (System.Data.Common.DbException exception) when (IsDuplicateKey(exception))
        {
            throw new InvalidOperationException("同じエンティティ ID が既に登録されている", exception);
        }
    }

    public async Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(providerId, out var id))
        {
            return false;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(Sql(
            "DELETE FROM [SamlProviders] WHERE [ProviderId] = @ProviderId",
            new { ProviderId = id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected == 1;
    }

    private static object Parameters(SamlProvider provider, Guid id, DateTime created, DateTime updated) => new
    {
        ProviderId = id,
        provider.EntityId,
        provider.DisplayName,
        provider.AcsUrl,
        provider.SloUrl,
        provider.SigningCertificate,
        provider.RequireSignedRequests,
        provider.NameIdFormat,
        provider.NameIdSource,
        AttributeMappingsJson = JsonSerializer.Serialize(provider.AttributeMappings),
        provider.SignAssertion,
        provider.SignResponse,
        AllowedTenantIdsJson = OidcClientStore.Json(provider.AllowedTenantIds),
        AccessRulesJson = OidcClientStore.Json(provider.AccessRules),
        provider.Enabled,
        CreatedAt = DbTime.ForDb(created),
        UpdatedAt = DbTime.ForDb(updated),
    };

    private static SamlProvider ToModel(Row row) => new()
    {
        ProviderId = row.ProviderId.ToString(),
        EntityId = row.EntityId,
        DisplayName = row.DisplayName,
        AcsUrl = row.AcsUrl,
        SloUrl = row.SloUrl,
        SigningCertificate = row.SigningCertificate,
        RequireSignedRequests = row.RequireSignedRequests,
        NameIdFormat = row.NameIdFormat,
        NameIdSource = row.NameIdSource,
        AttributeMappings = OidcClientStore.FromJson<SamlAttributeMapping>(row.AttributeMappingsJson),
        SignAssertion = row.SignAssertion,
        SignResponse = row.SignResponse,
        AllowedTenantIds = OidcClientStore.FromJson<int>(row.AllowedTenantIdsJson),
        AccessRules = OidcClientStore.FromJson<AccessRule>(row.AccessRulesJson),
        Enabled = row.Enabled,
        CreatedAt = DbTime.AsUtc(row.CreatedAt),
        UpdatedAt = DbTime.AsUtc(row.UpdatedAt),
    };

    private sealed class Row
    {
        public Guid ProviderId { get; set; }

        public string EntityId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string AcsUrl { get; set; } = string.Empty;

        public string? SloUrl { get; set; }

        public string? SigningCertificate { get; set; }

        public bool RequireSignedRequests { get; set; }

        public string NameIdFormat { get; set; } = string.Empty;

        public string NameIdSource { get; set; } = string.Empty;

        public string AttributeMappingsJson { get; set; } = "[]";

        public bool SignAssertion { get; set; }

        public bool SignResponse { get; set; }

        public string AllowedTenantIdsJson { get; set; } = "[]";


        public string? AccessRulesJson { get; set; }

        public bool Enabled { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}
