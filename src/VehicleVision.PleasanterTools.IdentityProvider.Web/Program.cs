using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using VehicleVision.PleasanterTools.IdentityProvider.Data;
using VehicleVision.PleasanterTools.IdentityProvider.Web;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Endpoints;
using VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

// **設定に書く鍵を作るための道。** 手で乱数を用意させると、短い値や使い回しが混ざる
if (args.Contains("--generate-secret-key"))
{
    Console.WriteLine(SecretProtector.GenerateKey());
    return;
}

var builder = WebApplication.CreateBuilder(args);

// ---- App_Data/Parameters の設定ファイル --------------------------------------
// **一番先に積む。** 以降の読み取りがすべてこれを見る。
// **優先順位は {名前}.local.json ＞ 環境変数 ＞ {名前}.json**（App_Data/Parameters/README.md）
builder.Configuration.AddParameterFiles();

// **入口の経路は起動時に 1 度だけ決める。** 画面から変えると、その場で入口を失うため外部設定だけにする。
var adminPath = AdminPathOptions.FromConfiguration(builder.Configuration);
var pathBase = PathBaseOptions.FromConfiguration(builder.Configuration);
var idpOptions = IdpOptions.FromConfiguration(builder.Configuration);
var endpointNetworkRestrictions = EndpointNetworkRestrictions.FromConfiguration(builder.Configuration);
var transportSecurity = TransportSecurityOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(adminPath);
builder.Services.AddSingleton(pathBase);
builder.Services.AddSingleton(idpOptions);
builder.Services.AddSingleton<HtmlShell>();
builder.Services.AddSingleton(TimeProvider.System);

// **署名・暗号化に使う鍵。** 失うと、保存した署名鍵が復号できなくなる。
// App Service の設定か Key Vault に置き、控えを取っておくこと
var secretKeyText = builder.Configuration["IDP_SECRET_KEY"]
    ?? throw new InvalidOperationException("IDP_SECRET_KEY is not set (Base64 of 32 bytes; generate with --generate-secret-key).");
var secretKey = SecretProtector.DecodeKey(secretKeyText);
builder.Services.AddSingleton(new SecretProtector(secretKeyText));
builder.Services.AddSingleton(new CookieSigner(secretKey));

// ---- 2 つの DB -------------------------------------------------------------
// IdP 自身の DB（読み書き）。Pleasanter の DB は別（読み取り専用）
var provider = Enum.Parse<DatabaseProvider>(
    builder.Configuration["IDP_DB_PROVIDER"] ?? nameof(DatabaseProvider.SqlServer), ignoreCase: true);
var connectionString = DatabaseConnectionString.Resolve(
    provider,
    builder.Configuration["IDP_DB_CONNECTIONSTRING"],
    builder.Environment.ContentRootPath);

var pleasanterProvider = Enum.Parse<DatabaseProvider>(
    builder.Configuration["IDP_PLEASANTER_DB_PROVIDER"] ?? nameof(DatabaseProvider.SqlServer), ignoreCase: true);
var pleasanterConnectionString = builder.Configuration["IDP_PLEASANTER_DB_CONNECTIONSTRING"];
if (string.IsNullOrWhiteSpace(pleasanterConnectionString))
{
    throw new InvalidOperationException("IDP_PLEASANTER_DB_CONNECTIONSTRING is not set.");
}

// **DB への通信が平文で流れていないかを起動時に見る。**
ConnectionSecurity.EnsureSecure(
    provider,
    connectionString,
    allowInsecure: string.Equals(builder.Configuration["IDP_DB_ALLOW_INSECURE"], "true", StringComparison.OrdinalIgnoreCase));
ConnectionSecurity.EnsureSecure(
    pleasanterProvider,
    pleasanterConnectionString,
    allowInsecure: string.Equals(builder.Configuration["IDP_PLEASANTER_DB_ALLOW_INSECURE"], "true", StringComparison.OrdinalIgnoreCase),
    allowSetting: "IDP_PLEASANTER_DB_ALLOW_INSECURE");

