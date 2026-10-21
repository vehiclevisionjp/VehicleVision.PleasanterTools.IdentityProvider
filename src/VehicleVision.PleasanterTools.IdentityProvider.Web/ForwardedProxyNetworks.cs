using System.Net;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>信頼するリバースプロキシのネットワーク設定。</summary>
public static class ForwardedProxyNetworks
{
    /// <summary>カンマ区切りの CIDR。</summary>
    public const string Setting = "IDP_FORWARDED_NETWORKS";

    /// <summary>設定値を CIDR の一覧にする。</summary>
    public static IReadOnlyList<IPNetwork> Parse(string? value) =>
        Parse(value, Setting);

    /// <summary>指定した設定値を CIDR の一覧にする。</summary>
    public static IReadOnlyList<IPNetwork> Parse(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => ParseOne(value, setting))
            .ToArray();
    }

    private static IPNetwork ParseOne(string value, string setting) =>
        IPNetwork.TryParse(value, out var network)
            ? network
            : throw new InvalidOperationException(
                $"{setting} に CIDR でない値がある: {value}");
}
