using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Endpoints;

/// <summary>管理画面が呼ぶ API。仕様は <c>_documents/管理API仕様.md</c>。</summary>
/// <remarks>
/// <para>
/// **管理者は Pleasanter のユーザ。** サインインは IdP のログイン画面（TOTP を含む）で、
/// ここでは「その人が今も Pleasanter で管理者か」を毎回引き直して確かめる
/// （<see cref="AdminFilter"/>）。
/// </para>
/// <para>
/// **シークレットは返さない。** クライアントシークレットは作成・再発行の応答で 1 度だけ。
/// 署名鍵の秘密鍵・Pleasanter のパスワードハッシュ・TOTP の共有鍵は、どの応答にも出さない。
/// </para>
/// </remarks>
public static class AdminEndpoints
{
    private const string PrincipalKey = "idp.admin";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin")
            .AddEndpointFilter<AdminFilter>()
            .WithTags("管理 API");

        group.MapGet("/me", (HttpContext http) =>
        {
            var admin = Principal(http);
            return Results.Json(new
            {
                userId = admin.User.UserId,
                tenantId = admin.User.TenantId,
                loginId = admin.User.LoginId,
                name = admin.User.Name,
                role = admin.User.ServiceManager ? "ServiceManager" : "TenantManager",
                language = admin.User.Language ?? "ja",
                authMethods = admin.Session.AuthMethods,
                authTime = admin.Session.AuthTime,
            });
        });

        group.MapPost("/logout", async (HttpContext http, SsoSessionService sessions) =>
        {
            await sessions.SignOutAsync(http).ConfigureAwait(false);
            return Results.NoContent();
        });

