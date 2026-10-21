using System.Security.Cryptography;
using System.Text;
using VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>クライアントシークレット・認可コード・リフレッシュトークンの生成とハッシュ。</summary>
/// <remarks>
/// <para>
/// **256 ビットの乱数なので、SHA-256 で足りる。** パスワードのような低エントロピーの値ではないため、
/// 遅いハッシュ（PBKDF2 など）にする理由が無く、要求ごとの検証が軽く済む。
/// </para>
/// <para>**値そのものは DB へ入れない。** 漏れても、そのままでは使えない。</para>
/// </remarks>
public static class ClientSecrets
{
    private const int Bytes = 32;

    /// <summary>新しい乱数（Base64url）。</summary>
    public static string Generate() => Pkce.Base64Url(RandomNumberGenerator.GetBytes(Bytes));

    public static string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>定数時間で照合する。保存値が無ければ <c>false</c>。</summary>
    public static bool Verify(string? presented, string? storedHash)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(presented)),
            Encoding.ASCII.GetBytes(storedHash));
    }
}
