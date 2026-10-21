using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

/// <summary>Pleasanter の <c>Users.Password</c> と同じ形のハッシュ。</summary>
/// <remarks>
/// <para>
/// Pleasanter は <c>SHA-512(UTF-8)</c> を 16 進小文字で保存する（ソルト無し）。
/// 参照: <c>_reference/Implem.Pleasanter/Implem.Libraries/Utilities/Encryptions.cs</c> の <c>Sha512Cng</c>。
/// </para>
/// <para>
/// **この IdP は検証にだけ使う。** 保存する側の形を変える権限は無い（Pleasanter の DB は読み取り専用）。
/// 比較は定数時間で行う。
/// </para>
/// </remarks>
public static class PleasanterPassword
{
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(password)));
    }

    /// <summary>入力が保存されたハッシュと一致するか。保存値が空なら常に不一致。</summary>
    public static bool Verify(string password, string? storedHash)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(storedHash.Trim().ToLowerInvariant());
        var actual = Encoding.ASCII.GetBytes(Hash(password));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
