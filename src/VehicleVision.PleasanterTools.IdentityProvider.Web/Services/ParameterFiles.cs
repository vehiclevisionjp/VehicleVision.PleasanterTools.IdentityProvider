using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>
/// <c>App_Data/Parameters/*.json</c> を設定として積む。
/// </summary>
/// <remarks>
/// <para>
/// **Pleasanter 本体と同じ置き場・同じ書き方**にしてある
/// （<c>App_Data/Parameters/README.md</c>）。**優先順位は
/// <c>{名前}.local.json</c> ＞ 環境変数 ＞ <c>{名前}.json</c>。**
/// </para>
/// <para>
/// **1 か所にまとめてある。** 設定ごとに「JSON → 環境変数」を繰り返すと、
/// 後から積んだ環境変数が**前の <c>.local.json</c> を追い越す**（実際に踏んだ）。
/// </para>
/// <para>
/// ⚠️ **ファイルのキーと環境変数の名前が違うものがある。**
/// ファイルは短い名前（<c>Issuer</c> など）で、環境変数は衝突を避けるため <c>IDP_*</c> で始まる。
/// **そのままでは片方しか効かない**ので、<see cref="Aliases"/> で正式な名前へ写している。
/// </para>
/// <para>
/// **値が <c>null</c> や空のキーは写さない。** 写すと、環境変数で与えた値を空で塗り潰してしまう。
/// </para>
/// </remarks>
public static class ParameterFiles
{
    /// <summary>既定の置き場。</summary>
    public const string DefaultDirectory = "App_Data/Parameters";

    /// <summary>読む対象。**この順に積む**（後のものが勝つわけではない。別名の写しで揃える）。</summary>
    private static readonly ImmutableArray<string> Names =
        ["Service", "Security"];

    /// <summary>ファイルのキー → 正式な設定名（環境変数と同じ名前）。</summary>
    /// <remarks>
    /// **ここに無いキーはそのままの名前で積まれる。**
    /// <c>Security.json</c> の <c>SecondaryAuthentication</c> は Pleasanter 本体と同じ形
    /// （<c>SecondaryAuthentication:Mode</c> など）のまま読むので、写す必要が無い。
    /// </remarks>
    private static readonly ImmutableDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Service.json
            ["Issuer"] = "IDP_ISSUER",
            ["SessionLifetimeMinutes"] = "IDP_SESSION_LIFETIME_MINUTES",
            ["SessionIdleMinutes"] = "IDP_SESSION_IDLE_MINUTES",
            ["AuthorizationCodeLifetimeSeconds"] = "IDP_AUTHORIZATION_CODE_LIFETIME_SECONDS",
            ["AccessTokenLifetimeSeconds"] = "IDP_ACCESS_TOKEN_LIFETIME_SECONDS",
            ["IdTokenLifetimeSeconds"] = "IDP_ID_TOKEN_LIFETIME_SECONDS",
            ["RefreshTokenLifetimeSeconds"] = "IDP_REFRESH_TOKEN_LIFETIME_SECONDS",
            ["SamlAssertionLifetimeSeconds"] = "IDP_SAML_ASSERTION_LIFETIME_SECONDS",
            ["AdminRole"] = "IDP_ADMIN_ROLE",
            ["AdminLoginIds"] = "IDP_ADMIN_LOGIN_IDS",

            // Security.json
            ["LoginMaxFailures"] = "IDP_LOGIN_MAX_FAILURES",
            ["LoginFailureWindowMinutes"] = "IDP_LOGIN_FAILURE_WINDOW_MINUTES",
            ["LoginLockoutMinutes"] = "IDP_LOGIN_LOCKOUT_MINUTES",
            ["TotpMode"] = "IDP_TOTP_MODE",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>実際に読んだ置き場。**起動時の記録に出す。**</summary>
    public const string DirectoryKey = "IDP_PARAMETERS_DIRECTORY";

    /// <summary>実際に見つかったファイル。**空なら 1 つも読めていない。**</summary>
    /// <remarks>
    /// ⚠️ **「読めているつもりで読めていない」を起動時に見せるため**。
    /// 設定ファイルは <c>optional</c> なので、置き場を間違えても黙って既定で動いてしまう。
    /// </remarks>
    public const string FoundFilesKey = "IDP_PARAMETERS_FOUND";

