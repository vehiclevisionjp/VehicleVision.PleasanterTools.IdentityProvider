using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>cookie の値へ改竄検知の署名（HMAC-SHA256）を付ける。</summary>
/// <remarks>
/// <para>
/// **ASP.NET Core の Data Protection を使わない。** 鍵束を共有しない複数インスタンスでは、
/// 別のインスタンスへ振られた途端にログアウトになる。この IdP は
/// <c>IDP_SECRET_KEY</c> が全インスタンス共通なので、そこから派生した鍵で署名すれば、
/// **鍵束の共有を考えずに**複数インスタンスで動く。
/// </para>
/// <para>
/// **用途ごとに別の鍵を派生させる**（<paramref name="purpose"/>）。
/// ある用途の署名を、別の用途へ流用できないようにする。
/// 値は暗号化しない。**中身は秘密でないものだけを入れること**（セッション ID・期限など）。
/// </para>
/// </remarks>
public sealed class CookieSigner(byte[] secretKey)
{
    public string Sign(string purpose, string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(payload);

        var body = Pkce.Base64Url(Encoding.UTF8.GetBytes(payload));
        return body + "." + Pkce.Base64Url(Mac(purpose, body));
    }

    /// <summary>署名を確かめて、本体を返す。壊れていれば <c>null</c>。</summary>
    public string? Verify(string purpose, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == value.Length - 1)
        {
            return null;
        }

        var body = value[..dot];
        byte[] actual;
        try
        {
            actual = FromBase64Url(value[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(Mac(purpose, body), actual))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(FromBase64Url(body));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private byte[] Mac(string purpose, string body)
    {
        // 用途ごとの派生鍵（HKDF 相当の 1 段）
        var derived = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes("idp-cookie:" + purpose));
        return HMACSHA256.HashData(derived, Encoding.ASCII.GetBytes(body));
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
