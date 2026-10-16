using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>管理画面の入口を起動時の外部設定から決める。</summary>
public sealed partial record AdminPathOptions(string Path)
{
    public const string Setting = "IDP_ADMIN_PATH";
    public const string DefaultPath = "/admin";
    public const int MaximumLength = 64;

    private static readonly HashSet<string> ReservedPaths = new(StringComparer.Ordinal)
    {
        "/api",
        "/assets",
        "/f",
        "/fonts",
        "/healthz",
        "/openapi",
        "/ready",
        "/scalar",
    };

    /// <summary>外部設定を読み、管理画面の入口として使えることを確かめる。</summary>
    public static AdminPathOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(configuration[Setting]);
    }

    /// <summary>設定値を検証する。未設定だけは従来の入口を使う。</summary>
    public static AdminPathOptions Parse(string? value)
    {
        var path = value ?? DefaultPath;
        if (path.Length > MaximumLength || !ValidPath().IsMatch(path))
        {
            throw new InvalidOperationException(
                $"{Setting} must start with '/' and contain only lowercase letters, "
                + $"digits, or hyphens after it, with a maximum length of {MaximumLength}.");
        }

        if (ReservedPaths.Contains(path))
        {
            throw new InvalidOperationException(
                $"{Setting} conflicts with an existing endpoint: {path}.");
        }

        return new AdminPathOptions(path);
    }

    [GeneratedRegex("^/[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPath();
}
