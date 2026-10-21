using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Integration.Tests;

/// <summary>本物の IdP を別プロセスで起こす（本番と同じ起動経路を通す）。</summary>
/// <remarks>
/// <para>
/// **インプロセスのホスト（WebApplicationFactory）を使わない。** この IdP は起動の途中で
/// マイグレーションと署名鍵の生成を行うので、本番と同じ道（<c>dotnet …Web.dll</c>）を通した方が確かめられる。
/// IdP 自身の DB も Pleasanter 相当の DB も SQLite のファイル（外部の DB は要らない）。
/// </para>
/// <para>
/// 起こしたプロセスの標準出力は溜めておき、失敗したときの診断に添える（<see cref="Output"/>）。
/// </para>
/// </remarks>
public class IdpProcess : IAsyncLifetime
{
    public const string SecretKey = "4S8dqZ1yq7Xh0mF2vQnQZ6dQ0kGf1o8pM3nR5tU7wYs=";

    private readonly StringBuilder output = new();
    private Process? process;

    /// <summary>この起動だけに足す環境変数（<see cref="InitializeAsync"/> の前に設定する）。</summary>
    public Dictionary<string, string> ExtraEnvironment { get; } = new(StringComparer.Ordinal);

    /// <summary>IdP 自身の DB と、Pleasanter 相当の DB の RDBMS（既定は SQLite）。</summary>
    public DatabaseProvider Provider { get; init; } = DatabaseProvider.Sqlite;

    /// <summary>サブパス（例 <c>/idp</c>）へ置く構成。空なら根。</summary>
    public string PathBase { get; init; } = string.Empty;

    public Uri BaseAddress { get; private set; } = null!;

    /// <summary><c>iss</c> と SAML の EntityID の元。サブパス込み。</summary>
    public string Issuer => BaseAddress.ToString().TrimEnd('/') + PathBase;

    internal PleasanterSeeder Pleasanter { get; private set; } = null!;

    internal DbConnectionFactory IdpDatabase { get; private set; } = null!;

    public string Output
    {
        get
        {
            lock (output)
            {
                return output.ToString();
            }
        }
    }

    public async Task InitializeAsync()
    {
        var port = FreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}");

        string idpConnectionString;
        if (Provider is DatabaseProvider.Sqlite)
        {
            idpConnectionString = TestDatabases.ConnectionString(DatabaseProvider.Sqlite);
            IdpDatabase = new DbConnectionFactory(Provider, idpConnectionString);
        }
        else
        {
            // 実機の DB は、前の実行の表を落として空から始める（Pleasanter 相当の表も同じサーバへ作る）
            (IdpDatabase, idpConnectionString) = await TestDatabases.FreshAsync(Provider);
        }

        var pleasanterConnectionString = Provider is DatabaseProvider.Sqlite
            ? TestDatabases.ConnectionString(DatabaseProvider.Sqlite)
            : idpConnectionString;
        var pleasanterFactory = new DbConnectionFactory(Provider, pleasanterConnectionString);
        await PleasanterSchema.CreateAsync(pleasanterFactory);
        Pleasanter = new PleasanterSeeder(pleasanterFactory);

        var webDll = Path.Combine(AppContext.BaseDirectory, "VehicleVision.PleasanterTools.IdentityProvider.Web.dll");
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(webDll);

        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = BaseAddress.ToString().TrimEnd('/'),
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["IDP_ALLOW_INSECURE"] = "true",
            ["IDP_ISSUER"] = Issuer,
            ["IDP_SECRET_KEY"] = SecretKey,
            ["IDP_DB_PROVIDER"] = Provider.ToString(),
            ["IDP_DB_CONNECTIONSTRING"] = idpConnectionString,
            ["IDP_DB_ALLOW_INSECURE"] = "true",
            ["IDP_PLEASANTER_DB_ALLOW_INSECURE"] = "true",
            ["IDP_PLEASANTER_DB_PROVIDER"] = Provider.ToString(),
            ["IDP_PLEASANTER_DB_CONNECTIONSTRING"] = pleasanterConnectionString,
            ["IDP_LOGIN_MAX_FAILURES"] = "5",

