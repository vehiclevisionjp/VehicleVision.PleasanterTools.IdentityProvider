using FluentMigrator;

namespace VehicleVision.PleasanterTools.IdentityProvider.Data.Migrations;

/// <summary>IdP 自身の表。</summary>
/// <remarks>
/// <para>
/// **Pleasanter の DB には何も作らない。** この IdP が持つ状態（クライアント・署名鍵・セッション・
/// 認可コード・ログイン失敗の記録・監査ログ）は、すべてこの専用 DB に置く。
/// </para>
/// <para>
/// ⚠️ **番号は PR 間で重ならないよう、Issue を投げる時点で割り当てる**
/// （<c>_documents/ブランチ運用方針.md</c>）。FluentMigrator は同じ番号を黙って飛ばす。
/// </para>
/// </remarks>
[Migration(1, "IdP の初期スキーマ")]
public sealed class M0001_InitialSchema : Migration
{
    public override void Up()
    {
        // ---- OIDC クライアント -------------------------------------------------
        Create.Table("OidcClients")
            .WithColumn("ClientId").AsString(128).NotNullable().PrimaryKey()
            .WithColumn("DisplayName").AsString(256).NotNullable()
            .WithColumn("ClientType").AsString(16).NotNullable()
            // **ハッシュだけ持つ。** 公開クライアントは NULL
            .WithColumn("SecretHash").AsString(256).Nullable()
            .WithColumn("RedirectUrisJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("PostLogoutRedirectUrisJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("AllowedScopesJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("AllowedTenantIdsJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("AccessTokenLifetimeSeconds").AsInt32().Nullable()
            .WithColumn("RefreshTokenLifetimeSeconds").AsInt32().Nullable()
            .WithColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        // ---- SAML サービスプロバイダ -------------------------------------------
        Create.Table("SamlProviders")
            .WithColumn("ProviderId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("EntityId").AsString(512).NotNullable()
            .WithColumn("DisplayName").AsString(256).NotNullable()
            .WithColumn("AcsUrl").AsString(2048).NotNullable()
            .WithColumn("SloUrl").AsString(2048).Nullable()
            .WithColumn("SigningCertificate").AsString(int.MaxValue).Nullable()
            .WithColumn("RequireSignedRequests").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("NameIdFormat").AsString(32).NotNullable()
            .WithColumn("NameIdSource").AsString(32).NotNullable()
            .WithColumn("AttributeMappingsJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("SignAssertion").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("SignResponse").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("AllowedTenantIdsJson").AsString(int.MaxValue).NotNullable()
            .WithColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        Create.Index("UX_SamlProviders_EntityId")
            .OnTable("SamlProviders").OnColumn("EntityId").Ascending().WithOptions().Unique();

        // ---- 署名鍵 ------------------------------------------------------------
        Create.Table("SigningKeys")
            .WithColumn("KeyId").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("Purpose").AsString(16).NotNullable()
            .WithColumn("Algorithm").AsString(16).NotNullable()
            .WithColumn("Status").AsString(16).NotNullable()
            // **秘密鍵は暗号化して保存する**（SecretProtector）。平文で持たない
            .WithColumn("PrivateKeyEncrypted").AsString(int.MaxValue).NotNullable()
            .WithColumn("CertificatePem").AsString(int.MaxValue).NotNullable()
            .WithColumn("Thumbprint").AsString(64).NotNullable()
            .WithColumn("NotBefore").AsDateTime2().NotNullable()
            .WithColumn("NotAfter").AsDateTime2().NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable();

        // ---- SSO セッション ----------------------------------------------------
        Create.Table("SsoSessions")
            .WithColumn("SessionId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("TenantId").AsInt32().NotNullable()
            .WithColumn("UserId").AsInt32().NotNullable()
            .WithColumn("LoginId").AsString(256).NotNullable()
            .WithColumn("UserName").AsString(256).NotNullable()
            // pwd / otp をカンマ区切りで
            .WithColumn("AuthMethods").AsString(64).NotNullable()
            .WithColumn("AuthTime").AsDateTime2().NotNullable()
            .WithColumn("LastSeenAt").AsDateTime2().NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable()
            .WithColumn("RevokedAt").AsDateTime2().Nullable()
            .WithColumn("IpAddress").AsString(64).Nullable()
            .WithColumn("UserAgent").AsString(512).Nullable();

        Create.Index("IX_SsoSessions_UserId").OnTable("SsoSessions").OnColumn("UserId").Ascending();
        Create.Index("IX_SsoSessions_ExpiresAt").OnTable("SsoSessions").OnColumn("ExpiresAt").Ascending();

        // このセッションで、どの RP / SP へ発行したか（ログアウトの伝播に使う）
        Create.Table("SessionParticipants")
            .WithColumn("ParticipantId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("SessionId").AsGuid().NotNullable()
            .WithColumn("Kind").AsString(8).NotNullable()
            .WithColumn("ClientKey").AsString(512).NotNullable()
            .WithColumn("SessionIndex").AsString(128).Nullable()
            .WithColumn("NameId").AsString(512).Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable();

        Create.Index("IX_SessionParticipants_SessionId")
            .OnTable("SessionParticipants").OnColumn("SessionId").Ascending();

        // ---- 認可コード --------------------------------------------------------
        Create.Table("AuthorizationCodes")
            // **コードそのものは持たない。** SHA-256 のハッシュだけ
            .WithColumn("CodeHash").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("ClientId").AsString(128).NotNullable()
            .WithColumn("SessionId").AsGuid().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable()
            .WithColumn("UserId").AsInt32().NotNullable()
            .WithColumn("RedirectUri").AsString(2048).NotNullable()
            .WithColumn("Scope").AsString(512).NotNullable()
            .WithColumn("CodeChallenge").AsString(128).NotNullable()
            .WithColumn("Nonce").AsString(512).Nullable()
            .WithColumn("AuthTime").AsDateTime2().NotNullable()
            .WithColumn("AuthMethods").AsString(64).NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable()
            .WithColumn("ConsumedAt").AsDateTime2().Nullable()
            // 使い回されたとき、このコードから出たトークンを辿って失効させるための印
            .WithColumn("FamilyId").AsGuid().NotNullable();

        Create.Index("IX_AuthorizationCodes_ExpiresAt")
            .OnTable("AuthorizationCodes").OnColumn("ExpiresAt").Ascending();

        // ---- リフレッシュトークン ----------------------------------------------
        Create.Table("RefreshTokens")
            .WithColumn("TokenHash").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("FamilyId").AsGuid().NotNullable()
            .WithColumn("ClientId").AsString(128).NotNullable()
            .WithColumn("SessionId").AsGuid().NotNullable()
            .WithColumn("TenantId").AsInt32().NotNullable()
            .WithColumn("UserId").AsInt32().NotNullable()
            .WithColumn("Scope").AsString(512).NotNullable()
            .WithColumn("AuthTime").AsDateTime2().NotNullable()
            .WithColumn("AuthMethods").AsString(64).NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable()
            .WithColumn("ConsumedAt").AsDateTime2().Nullable()
            .WithColumn("RevokedAt").AsDateTime2().Nullable();

        Create.Index("IX_RefreshTokens_FamilyId").OnTable("RefreshTokens").OnColumn("FamilyId").Ascending();
        Create.Index("IX_RefreshTokens_SessionId").OnTable("RefreshTokens").OnColumn("SessionId").Ascending();
        Create.Index("IX_RefreshTokens_ExpiresAt").OnTable("RefreshTokens").OnColumn("ExpiresAt").Ascending();

        // ---- ログイン失敗・TOTP の使用済み -------------------------------------
        // **Pleasanter の DB へは書かない**ので、締め出しの状態はこちらに持つ
        Create.Table("LoginFailures")
            .WithColumn("LoginKey").AsString(256).NotNullable().PrimaryKey()
            .WithColumn("FailedCount").AsInt32().NotNullable()
            .WithColumn("LockedUntil").AsDateTime2().Nullable()
            .WithColumn("LastFailedAt").AsDateTime2().NotNullable();

        Create.Table("TotpUsedSteps")
            .WithColumn("UserKey").AsString(64).NotNullable().PrimaryKey()
            .WithColumn("LastTimeStep").AsInt64().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        // ---- ログイン途中の要求（SAML の POST バインディングなど）-------------
        Create.Table("PendingRequests")
            .WithColumn("RequestId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("Kind").AsString(16).NotNullable()
            .WithColumn("Payload").AsString(int.MaxValue).NotNullable()
            .WithColumn("ExpiresAt").AsDateTime2().NotNullable();

        // ---- 監査 --------------------------------------------------------------
        Create.Table("AuditLogs")
            .WithColumn("AuditLogId").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("OccurredAt").AsDateTime2().NotNullable()
            .WithColumn("ActorUserKey").AsString(64).Nullable()
            .WithColumn("ActorLoginId").AsString(256).Nullable()
            .WithColumn("Action").AsString(128).NotNullable()
            .WithColumn("StatusCode").AsInt32().Nullable()
            .WithColumn("TargetType").AsString(128).Nullable()
            .WithColumn("TargetId").AsString(512).Nullable()
            .WithColumn("DetailJson").AsString(int.MaxValue).Nullable()
            .WithColumn("IpAddress").AsString(64).Nullable();

        Create.Index("IX_AuditLogs_OccurredAt")
            .OnTable("AuditLogs").OnColumn("OccurredAt").Descending();
    }

    public override void Down()
    {
        Delete.Table("AuditLogs");
        Delete.Table("PendingRequests");
        Delete.Table("TotpUsedSteps");
        Delete.Table("LoginFailures");
        Delete.Table("RefreshTokens");
        Delete.Table("AuthorizationCodes");
        Delete.Table("SessionParticipants");
        Delete.Table("SsoSessions");
        Delete.Table("SigningKeys");
        Delete.Table("SamlProviders");
        Delete.Table("OidcClients");
    }
}