        group.MapGet("/status", StatusAsync);
        MapOidcClients(group);
        MapSamlProviders(group);
        MapSigningKeys(group);
        MapSessions(group);
        MapAuditLogs(group);
        MapUserLookup(group);
        return app;
    }

    internal sealed record AdminPrincipal(PleasanterUser User, SsoSessionEntry Session);

    internal static AdminPrincipal Principal(HttpContext http) => (AdminPrincipal)http.Items[PrincipalKey]!;

    internal static void SetPrincipal(HttpContext http, AdminPrincipal principal) => http.Items[PrincipalKey] = principal;

    internal static IResult Problem(int status, string code, string message) =>
        Results.Json(new { error = code, message }, statusCode: status);

    // ---- 状態 ------------------------------------------------------------

    private static async Task<IResult> StatusAsync(
        HttpContext http,
        IdpOptions options,
        IDbConnectionFactory database,
        IPleasanterConnectionFactory pleasanterDatabase,
        IPleasanterUserStore users,
        DatabaseStartupState startup)
    {
        var issuer = options.Issuer;

        long? userCount = null;
        string? pleasanterError = null;
        try
        {
            userCount = await users.CountUsersAsync(http.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
        {
            // **接続文字列やサーバ名を含み得るので、例外の本文は返さない**（種類だけ）
            pleasanterError = exception.GetType().Name;
        }

        var migration = startup.MigrationStatus;
        return Results.Json(new
        {
            issuer,
            version = typeof(AdminEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0",
            endpoints = new
            {
                discovery = issuer + "/.well-known/openid-configuration",
                jwks = issuer + "/oidc/jwks",
                authorize = issuer + "/oidc/authorize",
                token = issuer + "/oidc/token",
                userinfo = issuer + "/oidc/userinfo",
                endSession = issuer + "/oidc/logout",
                revocation = issuer + "/oidc/revocation",
                samlMetadata = issuer + "/saml/metadata",
                samlSso = issuer + "/saml/sso",
                samlSlo = issuer + "/saml/slo",
            },
            database = new
            {
                provider = database.Provider.ToString(),
                appliedVersion = migration?.AppliedVersion,
                latestVersion = migration?.LatestVersion ?? 0,
                pendingCount = migration?.PendingCount ?? 0,
            },
            pleasanter = new
            {
                connected = pleasanterError is null,
                provider = pleasanterDatabase.Provider.ToString(),
                error = pleasanterError,
                userCount,
            },
            totp = new { mode = options.Totp.Mode.ToString(), countTolerances = options.Totp.CountTolerances },
            session = new
            {
                lifetimeMinutes = (int)options.SessionLifetime.TotalMinutes,
                idleMinutes = (int)options.SessionIdleTimeout.TotalMinutes,
            },
        });
    }

    // ---- OIDC クライアント -------------------------------------------------

    private static void MapOidcClients(RouteGroupBuilder group)
    {
        var clients = group.MapGroup("/oidc-clients");

        clients.MapGet("/", async (IOidcClientStore store, HttpContext http) =>
            Results.Json(await store.ListAsync(http.RequestAborted).ConfigureAwait(false)));

        clients.MapGet("/{clientId}", async (string clientId, IOidcClientStore store, HttpContext http) =>
            await store.FindAsync(clientId, http.RequestAborted).ConfigureAwait(false) is { } client
                ? Results.Json(client)
                : Problem(404, "not_found", "クライアントが見つかりません。"));

        clients.MapPost("/", async (OidcClient body, IOidcClientStore store, TimeProvider time, HttpContext http) =>
        {
            var client = Normalize(body, string.IsNullOrWhiteSpace(body.ClientId) ? "c-" + ClientSecrets.Generate()[..16] : body.ClientId);
            if (RegistrationValidator.Validate(client) is { Count: > 0 } errors)
            {
                return Problem(400, "validation", string.Join(" / ", errors));
            }

            // 公開クライアントはシークレットを持たない。機密クライアントは作成時に 1 度だけ見せる
            var secret = client.IsPublic ? null : ClientSecrets.Generate();
            var now = time.GetUtcNow().UtcDateTime;
            if (!await store.CreateAsync(client, secret is null ? null : ClientSecrets.Hash(secret), now, http.RequestAborted).ConfigureAwait(false))
            {
                return Problem(409, "conflict", "同じクライアント ID が既に登録されています。");
            }

            AuditTarget(http, "OidcClient", client.ClientId);
            var created = await store.FindAsync(client.ClientId, http.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { client = created, clientSecret = secret }, statusCode: 201);
        });

        clients.MapPut("/{clientId}", async (string clientId, OidcClient body, IOidcClientStore store, TimeProvider time, HttpContext http) =>
        {
            var existing = await store.FindAsync(clientId, http.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return Problem(404, "not_found", "クライアントが見つかりません。");
            }

            // **クライアント ID と種別は変えない。**（種別を変えると、シークレットの有無が食い違う）
            var client = Normalize(body, existing.ClientId) with { ClientType = existing.ClientType };
            if (RegistrationValidator.Validate(client) is { Count: > 0 } errors)
            {
                return Problem(400, "validation", string.Join(" / ", errors));
            }

            await store.UpdateAsync(client, time.GetUtcNow().UtcDateTime, http.RequestAborted).ConfigureAwait(false);
            AuditTarget(http, "OidcClient", clientId);
            return Results.Json(await store.FindAsync(clientId, http.RequestAborted).ConfigureAwait(false));
        });

        clients.MapPost("/{clientId}/secret", async (string clientId, IOidcClientStore store, TimeProvider time, HttpContext http) =>
        {
            var secret = ClientSecrets.Generate();
            if (!await store.SetSecretHashAsync(clientId, ClientSecrets.Hash(secret), time.GetUtcNow().UtcDateTime, http.RequestAborted).ConfigureAwait(false))
            {
                return Problem(404, "not_found", "シークレットを再発行できるクライアントが見つかりません（公開クライアントはシークレットを持ちません）。");
            }

            AuditTarget(http, "OidcClient", clientId);
            return Results.Json(new { clientSecret = secret });
        });

        clients.MapDelete("/{clientId}", async (string clientId, IOidcClientStore store, HttpContext http) =>
        {
            if (!await store.DeleteAsync(clientId, http.RequestAborted).ConfigureAwait(false))
            {
                return Problem(404, "not_found", "クライアントが見つかりません。");
            }

            AuditTarget(http, "OidcClient", clientId);
            return Results.NoContent();
        });
    }

    private static OidcClient Normalize(OidcClient body, string clientId) => body with
    {
        ClientId = clientId.Trim(),
        DisplayName = body.DisplayName?.Trim() ?? string.Empty,
        ClientType = body.ClientType?.Trim() ?? string.Empty,
        RedirectUris = Clean(body.RedirectUris),
        PostLogoutRedirectUris = Clean(body.PostLogoutRedirectUris),
        AllowedScopes = Clean(body.AllowedScopes),
        AllowedTenantIds = [.. (body.AllowedTenantIds ?? []).Distinct()],
        AccessRules = CleanRules(body.AccessRules),
    };

    private static List<AccessRule> CleanRules(IReadOnlyList<AccessRule>? rules) =>
        [.. (rules ?? []).Select(rule => new AccessRule(
            rule.Effect?.Trim().ToLowerInvariant() ?? string.Empty,
            rule.Kind?.Trim().ToLowerInvariant() ?? string.Empty,
            rule.TargetId,
            string.IsNullOrWhiteSpace(rule.Label) ? null : rule.Label.Trim())).DistinctBy(rule => (rule.Effect, rule.Kind, rule.TargetId))];

    private static List<string> Clean(IReadOnlyList<string>? values) =>
        [.. (values ?? []).Select(value => value?.Trim() ?? string.Empty).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)];

    // ---- SAML SP ----------------------------------------------------------

    private static void MapSamlProviders(RouteGroupBuilder group)
    {
        var providers = group.MapGroup("/saml-providers");

        providers.MapGet("/", async (ISamlProviderStore store, HttpContext http) =>
            Results.Json(await store.ListAsync(http.RequestAborted).ConfigureAwait(false)));

        providers.MapGet("/{providerId}", async (string providerId, ISamlProviderStore store, HttpContext http) =>
            await store.FindAsync(providerId, http.RequestAborted).ConfigureAwait(false) is { } provider
                ? Results.Json(provider)
                : Problem(404, "not_found", "サービスプロバイダが見つかりません。"));

        providers.MapPost("/", async (SamlProvider body, ISamlProviderStore store, TimeProvider time, HttpContext http) =>
        {
            var provider = NormalizeProvider(body, Guid.NewGuid().ToString());
            if (RegistrationValidator.Validate(provider) is { Count: > 0 } errors)
            {
                return Problem(400, "validation", string.Join(" / ", errors));
            }

            if (!await store.CreateAsync(provider, time.GetUtcNow().UtcDateTime, http.RequestAborted).ConfigureAwait(false))
            {
                return Problem(409, "conflict", "同じエンティティ ID が既に登録されています。");
            }

            AuditTarget(http, "SamlProvider", provider.EntityId);
            return Results.Json(await store.FindAsync(provider.ProviderId, http.RequestAborted).ConfigureAwait(false), statusCode: 201);
        });

        providers.MapPut("/{providerId}", async (string providerId, SamlProvider body, ISamlProviderStore store, TimeProvider time, HttpContext http) =>
        {
            if (await store.FindAsync(providerId, http.RequestAborted).ConfigureAwait(false) is not { } existing)
            {
                return Problem(404, "not_found", "サービスプロバイダが見つかりません。");
            }

            var provider = NormalizeProvider(body, existing.ProviderId);
            if (RegistrationValidator.Validate(provider) is { Count: > 0 } errors)
            {
                return Problem(400, "validation", string.Join(" / ", errors));
            }

            try
            {
                await store.UpdateAsync(provider, time.GetUtcNow().UtcDateTime, http.RequestAborted).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return Problem(409, "conflict", "同じエンティティ ID が既に登録されています。");
            }

            AuditTarget(http, "SamlProvider", provider.EntityId);
            return Results.Json(await store.FindAsync(providerId, http.RequestAborted).ConfigureAwait(false));
        });

        providers.MapDelete("/{providerId}", async (string providerId, ISamlProviderStore store, HttpContext http) =>
        {
            var existing = await store.FindAsync(providerId, http.RequestAborted).ConfigureAwait(false);
            if (existing is null || !await store.DeleteAsync(providerId, http.RequestAborted).ConfigureAwait(false))
            {
                return Problem(404, "not_found", "サービスプロバイダが見つかりません。");
            }

            AuditTarget(http, "SamlProvider", existing.EntityId);
            return Results.NoContent();
        });

        providers.MapPost("/import-metadata", (ImportMetadataRequest body) => ImportMetadata(body.Xml));
    }

    internal sealed record ImportMetadataRequest(string? Xml);

    private static SamlProvider NormalizeProvider(SamlProvider body, string providerId) => body with
    {
        ProviderId = providerId,
        EntityId = body.EntityId?.Trim() ?? string.Empty,
        DisplayName = body.DisplayName?.Trim() ?? string.Empty,
        AcsUrl = body.AcsUrl?.Trim() ?? string.Empty,
        SloUrl = string.IsNullOrWhiteSpace(body.SloUrl) ? null : body.SloUrl.Trim(),
        SigningCertificate = string.IsNullOrWhiteSpace(body.SigningCertificate) ? null : body.SigningCertificate.Trim(),
        AttributeMappings = [.. (body.AttributeMappings ?? []).Select(m => new SamlAttributeMapping(m.AttributeName?.Trim() ?? string.Empty, m.Source?.Trim() ?? string.Empty))],
        AllowedTenantIds = [.. (body.AllowedTenantIds ?? []).Distinct()],
        AccessRules = CleanRules(body.AccessRules),
    };

    /// <summary>SP のメタデータ XML から、登録の下書きを作る（保存はしない）。</summary>
    internal static IResult ImportMetadata(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 1_048_576)
        {
            return Problem(400, "validation", "メタデータ XML を貼り付けてください（1 MB まで）。");
        }

        try
        {
            // **DTD を禁止して先に読む**（XXE・billion laughs の対策）。通った文字列だけをライブラリへ渡す
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_048_576,
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
            {
                document.Load(reader);
            }

            var descriptor = new EntityDescriptor().ReadSPSsoDescriptor(document.OuterXml);
            var sp = descriptor.SPSsoDescriptor ?? throw new InvalidOperationException("SPSSODescriptor がありません。");

            var acs = sp.AssertionConsumerServices?
                .OrderByDescending(service => service.Binding == ProtocolBindings.HttpPost)
                .ThenByDescending(service => service.IsDefault)
                .FirstOrDefault();
            var slo = sp.SingleLogoutServices?
                .OrderByDescending(service => service.Binding == ProtocolBindings.HttpRedirect)
                .FirstOrDefault();
            var certificate = sp.SigningCertificates?.FirstOrDefault();

            return Results.Json(new SamlProvider
            {
                EntityId = descriptor.EntityId ?? string.Empty,
                DisplayName = descriptor.Organization?.OrganizationDisplayNames?.FirstOrDefault()?.Name
                    ?? descriptor.EntityId ?? string.Empty,
                AcsUrl = acs?.Location?.OriginalString ?? string.Empty,
                SloUrl = slo?.Location?.OriginalString,
                SigningCertificate = certificate?.ExportCertificatePem(),
                RequireSignedRequests = sp.AuthnRequestsSigned == true && certificate is not null,
                AttributeMappings =
                [
                    new SamlAttributeMapping("email", "email"),
                    new SamlAttributeMapping("name", "name"),
                ],
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // **利用者が貼ったドキュメントなので、ライブラリがどの例外で落ちても 400 にする**
            // （読めない理由は、貼った人に分かる範囲の文言だけを返す）
            return Problem(400, "validation", "メタデータを読み取れません。SP のメタデータ（EntityDescriptor）を貼り付けてください。");
        }
    }

    // ---- 署名鍵 -----------------------------------------------------------

    private static void MapSigningKeys(RouteGroupBuilder group)
    {
        var keys = group.MapGroup("/signing-keys");

        keys.MapGet("/", async (ISigningKeyStore store, HttpContext http) =>
            Results.Json(await store.ListAsync(http.RequestAborted).ConfigureAwait(false)));

        keys.MapPost("/rotate", async (RotateRequest body, SigningKeyService service, HttpContext http) =>
        {
            if (body.Purpose is not (SigningKey.Oidc or SigningKey.Saml))
            {
                return Problem(400, "validation", "purpose は oidc か saml。");
            }

            var key = await service.RotateAsync(body.Purpose, http.RequestAborted).ConfigureAwait(false);
            AuditTarget(http, "SigningKey", key.KeyId);
            return Results.Json(key, statusCode: 201);
        });

        keys.MapGet("/{keyId}/certificate", async (string keyId, ISigningKeyStore store, HttpContext http) =>
            await store.FindAsync(keyId, http.RequestAborted).ConfigureAwait(false) is { } key
                ? Results.Text(key.CertificatePem, "text/plain; charset=utf-8")
                : Problem(404, "not_found", "署名鍵が見つかりません。"));

        keys.MapDelete("/{keyId}", async (string keyId, ISigningKeyStore store, HttpContext http) =>
        {
            switch (await store.DeleteRetiredAsync(keyId, http.RequestAborted).ConfigureAwait(false))
            {
                case SigningKeyDeleteResult.Deleted:
                    AuditTarget(http, "SigningKey", keyId);
                    return Results.NoContent();
                case SigningKeyDeleteResult.StillActive:
                    return Problem(409, "conflict", "有効な鍵は削除できません。先にローテーションしてください。");
                default:
                    return Problem(404, "not_found", "署名鍵が見つかりません。");
            }
        });
    }

    internal sealed record RotateRequest(string? Purpose);

    // ---- セッション -------------------------------------------------------

    private static void MapSessions(RouteGroupBuilder group)
    {
        var sessions = group.MapGroup("/sessions");

        sessions.MapGet("/", async (
            string? loginId,
            int? limit,
            int? offset,
            ISsoSessionStore store,
            IdpOptions options,
            TimeProvider time,
            HttpContext http) =>
        {
            var current = Principal(http).Session.SessionId;
            var rows = await store.ListActiveAsync(
                loginId,
                time.GetUtcNow().UtcDateTime,
                options.SessionIdleTimeout,
                Math.Clamp(limit ?? 50, 1, 200),
                Math.Max(offset ?? 0, 0),
                http.RequestAborted).ConfigureAwait(false);

            return Results.Json(rows.Select(row => new SsoSession(
                row.SessionId.ToString(), row.TenantId, row.UserId, row.LoginId, row.UserName,
                row.AuthTime, row.LastSeenAt, row.ExpiresAt, row.IpAddress, row.UserAgent,
                row.AuthMethods, row.SessionId == current)));
        });

        sessions.MapDelete("/{sessionId}", async (string sessionId, SsoSessionService service, HttpContext http) =>
        {
            if (!Guid.TryParse(sessionId, out var id))
            {
                return Problem(404, "not_found", "セッションが見つかりません。");
            }

            await service.RevokeAsync(id, http.RequestAborted).ConfigureAwait(false);
            AuditTarget(http, "SsoSession", sessionId);
            return Results.NoContent();
        });
    }

    // ---- 監査ログ ---------------------------------------------------------

    private static void MapAuditLogs(RouteGroupBuilder group)
    {
        group.MapGet("/audit-logs", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? actor,
            bool? failedOnly,
            string? action,
            int? limit,
            int? offset,
            IAuditLogStore store,
            HttpContext http) =>
        {
            var rows = await store.ListAsync(new AuditLogQuery
            {
                From = from?.UtcDateTime,
                To = to?.UtcDateTime,
                Actor = actor,
                FailedOnly = failedOnly ?? false,
                ActionContains = action,
                Limit = Math.Clamp(limit ?? 100, 1, 200),
                Offset = Math.Max(offset ?? 0, 0),
            }, http.RequestAborted).ConfigureAwait(false);

            return Results.Json(rows.Select(row => new
            {
                row.OccurredAt,
                row.ActorUserKey,
                row.ActorLoginId,
                row.Action,
                row.StatusCode,
                row.TargetType,
                row.TargetId,
                detail = ParseDetail(row.DetailJson),
                row.IpAddress,
            }));
        });
    }

    private static JsonElement? ParseDetail(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- ユーザ照会 -------------------------------------------------------

    private static void MapUserLookup(RouteGroupBuilder group)
    {
        // アクセスのルールの対象（ユーザー・組織・グループ）を、名前で探す。**管理者自身のテナントだけ**
        group.MapGet("/pleasanter/directory", async (
            string? kind,
            string? q,
            IPleasanterUserStore users,
            HttpContext http) =>
        {
            if (kind is not (AccessRules.User or AccessRules.Dept or AccessRules.Group))
            {
                return Problem(400, "validation", "kind は user・dept・group のどれかです。");
            }

            var entries = await users.SearchDirectoryAsync(
                kind, Principal(http).User.TenantId, q ?? string.Empty, 20, http.RequestAborted).ConfigureAwait(false);
            return Results.Json(entries.Select(entry => new { id = entry.Id, name = entry.Name, detail = entry.Detail }));
        });

        group.MapGet("/pleasanter/users/lookup", async (
            string? loginId,
            IPleasanterUserStore users,
            IdpOptions options,
            HttpContext http) =>
        {
            var user = string.IsNullOrWhiteSpace(loginId)
                ? null
                : await users.FindByLoginIdAsync(loginId, http.RequestAborted).ConfigureAwait(false);
            if (user is null)
            {
                return Results.Json(new { found = false, disabled = false, lockout = false, tenantManager = false, serviceManager = false,
                    hasTotp = false, totpRequired = false, mailAddresses = Array.Empty<string>(), groups = Array.Empty<string>() });
            }

            var profile = await users.LoadProfileAsync(user, http.RequestAborted).ConfigureAwait(false);
            var requirement = options.Totp.Evaluate(user);

            // **共有鍵・パスワードのハッシュは返さない。** 「求められるか」だけ
            return Results.Json(new
            {
                found = true,
                tenantId = user.TenantId,
                userId = user.UserId,
                loginId = user.LoginId,
                name = user.Name,
                disabled = user.Disabled,
                lockout = user.Lockout,
                tenantManager = user.TenantManager,
                serviceManager = user.ServiceManager,
                hasTotp = user.HasTotp,
                totpRequired = requirement is not TotpRequirement.NotRequired,
                mailAddresses = profile.MailAddresses,
                groups = profile.Groups,
                dept = profile.DeptName,
            });
        });
    }

    /// <summary>監査ログへ残す対象を、入口が明示する。</summary>
    internal static void AuditTarget(HttpContext http, string type, string id)
    {
        http.Items["idp.audit.targetType"] = type;
        http.Items["idp.audit.targetId"] = id;
    }
}

/// <summary>管理 API の入口に掛ける検査（認証・権限・CSRF）と監査の記録。</summary>
/// <remarks>
/// <para>**入口の集まりに 1 回掛ける。** 1 つ 1 つの入口へ書き足すと、後から足した入口だけが抜ける。</para>
/// <para>
/// **更新系は <c>X-IdP-Admin: 1</c> を要求する。** カスタムヘッダは CORS のプリフライトが要るので、
/// 別サイトのフォームやスクリプトからは付けられない。加えて <c>Origin</c> が IdP のものと一致すること。
/// </para>
/// </remarks>
public sealed class AdminFilter(
    SsoSessionService sessions,
    IPleasanterUserStore users,
    IdpOptions options,
    AuditWriter audit) : IEndpointFilter
{
    public const string CsrfHeader = "X-IdP-Admin";

    private readonly string issuerOrigin = new Uri(options.Issuer).GetLeftPart(UriPartial.Authority);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var session = await sessions.GetCurrentAsync(http).ConfigureAwait(false);
        if (session is null)
        {
            return AdminEndpoints.Problem(401, "unauthorized", "サインインが必要です。");
        }

        var user = await users.FindByUserIdAsync(session.UserId, http.RequestAborted).ConfigureAwait(false);
        if (user is null || user.TenantId != session.TenantId || LoginGate.Check(user, DateTime.Now) is not LoginDenial.None)
        {
            await sessions.SignOutAsync(http).ConfigureAwait(false);
            return AdminEndpoints.Problem(401, "unauthorized", "サインインし直してください。");
        }

        if (!IsAdmin(user) || !await AccessCheck.AllowsAsync(users, user, options.AdminAccessRules, http.RequestAborted).ConfigureAwait(false))
        {
            return AdminEndpoints.Problem(403, "forbidden", "管理者ではありません。");
        }

        var isRead = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        if (!isRead && !PassesCsrf(http))
        {
            return AdminEndpoints.Problem(403, "forbidden", "要求を確認できませんでした。画面を読み込み直してください。");
        }

        AdminEndpoints.SetPrincipal(http, new AdminEndpoints.AdminPrincipal(user, session));
        var result = await next(context).ConfigureAwait(false);

        // **読み取りは残さない。** 変えた操作だけが埋もれずに残る
        if (!isRead)
        {
            var status = result is IStatusCodeHttpResult { StatusCode: { } code } ? code : StatusCodes.Status200OK;
            await audit.WriteAsync(
                http,
                $"{http.Request.Method} {Readable(http)}",
                status,
                user.UserKey,
                user.LoginId,
                http.Items["idp.audit.targetType"] as string,
                http.Items["idp.audit.targetId"] as string).ConfigureAwait(false);
        }

        return result;
    }

    private bool IsAdmin(PleasanterUser user)
    {
        var allowed = options.AdminRole switch
        {
            AdminRole.TenantManager => user.TenantManager || user.ServiceManager,
            _ => user.ServiceManager,
        };

        return allowed
            && (options.AdminLoginIds.Count == 0
                || options.AdminLoginIds.Contains(user.LoginId.ToLowerInvariant(), StringComparer.Ordinal));
    }

    private bool PassesCsrf(HttpContext http)
    {
        if (http.Request.Headers[CsrfHeader] != "1")
        {
            return false;
        }

        var origin = http.Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin)
            || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                && string.Equals(uri.GetLeftPart(UriPartial.Authority), issuerOrigin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>実際の URL ではなく経路の型を残す（識別子は対象の列に入る）。</summary>
    private static string Readable(HttpContext http) =>
        (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? "/";
}
