namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>DB へ渡す時刻。</summary>
/// <remarks>
/// <para>
/// **DB に入れる時刻は必ずここを通す。** <c>DateTime.UtcNow</c> をそのまま渡さない。
/// </para>
/// <para>
/// 理由は 2 つあり、**いずれも対応 RDBMS の実機で踏んでいる**
/// （<c>_documents/データモデル設計.md</c> 4 章）。
/// </para>
/// </remarks>
public static class DbTime
{
    /// <summary>DB へ渡す形にした現在時刻。</summary>
    public static DateTime UtcNowTruncated() => ForDb(DateTime.UtcNow);

    /// <summary>DB へ渡す形にする。</summary>
    /// <remarks>
    /// <para>
    /// **秒未満を切り捨てる。** MySQL の <c>datetime</c> は秒未満を保持せず四捨五入するため、
    /// そのまま渡すと保存した時刻が**現在より未来に丸められ**、
    /// 「今より前か」で判定している所が通らなくなる。
    /// </para>
    /// <para>
    /// **<see cref="DateTimeKind"/> を外す。** Npgsql は <see cref="DateTimeKind.Utc"/> を見て
    /// 型を <c>timestamptz</c> と判断するが、列は <c>timestamp</c>（時間帯を持たない）なので、
    /// PostgreSQL が**セッションの時間帯へ変換してから格納する**。
    /// サーバの時間帯が <c>Asia/Tokyo</c> だと**保存値が 9 時間ずれる**（実際に踏んだ）。
    /// 種別を外せば <c>timestamp</c> と判断され、渡した値がそのまま入る。
    /// </para>
    /// <para>
    /// **列に入っているのは常に UTC。** 4 つの RDBMS のいずれも時間帯を持たない列なので、
    /// 読み出した値の <see cref="DateTimeKind"/> は <see cref="DateTimeKind.Unspecified"/> になる。
    /// 表示のための変換は <c>PleasanterDateTime</c> が一手に引き受ける。
    /// </para>
    /// </remarks>
    public static DateTime ForDb(DateTime value) => DateTime.SpecifyKind(
        new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind),
        DateTimeKind.Unspecified);

    /// <summary>DB から読んだ時刻に UTC の印を付ける。</summary>
    /// <remarks>
    /// <para>
    /// **列は時間帯を持たないので、読んだ値の種別は
    /// <see cref="DateTimeKind.Unspecified"/> になる**（<see cref="ForDb"/>）。
    /// そのまま JSON にすると末尾に <c>Z</c> が付かず、
    /// **画面が端末の時間帯として読む**（日本なら 9 時間ずれる）。
    /// </para>
    /// <para>
    /// **入っているのは常に UTC** なので、印を付け直すのが正しい。
    /// 値そのものは動かさない。
    /// </para>
    /// </remarks>
    public static DateTime AsUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>DB から読んだ時刻に UTC の印を付ける。**未設定はそのまま。**</summary>
    public static DateTime? AsUtc(DateTime? value) =>
        value is { } present ? AsUtc(present) : null;

    /// <summary>秒未満を切り捨てる。</summary>
    /// <remarks>DB へ渡すなら <see cref="ForDb"/> を使う。</remarks>
    public static DateTime Truncate(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);
}