// **マイグレーションを手動で当てる口も残す。** 当てる道具を別に作らない
if (MigrationCommand.IsRequested(args))
{
    Environment.ExitCode = await MigrationCommand.RunAsync(provider, connectionString, args);
    return;
}

var databaseStartupState = new DatabaseStartupState();
builder.Services.AddSingleton(databaseStartupState);
builder.Services.AddSingleton<IDbConnectionFactory>(new DbConnectionFactory(provider, connectionString));
builder.Services.AddSingleton<IPleasanterConnectionFactory>(
    new PleasanterConnectionFactory(pleasanterProvider, pleasanterConnectionString));

builder.Services.AddSingleton<IPleasanterUserStore, PleasanterUserStore>();
builder.Services.AddSingleton<IOidcClientStore, OidcClientStore>();
builder.Services.AddSingleton<ISamlProviderStore, SamlProviderStore>();
builder.Services.AddSingleton<ISigningKeyStore, SigningKeyStore>();
builder.Services.AddSingleton<ISsoSessionStore, SsoSessionStore>();
builder.Services.AddSingleton<IAuthorizationCodeStore, AuthorizationCodeStore>();
builder.Services.AddSingleton<IRefreshTokenStore, RefreshTokenStore>();
builder.Services.AddSingleton<ILoginSecurityStore, LoginSecurityStore>();
builder.Services.AddSingleton<IPendingRequestStore, PendingRequestStore>();
builder.Services.AddSingleton<IAuditLogStore, AuditLogStore>();
builder.Services.AddSingleton<IExpiredDataStore, ExpiredDataStore>();

builder.Services.AddSingleton<SigningKeyService>();
builder.Services.AddSingleton<AuthenticationService>();
builder.Services.AddSingleton<SsoSessionService>();
builder.Services.AddSingleton<CsrfGuard>();
builder.Services.AddSingleton<AuditWriter>();
builder.Services.AddSingleton<OidcTokenService>();
builder.Services.AddSingleton<SamlIdpService>();
builder.Services.AddSingleton(AuditRetention.FromConfiguration(builder.Configuration));
builder.Services.AddHostedService<ExpiredDataCleanupService>();
builder.Services.AddHostedService<ParameterFilesReport>();

// **フォームは小さい。** 巨大なフォームを受けて資源を使わせない
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueCountLimit = 64;
    options.ValueLengthLimit = 16 * 1024;
    options.MultipartBodyLengthLimit = 64 * 1024;
});
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // 日本語の表示名などを \uXXXX へ逃がさない（HTML に効く文字は逃がしたまま）
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
        System.Text.Unicode.UnicodeRanges.All);
});

