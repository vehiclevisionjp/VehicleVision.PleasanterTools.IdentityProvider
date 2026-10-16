using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>本アプリを置くサブパス（例 <c>/idp</c>）を起動時の外部設定から決める。</summary>
/// <remarks>
/// <para>
/// **Pleasanter と同じホストで動かすための設定。** Pleasanter の cookie は Pleasanter 自身の
/// PathBase に閉じるので、Pleasanter を <c>/</c>、本アプリをサブパスへ置く。
/// </para>
/// <para>
/// **未設定なら従来どおり <c>/</c> で動く。** 画面から変えると、その場で入口を失うため外部設定だけにする
/// （<see cref="AdminPathOptions"/> と同じ扱い）。
/// </para>
/// <para>
/// ⚠️ **IIS のサブアプリケーションでは、ANCM が PathBase を先に渡してくる。**
/// そのときは未設定でも動くし、同じ値を設定しても二重にはならない
/// （<see cref="PathBaseMiddleware"/>）。
/// </para>
/// </remarks>
public sealed partial record PathBaseOptions(PathString Value)
{
    public const string Setting = "IDP_PATH_BASE";
    public const int MaximumLength = 256;

    /// <summary>サブパスを使わない（従来どおり <c>/</c> で動く）。</summary>
    public static PathBaseOptions Root { get; } = new(PathString.Empty);

    /// <summary>サブパスが設定されているか。</summary>
    public bool IsConfigured => Value.HasValue;

    /// <summary>外部設定を読み、サブパスとして使えることを確かめる。</summary>
    public static PathBaseOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[Setting]);
    }

    /// <summary>設定値を検証する。**書き間違いは起動時に止める。**</summary>
    /// <remarks>
    /// 黙って <c>/</c> へ落とすと、サブパスへ置いたつもりで全要求が 404 になり、原因を追えない。
    /// **例外の文言は英語。** Azure の Kudu の Debug console で日本語が化ける。
    /// </remarks>
    public static PathBaseOptions Parse(string? value)
    {
        // **未設定と `/` は「サブパスを使わない」。** `/` は根を指す自然な書き方なので許す
        if (string.IsNullOrWhiteSpace(value) || value == "/")
        {
            return Root;
        }

        if (value.Length > MaximumLength || !ValidPath().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"{Setting} must start with '/', must not end with '/', and may contain only "
                + "letters, digits, '-', '_', '.', or '~' in each segment "
                + $"(maximum length {MaximumLength}; no query, fragment, or percent-encoding). "
                + $"Current value: {value}");
        }

        // **`.` と `..` の区切りは受け付けない。** 正規化の仕方がプロキシごとに違い、
        // 本アプリが見るパスとブラウザが見るパスが食い違う
        if (value.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"{Setting} must not contain '.' or '..' segments. Current value: {value}");
        }

        return new PathBaseOptions(new PathString(value));
    }

    /// <summary>
    /// 外部へ見せる起点 URL（メール本文の起点など）にサブパスを足す。
    /// **既にサブパスで終わっていれば足さない。**
    /// </summary>
    /// <remarks>
    /// <c>IDP_MAIL_BASEURL</c> は「本アプリへ届く URL」として書かれる。
    /// サブパスまで書く人も、ホストまでしか書かない人も居るので、**どちらでも同じ結果**にする。
    /// 二重に足すと、どのリンクも 404 になる。
    /// </remarks>
    public string? ComposePublicUrl(string? baseUrl)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        var trimmed = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            // **ここでは直さない。** 形の検査は MailOptions が起動時・保存時に行う
            return baseUrl;
        }

        return uri.AbsolutePath.TrimEnd('/').EndsWith(Value.Value!, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + Value.Value;
    }

    [GeneratedRegex(@"^(/[A-Za-z0-9._~-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPath();
}
