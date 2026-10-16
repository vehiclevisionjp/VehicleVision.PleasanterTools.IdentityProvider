using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Pages;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Endpoints;

/// <summary>OpenID Connect（Authorization Code Flow + PKCE）。</summary>
/// <remarks>
/// <para>
/// 対応するもの: discovery、JWKS、認可、トークン（<c>authorization_code</c> / <c>refresh_token</c>）、
/// UserInfo、RP-Initiated Logout、トークンの失効（RFC 7009）。
/// 対応しないもの: implicit / hybrid フロー、<c>response_mode=form_post</c>、
/// Back-Channel / Front-Channel Logout、動的クライアント登録（クライアントは管理画面で登録する）。
/// </para>
/// <para>
/// **セキュリティ上の決め事。**
/// リダイレクト URI は完全一致。PKCE は <c>S256</c> のみで、公開クライアントでは必須。
/// 認可コードは 1 度きり（再利用を検知したら、そのコードから出たトークンを全て失効させる）。
/// リフレッシュトークンは使うたびに替える（再利用の検知も同じ）。
/// 応答には <c>iss</c> を付ける（RFC 9207、認可応答のすり替えへの対策）。
/// </para>
/// </remarks>
public static class OidcEndpoints
{
    public const string TokenRateLimitPolicy = "token";

