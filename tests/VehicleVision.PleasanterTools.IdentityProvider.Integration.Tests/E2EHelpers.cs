using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OtpNet;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>E2E 試験の共通の道具（管理者のログイン・クライアント登録・認可コードフロー）。</summary>
internal static class E2E
{
    public const string RedirectUri = "https://app.example.com/callback";

    public const string Password = "long-enough-password";

    public static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>管理者（ServiceManager）を作り、ログイン済みのブラウザを返す。</summary>
    public static async Task<Browser> AdminAsync(IdpProcess idp)
    {
        var loginId = Unique("admin");
        await idp.Pleasanter.AddUserAsync(loginId, Password, serviceManager: true, tenantManager: true, mail: loginId + "@example.com");
        var browser = idp.NewBrowser();
        await browser.LoginAsync(loginId, Password);
        return browser;
    }

    public static async Task<HttpResponseMessage> AdminPostAsync(Browser admin, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("X-IdP-Admin", "1");
        return await admin.Http.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> AdminSendAsync(Browser admin, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("X-IdP-Admin", "1");
        return await admin.Http.SendAsync(request);
    }

    public static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    /// <summary>OIDC クライアントを登録して、(ID, シークレット) を返す。</summary>
    public static async Task<(string ClientId, string? Secret)> CreateClientAsync(
        Browser admin,
        string clientType = "confidential",
        string[]? scopes = null,
        string[]? redirectUris = null,
        int[]? tenants = null,
        string[]? postLogoutRedirectUris = null)
    {
        using var response = await AdminPostAsync(admin, "/api/admin/oidc-clients", new
        {
            displayName = "テストアプリ",
            clientType,
            redirectUris = redirectUris ?? [RedirectUri],
            postLogoutRedirectUris = postLogoutRedirectUris ?? ["https://app.example.com/signed-out"],
            allowedScopes = scopes ?? ["openid", "profile", "email", "groups", "offline_access"],
            allowedTenantIds = tenants ?? [],
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        var body = JsonNode.Parse(text)!;
        return (body["client"]!["clientId"]!.GetValue<string>(), body["clientSecret"]?.GetValue<string>());
    }

    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Pkce.Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Pkce.ComputeChallenge(verifier));
    }

    public static string AuthorizeUrl(
        string clientId,
        string challenge,
        string scope = "openid profile email groups offline_access",
        string state = "st-1",
        string nonce = "nonce-1",
        string redirectUri = RedirectUri,
        string? extra = null) =>
        "/oidc/authorize?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(clientId)
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&scope=" + Uri.EscapeDataString(scope)
        + "&state=" + Uri.EscapeDataString(state)
        + "&nonce=" + Uri.EscapeDataString(nonce)
        + "&code_challenge=" + challenge
        + "&code_challenge_method=S256"
        + extra;

    /// <summary>認可要求を出し、ログインして、リダイレクト先（<c>redirect_uri</c>）の URI を返す。</summary>
    public static async Task<Uri> AuthorizeAsync(Browser browser, string authorizeUrl, string? loginId = null, string password = Password)
    {
        using var first = await browser.GetAsync(authorizeUrl);
        if (first.StatusCode == HttpStatusCode.Redirect && first.Headers.Location!.OriginalString.StartsWith("/login", StringComparison.Ordinal))
        {
            Assert.NotNull(loginId);
            var returnUrl = QueryHelpers.ParseQuery(first.Headers.Location.OriginalString.Split('?', 2)[1])["returnUrl"].ToString();
            using var login = await browser.SubmitPasswordAsync(loginId!, password, returnUrl);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            using var second = await browser.GetAsync(login.Headers.Location!.OriginalString);
            Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
            return second.Headers.Location!;
        }

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        return first.Headers.Location!;
    }

    public static string CodeOf(Uri redirect)
    {
        var query = QueryHelpers.ParseQuery(redirect.Query);
        Assert.True(query.ContainsKey("code"), "認可コードが無い: " + redirect);
        return query["code"].ToString();
    }

    public static HttpRequestMessage TokenRequest(string clientId, string? secret, IEnumerable<KeyValuePair<string, string>> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/oidc/token") { Content = new FormUrlEncodedContent(form) };
        if (secret is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));
        }
        else
        {
            request.Content = new FormUrlEncodedContent(form.Append(new("client_id", clientId)));
        }

        return request;
    }

    public static async Task<HttpResponseMessage> ExchangeCodeAsync(
        Browser browser, string clientId, string? secret, string code, string verifier, string redirectUri = RedirectUri)
    {
        using var request = TokenRequest(clientId, secret,
        [
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("code_verifier", verifier),
        ]);
        return await browser.Http.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> RefreshAsync(Browser browser, string clientId, string? secret, string refreshToken)
    {
        using var request = TokenRequest(clientId, secret,
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
        ]);
        return await browser.Http.SendAsync(request);
    }

    /// <summary>JWKS で署名・発行者・宛先を検証して、トークンの中身を返す。</summary>
    public static async Task<JsonWebToken> ValidateAsync(Browser browser, string token, string audience, bool validateLifetime = true)
    {
        using var jwks = await browser.GetAsync("/oidc/jwks");
        var keys = new JsonWebKeySet(await jwks.Content.ReadAsStringAsync()).GetSigningKeys();
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = browser.Idp.Issuer,
            ValidAudience = audience,
            IssuerSigningKeys = keys,
            ValidateLifetime = validateLifetime,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });
        Assert.True(result.IsValid, result.Exception?.ToString());
        return (JsonWebToken)result.SecurityToken;
    }

    public static string TotpCode(string base32Secret, int stepOffset = 0) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp(DateTime.UtcNow.AddSeconds(30 * stepOffset));

    public static string NewTotpSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
}
