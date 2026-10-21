namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>ログへ出す前に、利用者が書いた文字列を均す。</summary>
/// <remarks>
/// <para>
/// **改行や制御文字をそのまま出すと、ログの行を偽装できる。**
/// たとえばログイン ID に改行を含めれば、後続に「別の出来事が起きた」ように見える
/// 行を差し込める。追跡のためのログが、追跡を妨げる道具になる。
/// </para>
/// <para>
/// CodeQL の <c>cs/log-forging</c> が指摘した（2026-08-19）。
/// **構造化ログでも、書き出し先が平文なら同じことが起きる。**
/// </para>
/// <para>
/// **長さも切る。** 長大な値を投げ込まれると、ログが埋まって他が読めなくなる。
/// </para>
/// </remarks>
public static class LogSafe
{
    /// <summary>ログへ出せる長さの上限。</summary>
    private const int MaxLength = 128;

    /// <summary>制御文字を落とし、長さを切る。</summary>
    /// <returns>
    /// 均した文字列。<c>null</c> や空なら <c>(なし)</c>。
    /// **落とした文字がある場合はその旨を添える。**
    /// </returns>
    public static string Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(なし)";
        }

        var builder = new System.Text.StringBuilder(Math.Min(value.Length, MaxLength));
        var removed = false;

        foreach (var character in value)
        {
            if (builder.Length >= MaxLength)
            {
                break;
            }

            // **改行・復帰・タブを含む制御文字をすべて落とす**
            if (char.IsControl(character))
            {
                removed = true;
                continue;
            }

            builder.Append(character);
        }

        if (builder.Length == 0)
        {
            return "(制御文字のみ)";
        }

        var text = builder.ToString();

        if (value.Length > MaxLength)
        {
            // **切ったことを隠さない。** 元が長かったと分かるようにする
            text += "…(切り詰め)";
        }
        else if (removed)
        {
            text += "(制御文字を除去)";
        }

        return text;
    }
}
