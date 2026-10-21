using System.Security.Cryptography;
using System.Text;

namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Oidc;

/// <summary>PKCE（RFC 7636）。</summary>
/// <remarks>
/// **<c>S256</c> だけを受ける。** <c>plain</c> は、盗まれた認可コードと一緒に
/// <c>code_challenge</c> も見えている状況では守りにならない（RFC 9700）。
/// </remarks>
public static class Pkce
{
    public const string S256 = "S256";

    /// <summary><c>code_verifier</c> の長さの範囲（RFC 7636 4.1）。</summary>
    public const int MinimumVerifierLength = 43;

    public const int MaximumVerifierLength = 128;

    /// <summary><c>code_challenge</c>（Base64url の SHA-256。43 文字）として妥当か。</summary>
    public static bool IsValidChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(IsBase64Url);

    public static bool IsValidVerifier(string? verifier) =>
        verifier is { Length: >= MinimumVerifierLength and <= MaximumVerifierLength }
        && verifier.All(IsUnreserved);

    public static string ComputeChallenge(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>検証用の値が、認可要求のときの <c>code_challenge</c> と対応するか。</summary>
    public static bool Verify(string? verifier, string? challenge, string? method)
    {
        if (!string.Equals(method, S256, StringComparison.Ordinal)
            || !IsValidVerifier(verifier)
            || !IsValidChallenge(challenge))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ComputeChallenge(verifier!)),
            Encoding.ASCII.GetBytes(challenge!));
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsBase64Url(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static bool IsUnreserved(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
}