// **総当たりと負荷の入口を絞る。** IP ごとの固定窓。ログイン失敗の締め出しは DB 側（複数インスタンスで共有）
// 上限は設定で変えられる（IDP_RATE_LIMIT_LOGIN_PER_MINUTE / IDP_RATE_LIMIT_TOKEN_PER_MINUTE）。
// 同じ出口（NAT・プロキシ）から多数の利用者が入る構成では、緩める必要がある
var loginPerMinute = IdpOptions.PositiveInt(builder.Configuration, "IDP_RATE_LIMIT_LOGIN_PER_MINUTE", 20);
var tokenPerMinute = IdpOptions.PositiveInt(builder.Configuration, "IDP_RATE_LIMIT_TOKEN_PER_MINUTE", 120);
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(LoginEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        $"login:{context.Connection.RemoteIpAddress}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = loginPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    limiter.AddPolicy(OidcEndpoints.TokenRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        $"token:{context.Connection.RemoteIpAddress}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = tokenPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

if (transportSecurity.AllowInsecure)
{
    // **英語で書く。** Azure の Kudu の Debug console で日本語が化ける
    app.Logger.LogWarning(
        "Insecure HTTP mode is enabled by IDP_ALLOW_INSECURE. HTTPS redirection and HSTS are disabled. "
        + "Use this mode only in a closed network because passwords are sent in plaintext.");
}

if (provider is DatabaseProvider.Sqlite)
{
    app.Logger.LogWarning(
        "SQLite mode is enabled by IDP_DB_PROVIDER=Sqlite. Use SQLite only for simple setup and debugging, not for production.");
}

if (pleasanterProvider is DatabaseProvider.Sqlite)
{
    app.Logger.LogWarning("IDP_PLEASANTER_DB_PROVIDER=Sqlite is for tests only. Pleasanter supports SQL Server, PostgreSQL and MySQL.");
}

// **リバースプロキシ配下でも本当の送信元 IP とスキームを見る。** レート制限と cookie の Secure のため。
// 転送ヘッダを無条件には信じず、運用者が指定したプロキシの CIDR だけを追加する。
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
foreach (var network in ForwardedProxyNetworks.Parse(builder.Configuration[ForwardedProxyNetworks.Setting]))
{
    forwardedHeadersOptions.KnownIPNetworks.Add(network);
}

app.UseForwardedHeaders(forwardedHeadersOptions);

// **サブパスを剥がしてから経路を照合する。** `UseRouting` を明示しないと、剥がす前の Path で照合してしまう
app.UseIdpPathBase(pathBase);
app.UseRouting();
app.UseEndpointNetworkRestrictions(endpointNetworkRestrictions);

// **DB スキーマが揃うまでは生存確認と readiness 以外へ通さない。**
app.Use(async (context, next) =>
{
    if (!databaseStartupState.IsReady && !DatabaseStartupMigration.IsAvailableBeforeMigration(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }

    await next();
});

if (!app.Environment.IsDevelopment() && !transportSecurity.AllowInsecure)
{
    app.UseHsts();

    // Kubelet の HTTP probe は 3xx も成功と扱うので、この 2 経路だけリダイレクトしない
    app.UseWhen(
        context => !string.Equals(context.Request.Path.Value, "/healthz", StringComparison.Ordinal)
            && !string.Equals(context.Request.Path.Value, "/ready", StringComparison.Ordinal),
        branch => branch.UseHttpsRedirection());
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();

// 管理画面（TypeScript + Vite + Svelte のビルド成果物）。入口の HTML は配置先の経路を埋めてから返す。
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = StaticCachePolicy.Apply });

app.MapLoginEndpoints();
app.MapOidcEndpoints();
app.MapSamlEndpoints();
app.MapAdminEndpoints();

IResult AdminPage(HttpContext context, HtmlShell shell)
{
    context.Response.Headers.CacheControl = StaticCachePolicy.RevalidateValue;
    return shell.Admin(context) is { } html
        ? Results.Content(html, "text/html; charset=utf-8")
        : Results.NotFound();
}

app.MapGet(adminPath.Path, AdminPage);
app.MapFallback($"{adminPath.Path}/{{**path}}", AdminPage);

// **管理画面の素の HTML は配らない。** 入口は設定した管理画面パスだけ
app.MapMethods($"/{HtmlShell.AdminFile}", [HttpMethods.Get, HttpMethods.Head], () => Results.NotFound());

// 生存確認。ユーザの情報を出さない
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).DisableRateLimiting();

// 受付可能かの確認。**ここで見る依存先は IdP 自身の DB だけ。**
// Pleasanter の DB が止まっても、プロセスを再起動して直るものではない（状態は管理画面に出る）。
// 例外の中身は接続先や資格情報を含み得るので応答へ出さない。
app.MapGet("/ready", async (CancellationToken cancellationToken) =>
{
    if (!databaseStartupState.IsReady)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var failure = await DatabaseMigrator.WaitForDatabaseAsync(provider, connectionString, TimeSpan.Zero, cancellationToken);
    return failure is null
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
}).DisableRateLimiting();

await app.StartAsync();
try
{
    await DatabaseStartupMigration.RunAsync(
        builder.Configuration,
        provider,
        connectionString,
        databaseStartupState,
        app.Logger,
        app.Lifetime.ApplicationStopping);

    // 署名鍵が無ければ作る（初回起動）
    await app.Services.GetRequiredService<SigningKeyService>().EnsureAsync(app.Lifetime.ApplicationStopping);
    await app.WaitForShutdownAsync();
}
catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
{
}
finally
{
    await app.StopAsync();
}

/// <summary>結合テストから参照するための入口。</summary>
public partial class Program;
