namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>ブラウザと本アプリ間の通信を HTTP でも許すかを表す。</summary>
public sealed record TransportSecurityOptions(bool AllowInsecure)
{
    public const string AllowInsecureSetting = "IDP_ALLOW_INSECURE";

    /// <summary>環境変数の明示値を読み取る。</summary>
    public static TransportSecurityOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[AllowInsecureSetting];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TransportSecurityOptions(false);
        }

        return bool.TryParse(raw.Trim(), out var allowInsecure)
            ? new TransportSecurityOptions(allowInsecure)
            : throw new InvalidOperationException(
                $"{AllowInsecureSetting} must be true or false.");
    }
}
