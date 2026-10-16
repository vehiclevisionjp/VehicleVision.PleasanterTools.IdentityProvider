using System.Text.Json;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Cryptography;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Admin;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;
using VehicleVision.PleasanterTools.IdentityProvider.Data;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Pages;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Endpoints;

/// <summary>SAML 2.0 の IdP（SP-initiated の Web ブラウザ SSO と単一ログアウト）。</summary>
/// <remarks>
/// <para>
/// 対応するもの: メタデータ、AuthnRequest（HTTP-Redirect / HTTP-POST）、署名付き応答（POST バインディング）、
/// SP 起点の単一ログアウト。対応しないもの: IdP 起点の SSO、Artifact バインディング、アサーションの暗号化、
/// 他の SP への SLO の伝播。
/// </para>
/// <para>
/// **要求は、ログインへ回す前に検証を済ませる。** 検証済みの要点（要求 ID・SP・RelayState）だけを預け、
/// ログイン後はそれを使って応答を作る。ログインの前後で SP の登録が変わっても、
/// 応答を作る時点でもう 1 度 SP を引き直す。
/// </para>
/// <para>
/// **宛先（ACS）は登録された URL だけ。** 要求が別の ACS を名乗っても従わない
/// （アサーションを任意の URL へ送らせない）。
/// </para>
/// </remarks>
public static class SamlEndpoints
{
    private const string PendingKind = "saml";

    private sealed record PendingSso(string ProviderId, string RequestId, string? RelayState);

    public static IEndpointRouteBuilder MapSamlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/saml/metadata", MetadataAsync).ExcludeFromDescription();
        app.MapMethods("/saml/sso", ["GET", "POST"], SsoAsync).ExcludeFromDescription();
        app.MapGet("/saml/sso/continue", ContinueAsync).ExcludeFromDescription();
        app.MapMethods("/saml/slo", ["GET", "POST"], SloAsync).ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> MetadataAsync(SamlIdpService saml, HttpContext http)
    {
        var xml = await saml.BuildMetadataAsync(http.RequestAborted).ConfigureAwait(false);
        return Results.Content(xml, "application/samlmetadata+xml");
    }

    // ---- SSO -------------------------------------------------------------

