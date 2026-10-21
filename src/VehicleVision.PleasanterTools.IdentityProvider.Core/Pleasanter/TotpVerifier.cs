using OtpNet;

namespace VehicleVision.PleasanterTools.IdentityProvider.Core.Pleasanter;

/// <summary>Pleasanter が保存した共有鍵で TOTP（RFC 6238）を検証する。</summary>
/// <remarks>
/// <para>
/// **Pleasanter と同じ条件で検証する**（6 桁・30 秒・SHA-1。<c>UserModel.VerifyTotp</c>）。
/// 認証アプリに登録済みの鍵がそのまま使える。
/// </para>
/// <para>
/// **同じ時間枠の使い回しは呼び出し側で弾く**（<see cref="Verify"/> が返す時間枠を記録する）。
/// 30 秒の間は同じ数字が通るので、盗み見られた数字がそのまま使えてしまう。
/// これは Pleasanter 本体には無い守りで、IdP 側で足す。
/// </para>
/// <para>自前で実装しない。Otp.NET（MIT）に任せる。</para>
/// </remarks>
public static class TotpVerifier
{
    public static (bool Verified, long TimeStep) Verify(
        string? secretBase32,
        string? code,
        int countTolerances,
        DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(secretBase32) || string.IsNullOrWhiteSpace(code))
        {
            return (false, 0);
        }

        var digits = new string(code.Where(char.IsDigit).ToArray());
        if (digits.Length != 6)
        {
            return (false, 0);
        }

        byte[] secret;
        try
        {
            // Pleasanter は 4 文字ごとにハイフンを入れて見せるが、保存は区切り無し
            secret = Base32Encoding.ToBytes(secretBase32.Replace("-", string.Empty).Trim());
        }
        catch (ArgumentException)
        {
            return (false, 0);
        }

        // **端末の時計のずれを吸収するため、前（許容回数）と後ろ 1 枠だけ許す。**
        // 広げるほど総当たりが通りやすくなる
        var tolerances = Math.Clamp(countTolerances, 0, 3);
        var window = new VerificationWindow(previous: tolerances, future: 1);
        var totp = new Totp(secret, totpSize: 6);
        var verified = totp.VerifyTotp(utcNow ?? DateTime.UtcNow, digits, out var timeStep, window);
        return (verified, timeStep);
    }
}