    public static IEndpointRouteBuilder MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", Discovery).ExcludeFromDescription();
        app.MapGet("/oidc/jwks", Jwks).ExcludeFromDescription();
        app.MapMethods("/oidc/authorize", ["GET", "POST"], AuthorizeAsync).ExcludeFromDescription();
        app.MapPost("/oidc/token", TokenAsync).RequireRateLimiting(TokenRateLimitPolicy).ExcludeFromDescription();
        app.MapMethods("/oidc/userinfo", ["GET", "POST"], UserInfoAsync).ExcludeFromDescription();
        app.MapMethods("/oidc/logout", ["GET", "POST"], EndSessionAsync).ExcludeFromDescription();
        app.MapPost("/oidc/revocation", RevocationAsync).RequireRateLimiting(TokenRateLimitPolicy).ExcludeFromDescription();
        return app;
    }

    // ---- discovery / jwks ------------------------------------------------

    private static IResult Discovery(IdpOptions options)
    {
        var issuer = options.Issuer;
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = issuer + "/oidc/authorize",
            ["token_endpoint"] = issuer + "/oidc/token",
            ["userinfo_endpoint"] = issuer + "/oidc/userinfo",
            ["jwks_uri"] = issuer + "/oidc/jwks",
            ["end_session_endpoint"] = issuer + "/oidc/logout",
            ["revocation_endpoint"] = issuer + "/oidc/revocation",
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = new[] { "query" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["scopes_supported"] = Scopes.Supported,
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "none" },
            ["code_challenge_methods_supported"] = new[] { Pkce.S256 },
            ["claims_supported"] = new[]
            {
                "sub", "iss", "aud", "exp", "iat", "auth_time", "nonce", "amr", "acr", "sid", "at_hash", "azp",
                "name", "preferred_username", "locale", "zoneinfo", "email", "email_verified",
                "groups", "dept", "tenant_id", "user_code", "tenant_manager", "service_manager",
            },
            ["authorization_response_iss_parameter_supported"] = true,
            ["claims_parameter_supported"] = false,
            ["request_parameter_supported"] = false,
            ["request_uri_parameter_supported"] = false,
        });
    }

    private static async Task<IResult> Jwks(OidcTokenService tokens, HttpContext http)
    {
        var jwks = await tokens.GetJwksAsync(http.RequestAborted).ConfigureAwait(false);
        http.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Json(new
        {
            keys = jwks.Select(key => new { kty = key.Kty, use = key.Use, alg = key.Alg, kid = key.Kid, n = key.N, e = key.E }),
        });
    }

    // ---- authorize -------------------------------------------------------

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        IdpOptions options,
        IOidcClientStore clients,
        IPleasanterUserStore users,
        SsoSessionService sessions,
        IAuthorizationCodeStore codes,
        ISsoSessionStore sessionStore,
        AuditWriter audit,
        TimeProvider timeProvider)
    {
        var language = LoginEndpoints.Language(http);
        var basePath = LoginEndpoints.BasePath(http);

        var parameters = await ReadParametersAsync(http).ConfigureAwait(false);
        if (parameters is null)
        {
            return ErrorPage(language, basePath, UiText.InvalidRequest);
        }

        // ---- 1. リダイレクト先が確定するまでは、エラーを利用者へ見せるだけ（リダイレクトしない）
        var clientId = parameters.GetValueOrDefault("client_id");
        var client = string.IsNullOrEmpty(clientId)
            ? null
            : await clients.FindAsync(clientId, http.RequestAborted).ConfigureAwait(false);
        if (client is null || !client.Enabled)
        {
            return ErrorPage(language, basePath, UiText.UnknownClient);
        }

        var redirectUri = parameters.GetValueOrDefault("redirect_uri");
        if (!RedirectUris.Matches(client.RedirectUris, redirectUri))
        {
            return ErrorPage(language, basePath, UiText.InvalidRequest);
        }

        // ---- 2. ここからは、エラーをリダイレクト先へ返す
        var state = parameters.GetValueOrDefault("state");
        IResult Fail(string error, string description) =>
            Results.Redirect(ErrorRedirect(options, redirectUri!, error, description, state));

        if (parameters.GetValueOrDefault("response_type") != "code")
        {
            return Fail("unsupported_response_type", "response_type must be 'code'.");
        }

        var responseMode = parameters.GetValueOrDefault("response_mode");
        if (responseMode is not null && responseMode != "query")
        {
            return Fail("invalid_request", "Only response_mode=query is supported.");
        }

        if (parameters.ContainsKey("request") || parameters.ContainsKey("request_uri"))
        {
            return Fail("request_not_supported", "Request objects are not supported.");
        }

        var requestedScopes = Scopes.Parse(parameters.GetValueOrDefault("scope"));
        if (!requestedScopes.Contains(Scopes.OpenId, StringComparer.Ordinal))
        {
            return Fail("invalid_scope", "The 'openid' scope is required.");
        }

        var denied = Scopes.Denied(requestedScopes, client.AllowedScopes);
        if (denied.Count > 0)
        {
            return Fail("invalid_scope", "Scope not allowed: " + string.Join(' ', denied));
        }

        var challenge = parameters.GetValueOrDefault("code_challenge");
        var method = parameters.GetValueOrDefault("code_challenge_method");
        if (string.IsNullOrEmpty(challenge))
        {
            if (client.IsPublic)
            {
                return Fail("invalid_request", "PKCE (code_challenge) is required for public clients.");
            }
        }
        else if (method != Pkce.S256 || !Pkce.IsValidChallenge(challenge))
        {
            return Fail("invalid_request", "code_challenge must be a valid S256 challenge.");
        }

        var nonce = parameters.GetValueOrDefault("nonce");
        if (nonce is { Length: > 512 })
        {
            return Fail("invalid_request", "nonce is too long.");
        }

        // ---- 3. セッション
        var prompt = Scopes.Parse(parameters.GetValueOrDefault("prompt"));
        var session = await sessions.GetCurrentAsync(http).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var reauthenticate = prompt.Contains("login", StringComparer.Ordinal)
            || (int.TryParse(parameters.GetValueOrDefault("max_age"), out var maxAge)
                && maxAge >= 0
                && session is not null
                && now - session.AuthTime > TimeSpan.FromSeconds(maxAge));

        if (session is not null && reauthenticate)
        {
            if (prompt.Contains("none", StringComparer.Ordinal))
            {
                return Fail("login_required", "Re-authentication is required.");
            }

            await sessions.SignOutAsync(http).ConfigureAwait(false);
            session = null;
        }

        if (session is not null)
        {
            // **セッションの持ち主を Pleasanter から引き直す。** 無効化・ロックされていれば通さない
            var user = await users.FindByUserIdAsync(session.UserId, http.RequestAborted).ConfigureAwait(false);
            if (user is null || user.TenantId != session.TenantId || LoginGate.Check(user, DateTime.Now) is not LoginDenial.None)
            {
                await sessions.SignOutAsync(http).ConfigureAwait(false);
                session = null;
            }
            else if (client.AllowedTenantIds.Count > 0 && !client.AllowedTenantIds.Contains(user.TenantId))
            {
                await audit.WriteAsync(http, "oidc.authorize.denied", StatusCodes.Status403Forbidden,
                    user.UserKey, user.LoginId, "OidcClient", client.ClientId, new { reason = "tenant-not-allowed" }).ConfigureAwait(false);
                return Fail("access_denied", "The user's tenant is not allowed for this client.");
            }
        }

        if (session is null)
        {
            if (prompt.Contains("none", StringComparer.Ordinal))
            {
                return Fail("login_required", "The user is not signed in.");
            }

            // prompt=login と max_age は、再認証が済んだあとにもう 1 度判定されないよう外して戻る
            var resume = QueryString.Create(
                parameters.Where(pair => pair.Key is not ("prompt" or "max_age"))
                    .Select(pair => new KeyValuePair<string, StringValues>(pair.Key, pair.Value)));
            var returnUrl = http.Request.PathBase + "/oidc/authorize" + resume;
            return Results.Redirect(http.Request.PathBase + "/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        // ---- 4. 認可コードを発行する
        var code = ClientSecrets.Generate();
        await codes.CreateAsync(new AuthorizationCodeEntry
        {
            CodeHash = ClientSecrets.Hash(code),
            ClientId = client.ClientId,
            SessionId = session.SessionId,
            TenantId = session.TenantId,
            UserId = session.UserId,
            RedirectUri = redirectUri!,
            Scope = Scopes.Join(requestedScopes),
            CodeChallenge = challenge ?? string.Empty,
            Nonce = nonce,
            AuthTime = session.AuthTime,
            AuthMethods = string.Join(',', session.AuthMethods),
            ExpiresAt = now + options.AuthorizationCodeLifetime,
            FamilyId = Guid.NewGuid(),
        }, http.RequestAborted).ConfigureAwait(false);

        await sessionStore.AddParticipantAsync(
            new SessionParticipant(session.SessionId, SessionParticipant.Oidc, client.ClientId, null, null),
            now, http.RequestAborted).ConfigureAwait(false);
        await audit.WriteAsync(http, "oidc.authorize", StatusCodes.Status302Found,
            $"{session.TenantId}:{session.UserId}", session.LoginId, "OidcClient", client.ClientId,
            new { scope = Scopes.Join(requestedScopes) }).ConfigureAwait(false);

        var query = new Dictionary<string, string?>
        {
            ["code"] = code,
            ["iss"] = options.Issuer,
        };
        if (state is not null)
        {
            query["state"] = state;
        }

        return Results.Redirect(QueryHelpers.AddQueryString(redirectUri!, query));
    }

    // ---- token -----------------------------------------------------------

    private static async Task<IResult> TokenAsync(
        HttpContext http,
        IdpOptions options,
        IOidcClientStore clients,
        IAuthorizationCodeStore codes,
        IRefreshTokenStore refreshTokens,
        IPleasanterUserStore users,
        OidcTokenService tokens,
        AuditWriter audit,
        TimeProvider timeProvider)
    {
        // クライアント認証は本文の形に関わらず必ず通す（形が違うときは、認証のあとで断る）
        var isForm = http.Request.HasFormContentType;
        var form = isForm
            ? await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false)
            : new FormCollection([]);
        var authentication = await AuthenticateClientAsync(http, form, clients).ConfigureAwait(false);
        if (authentication.Client is not { } client)
        {
            return authentication.Failure!;
        }

        if (!isForm)
        {
            return TokenError("invalid_request", "The request must be application/x-www-form-urlencoded.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var grantType = form["grant_type"].ToString();

        switch (grantType)
        {
            case "authorization_code":
            {
                var code = form["code"].ToString();
                var consumed = await codes.ConsumeAsync(ClientSecrets.Hash(code), now, http.RequestAborted).ConfigureAwait(false);

                if (consumed.Status is CodeConsumeStatus.Replayed && consumed.Entry is { } replayed)
                {
                    // **使用済みのコードが再び来た。** 盗まれた疑いがあるので、そのコードから出たトークンを全て止める
                    await refreshTokens.RevokeFamilyAsync(replayed.FamilyId, now, http.RequestAborted).ConfigureAwait(false);
                    await audit.WriteAsync(http, "oidc.code.replayed", StatusCodes.Status400BadRequest,
                        targetType: "OidcClient", targetId: client.ClientId).ConfigureAwait(false);
                }

                if (consumed.Status is not CodeConsumeStatus.Consumed || consumed.Entry is not { } entry
                    || entry.ClientId != client.ClientId
                    || !string.Equals(entry.RedirectUri, form["redirect_uri"].ToString(), StringComparison.Ordinal))
                {
                    return TokenError("invalid_grant", "The authorization code is invalid, expired, or already used.");
                }

                if (entry.CodeChallenge.Length > 0
                    && !Pkce.Verify(form["code_verifier"].ToString(), entry.CodeChallenge, Pkce.S256))
                {
                    return TokenError("invalid_grant", "PKCE verification failed.");
                }

                return await IssueAsync(
                    http, options, client, users, refreshTokens, tokens, audit, now,
                    entry.TenantId, entry.UserId, entry.SessionId, Scopes.Parse(entry.Scope),
                    entry.Nonce, entry.AuthTime, entry.AuthMethods.Split(',', StringSplitOptions.RemoveEmptyEntries),
                    entry.FamilyId, includeIdToken: true, "authorization_code").ConfigureAwait(false);
            }

            case "refresh_token":
            {
                var consumed = await refreshTokens.ConsumeAsync(
                    ClientSecrets.Hash(form["refresh_token"].ToString()), now, http.RequestAborted).ConfigureAwait(false);

                if (consumed.Status is RefreshConsumeStatus.Replayed && consumed.Entry is { } replayed)
                {
                    await refreshTokens.RevokeFamilyAsync(replayed.FamilyId, now, http.RequestAborted).ConfigureAwait(false);
                    await audit.WriteAsync(http, "oidc.refresh.replayed", StatusCodes.Status400BadRequest,
                        targetType: "OidcClient", targetId: client.ClientId).ConfigureAwait(false);
                }

                if (consumed.Status is not RefreshConsumeStatus.Consumed || consumed.Entry is not { } entry
                    || entry.ClientId != client.ClientId)
                {
                    return TokenError("invalid_grant", "The refresh token is invalid, expired, or revoked.");
                }

                // scope は元の範囲を超えて増やせない（省略は元のまま）
                var original = Scopes.Parse(entry.Scope);
                var requested = form.ContainsKey("scope") ? Scopes.Parse(form["scope"].ToString()) : original;
                if (requested.Any(scope => !original.Contains(scope, StringComparer.Ordinal)))
                {
                    return TokenError("invalid_scope", "The requested scope exceeds the original grant.");
                }

                return await IssueAsync(
                    http, options, client, users, refreshTokens, tokens, audit, now,
                    entry.TenantId, entry.UserId, entry.SessionId, requested,
                    nonce: null, entry.AuthTime, entry.AuthMethods.Split(',', StringSplitOptions.RemoveEmptyEntries),
                    entry.FamilyId, includeIdToken: requested.Contains(Scopes.OpenId, StringComparer.Ordinal), "refresh_token")
                    .ConfigureAwait(false);
            }

            default:
                return TokenError("unsupported_grant_type", "grant_type must be authorization_code or refresh_token.");
        }
    }

    private static async Task<IResult> IssueAsync(
        HttpContext http,
        IdpOptions options,
        OidcClient client,
        IPleasanterUserStore users,
        IRefreshTokenStore refreshTokens,
        OidcTokenService tokens,
        AuditWriter audit,
        DateTime now,
        int tenantId,
        int userId,
        Guid sessionId,
        IReadOnlyList<string> scopes,
        string? nonce,
        DateTime authTime,
        IReadOnlyList<string> authMethods,
        Guid familyId,
        bool includeIdToken,
        string grantType)
    {
        // **発行のたびに Pleasanter を引き直す。** 無効化された人にトークンを出さない
        var user = await users.FindByUserIdAsync(userId, http.RequestAborted).ConfigureAwait(false);
        if (user is null || user.TenantId != tenantId || LoginGate.Check(user, DateTime.Now) is not LoginDenial.None
            || (client.AllowedTenantIds.Count > 0 && !client.AllowedTenantIds.Contains(user.TenantId)))
        {
            await refreshTokens.RevokeFamilyAsync(familyId, now, http.RequestAborted).ConfigureAwait(false);
            return TokenError("invalid_grant", "The user is no longer allowed to sign in.");
        }

        var profile = await users.LoadProfileAsync(user, http.RequestAborted).ConfigureAwait(false);
        var granted = Scopes.Grant(scopes, client.AllowedScopes);
        var (accessToken, lifetime) = await tokens.CreateAccessTokenAsync(
            client, UserClaims.Subject(user), granted, sessionId, http.RequestAborted).ConfigureAwait(false);

        var response = new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)lifetime.TotalSeconds,
            ["scope"] = Scopes.Join(granted),
        };

        if (includeIdToken)
        {
            response["id_token"] = await tokens.CreateIdTokenAsync(
                client, UserClaims.ForScopes(profile, granted), nonce, authTime, authMethods,
                sessionId, accessToken, http.RequestAborted).ConfigureAwait(false);
        }

        if (granted.Contains(Scopes.OfflineAccess, StringComparer.Ordinal))
        {
            var refresh = ClientSecrets.Generate();
            var refreshLifetime = client.RefreshTokenLifetimeSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : options.RefreshTokenLifetime;
            await refreshTokens.CreateAsync(new RefreshTokenEntry
            {
                TokenHash = ClientSecrets.Hash(refresh),
                FamilyId = familyId,
                ClientId = client.ClientId,
                SessionId = sessionId,
                TenantId = tenantId,
                UserId = userId,
                Scope = Scopes.Join(granted),
                AuthTime = authTime,
                AuthMethods = string.Join(',', authMethods),
                ExpiresAt = now + refreshLifetime,
            }, now, http.RequestAborted).ConfigureAwait(false);
            response["refresh_token"] = refresh;
        }

        await audit.WriteAsync(http, "oidc.token.issued", StatusCodes.Status200OK,
            user.UserKey, user.LoginId, "OidcClient", client.ClientId,
            new { grant = grantType, scope = Scopes.Join(granted) }).ConfigureAwait(false);

        return Results.Json(response);
    }

    // ---- userinfo --------------------------------------------------------

    private static async Task<IResult> UserInfoAsync(
        HttpContext http,
        OidcTokenService tokens,
        ISsoSessionStore sessionStore,
        IPleasanterUserStore users)
    {
        var token = BearerToken(http);
        var info = token is null
            ? null
            : await tokens.ValidateAccessTokenAsync(token, http.RequestAborted).ConfigureAwait(false);

        if (info is null
            || !Scopes.Parse(string.Join(' ', info.Scopes)).Contains(Scopes.OpenId, StringComparer.Ordinal)
            || await sessionStore.IsRevokedAsync(info.SessionId, http.RequestAborted).ConfigureAwait(false)
            || !int.TryParse(info.Subject, out var userId))
        {
            return InvalidToken(http);
        }

        var user = await users.FindByUserIdAsync(userId, http.RequestAborted).ConfigureAwait(false);
        if (user is null || LoginGate.Check(user, DateTime.Now) is not LoginDenial.None)
        {
            return InvalidToken(http);
        }

        var profile = await users.LoadProfileAsync(user, http.RequestAborted).ConfigureAwait(false);
        return Results.Json(UserClaims.ForScopes(profile, info.Scopes));
    }

    // ---- end session -----------------------------------------------------

    private static async Task<IResult> EndSessionAsync(
        HttpContext http,
        IOidcClientStore clients,
        OidcTokenService tokens,
        SsoSessionService sessions,
        CsrfGuard csrf,
        AuditWriter audit)
    {
        var language = LoginEndpoints.Language(http);
        var basePath = LoginEndpoints.BasePath(http);
        var parameters = await ReadParametersAsync(http).ConfigureAwait(false);
        if (parameters is null)
        {
            return ErrorPage(language, basePath, UiText.InvalidRequest);
        }

        var state = parameters.GetValueOrDefault("state");
        var requestedRedirect = parameters.GetValueOrDefault("post_logout_redirect_uri");
        var hintToken = parameters.GetValueOrDefault("id_token_hint");
        var session = await sessions.GetCurrentAsync(http).ConfigureAwait(false);

        IdTokenHint? hint = null;
        OidcClient? client = null;
        if (!string.IsNullOrEmpty(hintToken))
        {
            hint = await tokens.ValidateIdTokenHintAsync(hintToken, http.RequestAborted).ConfigureAwait(false);
            if (hint is null)
            {
                return ErrorPage(language, basePath, UiText.InvalidRequest);
            }

            client = await clients.FindAsync(hint.ClientId, http.RequestAborted).ConfigureAwait(false);
        }

        // **戻り先は、ヒントのクライアントに登録されたものだけ。** それ以外へは戻さない
        string? redirect = null;
        if (requestedRedirect is not null)
        {
            if (client is null || !RedirectUris.Matches(client.PostLogoutRedirectUris, requestedRedirect))
            {
                return ErrorPage(language, basePath, UiText.InvalidRequest);
            }

            redirect = requestedRedirect;
        }

        var confirmed = hint is not null;
        if (HttpMethods.IsPost(http.Request.Method))
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
            confirmed = csrf.Validate(http, form[CsrfGuard.FormField]);
            if (!confirmed)
            {
                return ErrorPage(language, basePath, UiText.CsrfFailed);
            }
        }

        if (!confirmed)
        {
            // **ヒントの無い GET では、他所のページからの強制サインアウトを防ぐため確認を挟む**
            return LoginEndpoints.Html(HtmlPages.LogoutConfirm(language, basePath, csrf.Issue(http), session?.UserName, null, null));
        }

        // ヒントがあるときは、同じ人のセッションだけ閉じる（別の人のセッションを巻き込まない）
        if (session is not null && (hint is null || hint.Subject == session.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            await sessions.SignOutAsync(http).ConfigureAwait(false);
            await audit.WriteAsync(http, "oidc.logout", StatusCodes.Status200OK,
                $"{session.TenantId}:{session.UserId}", session.LoginId, "SsoSession", session.SessionId.ToString(),
                new { client = hint?.ClientId }).ConfigureAwait(false);
        }

        if (redirect is not null)
        {
            return Results.Redirect(state is null ? redirect : QueryHelpers.AddQueryString(redirect, "state", state));
        }

        return LoginEndpoints.Html(HtmlPages.Message(
            language, basePath, UiText.Of(language, UiText.LogoutTitle), UiText.Of(language, UiText.LoggedOut), basePath + "/login"));
    }

    // ---- revocation ------------------------------------------------------

    private static async Task<IResult> RevocationAsync(
        HttpContext http,
        IOidcClientStore clients,
        IRefreshTokenStore refreshTokens,
        TimeProvider timeProvider)
    {
        // クライアント認証は本文の形に関わらず必ず通す（形が違うときは、認証のあとで断る）
        var isForm = http.Request.HasFormContentType;
        var form = isForm
            ? await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false)
            : new FormCollection([]);
        var authentication = await AuthenticateClientAsync(http, form, clients).ConfigureAwait(false);
        if (authentication.Client is not { } client)
        {
            return authentication.Failure!;
        }

        if (!isForm)
        {
            return TokenError("invalid_request", "The request must be application/x-www-form-urlencoded.");
        }

        // **存在しない・他のクライアントのトークンでも 200。** 有効かどうかを教えない（RFC 7009 2.2）。
        // アクセストークンは自己完結の JWT なので失効できない（短命で、セッションの失効には追随する）
        var token = form["token"].ToString();
        if (token.Length > 0)
        {
            await refreshTokens.RevokeAsync(
                ClientSecrets.Hash(token), client.ClientId, timeProvider.GetUtcNow().UtcDateTime, http.RequestAborted)
                .ConfigureAwait(false);
        }

        return Results.Ok();
    }

    // ---- helpers ---------------------------------------------------------

    private sealed record ClientAuthentication(OidcClient? Client, IResult? Failure);

    /// <summary>クライアント認証（<c>client_secret_basic</c> / <c>client_secret_post</c> / 公開クライアントの <c>none</c>）。</summary>
    private static async Task<ClientAuthentication> AuthenticateClientAsync(
        HttpContext http,
        IFormCollection form,
        IOidcClientStore clients)
    {
        string? clientId = null;
        string? secret = null;
        var usedBasic = false;

        var authorization = http.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            usedBasic = true;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
                var colon = decoded.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    // RFC 6749 2.3.1: 値は application/x-www-form-urlencoded で符号化されている
                    clientId = Uri.UnescapeDataString(decoded[..colon].Replace('+', ' '));
                    secret = Uri.UnescapeDataString(decoded[(colon + 1)..].Replace('+', ' '));
                }
            }
            catch (Exception exception) when (exception is FormatException or UriFormatException)
            {
                // 読めなければ資格情報なし（下で invalid_client）
            }
        }
        else
        {
            clientId = form["client_id"].ToString();
            secret = form.ContainsKey("client_secret") ? form["client_secret"].ToString() : null;
        }

        var client = string.IsNullOrEmpty(clientId)
            ? null
            : await clients.FindAsync(clientId, http.RequestAborted).ConfigureAwait(false);

        if (client is null || !client.Enabled)
        {
            // 存在しないクライアントでも、実在するときと同じ時間をかける（存在の推測を防ぐ）
            _ = ClientSecrets.Verify(secret ?? "x", ClientSecrets.Hash("dummy"));
            return new ClientAuthentication(null, InvalidClient(http, usedBasic));
        }

        if (client.IsPublic)
        {
            // 公開クライアントはシークレットを持たない。持って来たら設定の誤りとして拒否する
            return string.IsNullOrEmpty(secret)
                ? new ClientAuthentication(client, null)
                : new ClientAuthentication(null, InvalidClient(http, usedBasic));
        }

        var storedHash = await clients.GetSecretHashAsync(client.ClientId, http.RequestAborted).ConfigureAwait(false);
        return ClientSecrets.Verify(secret, storedHash)
            ? new ClientAuthentication(client, null)
            : new ClientAuthentication(null, InvalidClient(http, usedBasic));
    }

    private static IResult InvalidClient(HttpContext http, bool usedBasic)
    {
        if (usedBasic)
        {
            http.Response.Headers.WWWAuthenticate = "Basic realm=\"idp\"";
        }

        return Results.Json(
            new { error = "invalid_client", error_description = "Client authentication failed." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    private static IResult TokenError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult InvalidToken(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    private static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7
            ? header[7..].Trim()
            : null;
    }

    private static IResult ErrorPage(string language, string basePath, string messageKey) =>
        LoginEndpoints.Html(
            HtmlPages.Message(language, basePath, UiText.Of(language, UiText.RequestErrorTitle), UiText.Of(language, messageKey)),
            StatusCodes.Status400BadRequest);

    private static string ErrorRedirect(IdpOptions options, string redirectUri, string error, string description, string? state)
    {
        var query = new Dictionary<string, string?>
        {
            ["error"] = error,
            ["error_description"] = description,
            ["iss"] = options.Issuer,
        };
        if (state is not null)
        {
            query["state"] = state;
        }

        return QueryHelpers.AddQueryString(redirectUri, query);
    }

    /// <summary>
    /// 要求のパラメータ（GET はクエリ、POST はフォーム）。
    /// **同じ名前が 2 回出てきたら <c>null</c>**（RFC 6749 3.1。値のすり替えの足掛かりにさせない）。
    /// </summary>
    private static async Task<Dictionary<string, string>?> ReadParametersAsync(HttpContext http)
    {
        IEnumerable<KeyValuePair<string, StringValues>> source = http.Request.Query;
        if (HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType)
        {
            source = source.Concat(await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false));
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in source)
        {
            if (values.Count != 1 || !result.TryAdd(key, values[0] ?? string.Empty))
            {
                return null;
            }
        }

        return result;
    }
}