    private static async Task<IResult> SsoAsync(
        HttpContext http,
        IdpOptions options,
        SamlIdpService saml,
        ISamlProviderStore providers,
        SsoSessionService sessions,
        IPendingRequestStore pending,
        AuditWriter audit,
        TimeProvider timeProvider,
        ILogger<SamlIdpService> logger)
    {
        var language = LoginEndpoints.Language(http);
        var basePath = LoginEndpoints.BasePath(http);

        SamlProvider provider;
        Saml2AuthnRequest request;
        string? relayState;
        try
        {
            var generic = http.Request.ToGenericHttpRequest(validate: true);
            var probe = new Saml2AuthnRequest(new Saml2Configuration { Issuer = saml.EntityId });
            generic.Binding.ReadSamlRequest(generic, probe);
            relayState = generic.Binding.RelayState;

            if (string.IsNullOrEmpty(probe.Issuer)
                || await providers.FindByEntityIdAsync(probe.Issuer, http.RequestAborted).ConfigureAwait(false) is not { Enabled: true } found)
            {
                logger.LogWarning("SAML: 登録されていない、または無効な SP からの要求（{Issuer}）。", LogSafe.Text(probe.Issuer));
                return ErrorPage(language, basePath, UiText.UnknownClient);
            }

            provider = found;
            var hasSignature = HasSignature(generic, probe);
            if (provider.RequireSignedRequests && !hasSignature)
            {
                logger.LogWarning("SAML: 署名が必須の SP から、署名の無い要求（{Issuer}）。", LogSafe.Text(provider.EntityId));
                await audit.WriteAsync(http, "saml.request.rejected", StatusCodes.Status400BadRequest,
                    targetType: "SamlProvider", targetId: provider.EntityId, detail: new { reason = "signature-missing" }).ConfigureAwait(false);
                return ErrorPage(language, basePath, UiText.InvalidRequest);
            }

            // 署名の検証は Unbind が行う（署名があり、証明書が登録されているときだけ）
            request = new Saml2AuthnRequest(SamlIdpService.ValidationConfiguration(provider, saml.EntityId, hasSignature));
            generic.Binding.Unbind(generic, request);
        }
        catch (Exception exception) when (IsSamlFailure(exception))
        {
            // **理由は外へ返さない。** 何が通らなかったかは手掛かりになる
            logger.LogWarning(exception, "SAML の AuthnRequest を受け取れなかった。");
            return ErrorPage(language, basePath, UiText.InvalidRequest);
        }

        if (relayState is { Length: > 80 })
        {
            // SAML の仕様上限。長いものは切らずに拒否する（切ると SP の状態が壊れる）
            return ErrorPage(language, basePath, UiText.InvalidRequest);
        }

        if (request.AssertionConsumerServiceUrl is { } requestedAcs
            && !string.Equals(requestedAcs.OriginalString, provider.AcsUrl, StringComparison.Ordinal))
        {
            logger.LogWarning("SAML: 登録と違う ACS を名乗る要求（{Issuer}）。", LogSafe.Text(provider.EntityId));
            await audit.WriteAsync(http, "saml.request.rejected", StatusCodes.Status400BadRequest,
                targetType: "SamlProvider", targetId: provider.EntityId, detail: new { reason = "acs-mismatch" }).ConfigureAwait(false);
            return ErrorPage(language, basePath, UiText.InvalidRequest);
        }

        var session = await sessions.GetCurrentAsync(http).ConfigureAwait(false);
        if (session is not null && request.ForceAuthn == true)
        {
            await sessions.SignOutAsync(http).ConfigureAwait(false);
            session = null;
        }

        var info = new PendingSso(provider.ProviderId, request.IdAsString, relayState);
        if (session is null)
        {
            if (request.IsPassive == true)
            {
                return await PostToAcsAsync(http, saml, provider,
                    await saml.BuildStatusResponseAsync(provider, info.RequestId, Saml2StatusCodes.NoPassive, http.RequestAborted).ConfigureAwait(false),
                    relayState).ConfigureAwait(false);
            }

            var id = await pending.SaveAsync(
                PendingKind,
                JsonSerializer.Serialize(info),
                timeProvider.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(30),
                http.RequestAborted).ConfigureAwait(false);
            var returnUrl = http.Request.PathBase + "/saml/sso/continue?id=" + id.ToString("N");
            return Results.Redirect(http.Request.PathBase + "/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        }

        return await IssueAsync(http, options, saml, providers, sessions, info, audit, timeProvider).ConfigureAwait(false);
    }

    private static async Task<IResult> ContinueAsync(
        HttpContext http,
        IdpOptions options,
        SamlIdpService saml,
        ISamlProviderStore providers,
        SsoSessionService sessions,
        IPendingRequestStore pending,
        AuditWriter audit,
        TimeProvider timeProvider,
        string? id)
    {
        var language = LoginEndpoints.Language(http);
        var basePath = LoginEndpoints.BasePath(http);

        if (!Guid.TryParseExact(id, "N", out var requestId)
            || await pending.TakeAsync(requestId, PendingKind, timeProvider.GetUtcNow().UtcDateTime, http.RequestAborted).ConfigureAwait(false) is not { } payload
            || JsonSerializer.Deserialize<PendingSso>(payload) is not { } info
            || await sessions.GetCurrentAsync(http).ConfigureAwait(false) is null)
        {
            return ErrorPage(language, basePath, UiText.InvalidRequest);
        }

        return await IssueAsync(http, options, saml, providers, sessions, info, audit, timeProvider).ConfigureAwait(false);
    }

    /// <summary>セッションの持ち主へ、アサーション入りの応答を出す。</summary>
    private static async Task<IResult> IssueAsync(
        HttpContext http,
        IdpOptions options,
        SamlIdpService saml,
        ISamlProviderStore providers,
        SsoSessionService sessions,
        PendingSso info,
        AuditWriter audit,
        TimeProvider timeProvider)
    {
        var language = LoginEndpoints.Language(http);
        var basePath = LoginEndpoints.BasePath(http);
        var users = http.RequestServices.GetRequiredService<IPleasanterUserStore>();
        var sessionStore = http.RequestServices.GetRequiredService<ISsoSessionStore>();

        var provider = await providers.FindAsync(info.ProviderId, http.RequestAborted).ConfigureAwait(false);
        var session = await sessions.GetCurrentAsync(http).ConfigureAwait(false);
        if (provider is not { Enabled: true } || session is null)
        {
            return ErrorPage(language, basePath, UiText.UnknownClient);
        }

        // **セッションの持ち主を Pleasanter から引き直す。**
        var user = await users.FindByUserIdAsync(session.UserId, http.RequestAborted).ConfigureAwait(false);
        if (user is null || user.TenantId != session.TenantId || LoginGate.Check(user, DateTime.Now) is not LoginDenial.None)
        {
            await sessions.SignOutAsync(http).ConfigureAwait(false);
            return ErrorPage(language, basePath, UiText.CsrfFailed);
        }

        if (provider.AllowedTenantIds.Count > 0 && !provider.AllowedTenantIds.Contains(user.TenantId))
        {
            await audit.WriteAsync(http, "saml.response.denied", StatusCodes.Status403Forbidden,
                user.UserKey, user.LoginId, "SamlProvider", provider.EntityId, new { reason = "tenant-not-allowed" }).ConfigureAwait(false);
            return ErrorPage(language, basePath, UiText.TenantNotAllowed, StatusCodes.Status403Forbidden);
        }

        if (!await AccessCheck.AllowsAsync(users, user, provider.AccessRules, http.RequestAborted).ConfigureAwait(false))
        {
            await audit.WriteAsync(http, "saml.response.denied", StatusCodes.Status403Forbidden,
                user.UserKey, user.LoginId, "SamlProvider", provider.EntityId, new { reason = "access-rule" }).ConfigureAwait(false);
            return ErrorPage(language, basePath, UiText.TenantNotAllowed, StatusCodes.Status403Forbidden);
        }

        var profile = await users.LoadProfileAsync(user, http.RequestAborted).ConfigureAwait(false);
        var nameId = provider.NameIdFormat == "transient"
            ? Guid.NewGuid().ToString("N")
            : UserClaims.SamlNameId(profile, provider.NameIdSource);
        if (string.IsNullOrWhiteSpace(nameId))
        {
            // **メールアドレスが無いのに、メールを NameID に求められた。** 空の NameID は出さない
            await audit.WriteAsync(http, "saml.response.denied", StatusCodes.Status403Forbidden,
                user.UserKey, user.LoginId, "SamlProvider", provider.EntityId, new { reason = "name-id-unavailable" }).ConfigureAwait(false);
            return ErrorPage(language, basePath, UiText.NoMailForSaml, StatusCodes.Status403Forbidden);
        }

        var sessionIndex = SamlIdpService.SessionIndexOf(session.SessionId);
        var samlResponse = await saml.BuildResponseAsync(
            provider, info.RequestId, info.RelayState, profile, sessionIndex,
            multiFactor: session.AuthMethods.Contains("otp", StringComparer.Ordinal),
            nameId, SamlIdpService.NameIdFormatOf(provider.NameIdFormat), http.RequestAborted).ConfigureAwait(false);

        await sessionStore.AddParticipantAsync(
            new SessionParticipant(session.SessionId, SessionParticipant.Saml, provider.EntityId, sessionIndex, nameId),
            timeProvider.GetUtcNow().UtcDateTime, http.RequestAborted).ConfigureAwait(false);
        await audit.WriteAsync(http, "saml.response.issued", StatusCodes.Status200OK,
            user.UserKey, user.LoginId, "SamlProvider", provider.EntityId).ConfigureAwait(false);

        return await PostToAcsAsync(http, saml, provider, samlResponse, info.RelayState).ConfigureAwait(false);
    }

    /// <summary>応答を ACS へ POST するページ（自動送信）。</summary>
    private static Task<IResult> PostToAcsAsync(
        HttpContext http,
        SamlIdpService saml,
        SamlProvider provider,
        string samlResponse,
        string? relayState)
    {
        _ = saml;
        var fields = new Dictionary<string, string> { ["SAMLResponse"] = samlResponse };
        if (!string.IsNullOrEmpty(relayState))
        {
            fields["RelayState"] = relayState;
        }

        // **この応答に限り、フォームの送信先として ACS のオリジンを許す。** 既定の CSP は自身だけ
        var acsOrigin = new Uri(provider.AcsUrl).GetLeftPart(UriPartial.Authority);
        http.Response.Headers.ContentSecurityPolicy =
            $"default-src 'self'; script-src 'self'; style-src 'self'; form-action 'self' {acsOrigin}; base-uri 'self'; frame-ancestors 'none'; object-src 'none'";

        return Task.FromResult(LoginEndpoints.Html(HtmlPages.AutoPost(
            LoginEndpoints.Language(http), LoginEndpoints.BasePath(http), provider.AcsUrl, fields)));
    }

    // ---- SLO -------------------------------------------------------------

    private static async Task<IResult> SloAsync(
        HttpContext http,
        SamlIdpService saml,
        ISamlProviderStore providers,
        SsoSessionService sessions,
        ISsoSessionStore sessionStore,
        AuditWriter audit,
        ILogger<SamlIdpService> logger)
    {
        var language = LoginEndpoints.Language(http);
        var basePath = LoginEndpoints.BasePath(http);

        SamlProvider provider;
        Saml2LogoutRequest request;
        string? relayState;
        try
        {
            var generic = http.Request.ToGenericHttpRequest(validate: true);
            var probe = new Saml2LogoutRequest(new Saml2Configuration { Issuer = saml.EntityId });
            generic.Binding.ReadSamlRequest(generic, probe);
            relayState = generic.Binding.RelayState;

            if (string.IsNullOrEmpty(probe.Issuer)
                || await providers.FindByEntityIdAsync(probe.Issuer, http.RequestAborted).ConfigureAwait(false) is not { } found)
            {
                return ErrorPage(language, basePath, UiText.UnknownClient);
            }

            provider = found;
            var hasSignature = HasSignature(generic, probe);

            // **証明書を登録した SP は、署名の無いログアウト要求を受け付けない。**
            // 誰でもログアウトさせられる口にしない
            if (!string.IsNullOrWhiteSpace(provider.SigningCertificate) && !hasSignature)
            {
                return ErrorPage(language, basePath, UiText.InvalidRequest);
            }

            request = new Saml2LogoutRequest(SamlIdpService.ValidationConfiguration(provider, saml.EntityId, hasSignature));
            generic.Binding.Unbind(generic, request);
        }
        catch (Exception exception) when (IsSamlFailure(exception))
        {
            logger.LogWarning(exception, "SAML の LogoutRequest を受け取れなかった。");
            return ErrorPage(language, basePath, UiText.InvalidRequest);
        }

        // 閉じるセッション: cookie があればそれ。無ければ SessionIndex（署名を確かめた要求だけ）
        var session = await sessions.GetCurrentAsync(http).ConfigureAwait(false);
        Guid? target = session?.SessionId;
        if (target is null && !string.IsNullOrWhiteSpace(provider.SigningCertificate)
            && SamlIdpService.SessionIdOf(request.SessionIndex) is { } indexed)
        {
            var participants = await sessionStore.ListParticipantsAsync(indexed, http.RequestAborted).ConfigureAwait(false);
            if (participants.Any(p => p.Kind == SessionParticipant.Saml
                && p.ClientKey == provider.EntityId
                && string.Equals(p.NameId, request.NameId?.Value, StringComparison.Ordinal)))
            {
                target = indexed;
            }
        }

        if (target is { } sessionId)
        {
            await sessions.RevokeAsync(sessionId, http.RequestAborted).ConfigureAwait(false);
            if (session is not null && session.SessionId == sessionId)
            {
                http.Response.Cookies.Delete(SsoSessionService.SessionCookie, sessions.CookieOptions(http, null));
            }

            await audit.WriteAsync(http, "saml.logout", StatusCodes.Status200OK,
                targetType: "SsoSession", targetId: sessionId.ToString(), detail: new { provider = provider.EntityId }).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(provider.SloUrl))
        {
            return LoginEndpoints.Html(HtmlPages.Message(
                language, basePath, UiText.Of(language, UiText.LogoutTitle), UiText.Of(language, UiText.LoggedOut), basePath + "/login"));
        }

        var configuration = await saml.SigningConfigurationAsync(provider, http.RequestAborted).ConfigureAwait(false);
        var response = new Saml2LogoutResponse(configuration)
        {
            InResponseTo = request.Id,
            Status = Saml2StatusCodes.Success,
            Destination = new Uri(provider.SloUrl),
        };

        var binding = new Saml2RedirectBinding { RelayState = relayState };
        _ = binding.Bind(response);
        return Results.Redirect(binding.RedirectLocation.OriginalString);
    }

    // ---- helpers ---------------------------------------------------------

    private static IResult ErrorPage(string language, string basePath, string messageKey, int status = StatusCodes.Status400BadRequest) =>
        LoginEndpoints.Html(
            HtmlPages.Message(language, basePath, UiText.Of(language, UiText.RequestErrorTitle), UiText.Of(language, messageKey)),
            status);

    /// <summary>要求に署名が付いているか。Redirect は問い合わせ文字列、POST は XML の中を見る。</summary>
    private static bool HasSignature(ITfoxtec.Identity.Saml2.Http.HttpRequest request, Saml2Request parsed) =>
        request.Binding is Saml2RedirectBinding
            ? request.Query.AllKeys.Contains("Signature", StringComparer.Ordinal)
            : parsed.XmlDocument?.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#").Count > 0;

    private static bool IsSamlFailure(Exception exception) =>
        exception is Saml2RequestException
            or Saml2BindingException
            or InvalidSignatureException
            or InvalidOperationException
            or ArgumentException
            or FormatException
            or System.IO.InvalidDataException
            or System.Xml.XmlException;
}