            // **既定の上限（IP ごと）だと、同じ IP から多数のログインを重ねる試験が止まる**
            ["IDP_RATE_LIMIT_LOGIN_PER_MINUTE"] = "100000",
            ["IDP_RATE_LIMIT_TOKEN_PER_MINUTE"] = "100000",
        };
        if (PathBase.Length > 0)
        {
            environment["IDP_PATH_BASE"] = PathBase;
        }

        foreach (var (key, value) in ExtraEnvironment)
        {
            environment[key] = value;
        }

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        process = Process.Start(info) ?? throw new InvalidOperationException("IdP のプロセスを起こせなかった。");
        process.OutputDataReceived += (_, args) => Append(args.Data);
        process.ErrorDataReceived += (_, args) => Append(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitUntilReadyAsync();
    }

    public async Task DisposeAsync()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        process?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>cookie を保つ、リダイレクトを追わないクライアント（ブラウザ 1 つ分）。</summary>
    public Browser NewBrowser() => new(this);

    private async Task WaitUntilReadyAsync()
    {
        using var client = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (process!.HasExited)
            {
                throw new InvalidOperationException($"IdP が起動せずに終了した:\n{Output}");
            }

            try
            {
                using var response = await client.GetAsync("/ready");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // 署名鍵の生成は /ready のあと。JWKS が出るまで待つ
                    using var jwks = await client.GetAsync(PathBase + "/oidc/jwks");
                    if (jwks.StatusCode == HttpStatusCode.OK && (await jwks.Content.ReadAsStringAsync()).Contains("\"kid\"", StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"IdP が 90 秒以内に ready にならなかった:\n{Output}");
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>ブラウザ 1 つ分（cookie を保ち、リダイレクトは自分で追う）。</summary>
public sealed partial class Browser : IDisposable
{
    private readonly HttpClient client;
    private readonly HttpClientHandler handler;

    internal Browser(IdpProcess idp)
    {
        Idp = idp;
        handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() };
        client = new HttpClient(handler) { BaseAddress = idp.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja");
    }

    public IdpProcess Idp { get; }

    public HttpClient Http => client;

    public CookieContainer Cookies => handler.CookieContainer;

    public Task<HttpResponseMessage> GetAsync(string url) => client.GetAsync(url);

    public Task<HttpResponseMessage> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form) =>
        client.PostAsync(url, new FormUrlEncodedContent(form));

    /// <summary>ログイン画面を開いて、フォームの hidden（csrf と returnUrl）を取る。</summary>
    public async Task<(string Csrf, string ReturnUrl)> OpenLoginAsync(string returnUrl, string pathBase = "")
    {
        using var response = await client.GetAsync(pathBase + "/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        return (Hidden(html, "csrf"), Hidden(html, "returnUrl"));
    }

    /// <summary>パスワードを送る。最初の応答（リダイレクトも追わない）を返す。</summary>
    public async Task<HttpResponseMessage> SubmitPasswordAsync(string loginId, string password, string returnUrl = "/admin", string pathBase = "")
    {
        var (csrf, hiddenReturn) = await OpenLoginAsync(returnUrl, pathBase);
        return await client.PostAsync(pathBase + "/login", new FormUrlEncodedContent(
        [
            new("loginId", loginId),
            new("password", password),
            new("returnUrl", hiddenReturn),
            new("csrf", csrf),
        ]));
    }

    /// <summary>TOTP の画面を開いて、送る。</summary>
    public async Task<HttpResponseMessage> SubmitTotpAsync(string code, string returnUrl = "/admin")
    {
        using var page = await client.GetAsync("/login/totp?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var html = await page.Content.ReadAsStringAsync();
        return await client.PostAsync("/login/totp", new FormUrlEncodedContent(
        [
            new("code", code),
            new("returnUrl", returnUrl),
            new("csrf", Hidden(html, "csrf")),
        ]));
    }

    /// <summary>パスワードだけで入れるユーザのログイン（成功を前提にする）。</summary>
    public async Task LoginAsync(string loginId, string password)
    {
        using var response = await SubmitPasswordAsync(loginId, password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>リダイレクトを辿る（最大 <paramref name="max"/> 回）。最後の応答を返す。</summary>
    public async Task<HttpResponseMessage> FollowAsync(HttpResponseMessage response, int max = 8)
    {
        for (var i = 0; i < max && response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther; i++)
        {
            var location = response.Headers.Location!;
            response.Dispose();
            response = await client.GetAsync(location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString);
        }

        return response;
    }

    public static string Hidden(string html, string name)
    {
        var match = HiddenPattern(name).Match(html);
        Assert.True(match.Success, $"hidden '{name}' がページに無い:\n{html}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Regex HiddenPattern(string name) =>
        new($"name=\"{Regex.Escape(name)}\" value=\"([^\"]*)\"", RegexOptions.CultureInvariant);

    public void Dispose()
    {
        client.Dispose();
        handler.Dispose();
    }
}

/// <summary>TOTP を Pleasanter の設定（<c>SecondaryAuthentication.Mode</c>）に従わせる構成。</summary>
public sealed class PleasanterTotpModeProcess : IdpProcess
{
    public PleasanterTotpModeProcess()
    {
        ExtraEnvironment["IDP_TOTP_MODE"] = "Pleasanter";
        ExtraEnvironment["SecondaryAuthentication__Mode"] = "DefaultEnable";
    }
}

/// <summary>ログインのレート制限を低くした構成。</summary>
public sealed class LowRateLimitProcess : IdpProcess
{
    public LowRateLimitProcess()
    {
        ExtraEnvironment["IDP_RATE_LIMIT_LOGIN_PER_MINUTE"] = "3";
    }
}


/// <summary>サブパス（<c>/idp</c>）へ置いた構成。</summary>
public sealed class SubPathProcess : IdpProcess
{
    public SubPathProcess()
    {
        PathBase = "/idp";
    }
}

/// <summary>管理画面へ入れる人へ、グループ 9001 の Include と組織 9002 の Exclude を重ねた構成。</summary>
public sealed class AdminAccessRuleProcess : IdpProcess
{
    public const int AllowedGroup = 9001;

    public const int BlockedDept = 9002;

    public AdminAccessRuleProcess()
    {
        ExtraEnvironment["IDP_ADMIN_INCLUDE"] = "group:" + AllowedGroup;
        ExtraEnvironment["IDP_ADMIN_EXCLUDE"] = "dept:" + BlockedDept;
    }
}