    /// <summary>
    /// 設定を積む。**アプリの一番先で 1 度だけ呼ぶ**
    /// （以降の読み取りが全てこれを見る）。
    /// </summary>
    public static IConfigurationBuilder AddParameterFiles(
        this IConfigurationBuilder builder,
        string directory = DefaultDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        // ⚠️ **絶対パスへ直してから積む。**
        // JSON の側（AddJsonFile）は content root を基準にするが、
        // 別名を写すために自分で読む側は現在のディレクトリを基準にする。
        // **基準が食い違うと、片方だけ読めて気付けない**（実際に踏んだ）
        var resolved = ResolveDirectory(BasePathOf(builder), directory);

        // ---- 1. {名前}.json（一番弱い）
        foreach (var name in Names)
        {
            builder.AddJsonFile(Path.Combine(resolved, $"{name}.json"),
                optional: true, reloadOnChange: false);
        }

        builder.AddInMemoryCollection(AliasesOf(resolved, local: false));

        // ---- 2. 環境変数
        builder.AddEnvironmentVariables();

        // ---- 3. {名前}.local.json（一番強い。git 管理外）
        foreach (var name in Names)
        {
            builder.AddJsonFile(Path.Combine(resolved, $"{name}.local.json"),
                optional: true, reloadOnChange: false);
        }

        builder.AddInMemoryCollection(AliasesOf(resolved, local: true));

        // ---- 4. 何を読んだかを残す（起動時の記録に出す）
        return builder.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>(DirectoryKey, resolved),
            new KeyValuePair<string, string?>(FoundFilesKey, string.Join(" / ", FoundIn(resolved))),
        ]);
    }

    /// <summary>実際に置いてあるファイルの名前。</summary>
    public static IEnumerable<string> FoundIn(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        foreach (var name in Names)
        {
            foreach (var suffix in new[] { ".json", ".local.json" })
            {
                if (File.Exists(Path.Combine(directory, name + suffix)))
                {
                    yield return name + suffix;
                }
            }
        }
    }

    /// <summary>設定ファイルの置き場を決める。</summary>
    /// <remarks>
    /// **content root にあればそこ、無ければ実行ファイルの隣（<c>bin/…/App_Data/Parameters</c>）。**
    /// <c>dotnet run</c> の content root はプロジェクトのフォルダで、既定値の JSON はビルドで実行ファイルの隣へ
    /// 複写される（<c>.csproj</c> の <c>Content</c>）ため、どちらからでも読めるようにする。
    /// 配布物（publish・コンテナ）では両者が同じ場所になる。
    /// </remarks>
    public static string ResolveDirectory(string contentRoot, string directory)
    {
        if (Path.IsPathRooted(directory))
        {
            return directory;
        }

        var inContentRoot = Path.Combine(contentRoot, directory);
        if (Directory.Exists(inContentRoot))
        {
            return inContentRoot;
        }

        var besideExecutable = Path.Combine(AppContext.BaseDirectory, directory);
        return Directory.Exists(besideExecutable) ? besideExecutable : inContentRoot;
    }

    /// <summary>相対パスの基準。**content root に合わせる。**</summary>
    private static string BasePathOf(IConfigurationBuilder builder) =>
        builder.Properties.TryGetValue("FileProvider", out var provider)
            && provider is Microsoft.Extensions.FileProviders.PhysicalFileProvider physical
            ? physical.Root
            : Directory.GetCurrentDirectory();

    /// <summary>ファイルのキーを正式な名前へ写したものを作る。</summary>
    /// <remarks>
    /// **写す前に一度だけ読む。** 積んだ後の設定から読むと、
    /// 環境変数の値まで巻き込んで写してしまう。
    /// </remarks>
    [SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "IConfigurationRoot は読み終えた時点で用済み。IDisposable は実装側の都合で、"
            + "ここで破棄すると後続の読み取りが失敗する形にはなっていない")]
    private static IEnumerable<KeyValuePair<string, string?>> AliasesOf(string directory, bool local)
    {
        var suffix = local ? ".local.json" : ".json";
        var files = new ConfigurationBuilder();

        foreach (var name in Names)
        {
            files.AddJsonFile(Path.Combine(directory, $"{name}{suffix}"),
                optional: true, reloadOnChange: false);
        }

        var configuration = files.Build();

        foreach (var (key, setting) in Aliases)
        {
            var value = configuration[key];

            // **null と空は写さない。** 環境変数で与えた値を塗り潰さないため
            if (!string.IsNullOrEmpty(value))
            {
                yield return new KeyValuePair<string, string?>(setting, value);
            }
        }
    }
}
