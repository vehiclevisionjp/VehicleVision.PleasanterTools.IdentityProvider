namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>RDBMS ごとに書き方が違う部分を閉じ込める。</summary>
/// <remarks>
/// **方言差はここだけに置く。** 呼び出し側へ散らさない
/// （設計方針）。
/// </remarks>
public static partial class SqlDialect
{
    /// <summary>起動時マイグレーションのロックを直ちに取得する SQL。</summary>
    /// <remarks>
    /// 待機は呼び出し側で有限時間だけ繰り返す。接続を閉じれば DB 側でも自動解放される。
    /// SQLite は名前付きロックを持たないため、この SQL は使わない。
    /// </remarks>
    public static string TryAcquireMigrationLock(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "DECLARE @Result int; "
            + "EXEC @Result = sp_getapplock "
            + "@Resource = @Name, @LockMode = 'Exclusive', "
            + "@LockOwner = 'Session', @LockTimeout = 0; "
            + "SELECT @Result;",
        DatabaseProvider.PostgreSql => "SELECT pg_try_advisory_lock(@Key);",
        DatabaseProvider.MySql => "SELECT GET_LOCK(@Name, 0);",
        DatabaseProvider.Sqlite => throw new NotSupportedException(
            "SQLite のマイグレーション排他にはファイルロックを使う"),
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>起動時マイグレーションのロックを解放する SQL。</summary>
    public static string ReleaseMigrationLock(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer =>
            "EXEC sp_releaseapplock @Resource = @Name, @LockOwner = 'Session';",
        DatabaseProvider.PostgreSql => "SELECT pg_advisory_unlock(@Key);",
        DatabaseProvider.MySql => "SELECT RELEASE_LOCK(@Name);",
        DatabaseProvider.Sqlite => throw new NotSupportedException(
            "SQLite のマイグレーション排他にはファイルロックを使う"),
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>ロック取得 SQL の戻り値が成功を表すか。</summary>
    public static bool MigrationLockAcquired(DatabaseProvider provider, object? result) => provider switch
    {
        DatabaseProvider.SqlServer => result is not null
            && result is not DBNull
            && Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) >= 0,
        DatabaseProvider.PostgreSql => result is true,
        DatabaseProvider.MySql => result is not null
            && result is not DBNull
            && Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1,
        DatabaseProvider.Sqlite => throw new NotSupportedException(
            "SQLite のマイグレーション排他にはファイルロックを使う"),
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>最後に適用したマイグレーションの時刻を読む SQL。</summary>
    public const string LastMigrationAppliedAt =
        "SELECT MAX([AppliedOn]) FROM [VersionInfo]";

    /// <summary>SQL の中の識別子を、その RDBMS の引用符へ書き換える。</summary>
    /// <remarks>
    /// <para>
    /// **SQL を SQL のまま書けるようにするためのもの。**
    /// 文字列の連結と補間で組み立てると、読むのに頭の中で展開する必要があるうえ、
    /// **引用を忘れても動いてしまう**ので気付けない。
    /// </para>
    /// <code>
    /// Format(provider, "SELECT [PublicId] FROM [Surveys] WHERE [SurveyId] = @SurveyId")
    /// </code>
    /// <para>
    /// **角括弧で囲んだ所だけが書き換わる。** 素の識別子はそのまま残るので、
    /// 囲み忘れが目で見て分かる。SQL Server では書き換えが起きない
    /// （角括弧がそのまま引用符になる）。
    /// </para>
    /// <para>
    /// ⚠️ **文字列リテラルを含む SQL には使わないこと。**
    /// リテラルの中の <c>[</c> まで書き換えてしまう。
    /// 値はパラメータで渡すのが決まりなので、今のところ該当する SQL は無い。
    /// </para>
    /// </remarks>
    public static string Format(DatabaseProvider provider, string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        // SQL Server は角括弧がそのまま引用符。**触らない**
        if (provider is DatabaseProvider.SqlServer)
        {
            return sql;
        }

        return IdentifierPattern().Replace(
            sql,
            match => Quote(provider, match.Groups[1].Value));
    }

    /// <summary>角括弧で囲まれた識別子。</summary>
    /// <remarks>
    /// **識別子として妥当な形だけを拾う。** 配列の添字などを巻き込まないため。
    /// </remarks>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\[([A-Za-z_][A-Za-z0-9_]*)\]",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex IdentifierPattern();

    /// <summary>識別子を引用する。</summary>
    public static string Quote(DatabaseProvider provider, string identifier) => provider switch
    {
        DatabaseProvider.SqlServer => $"[{identifier}]",
        DatabaseProvider.PostgreSql or DatabaseProvider.Sqlite => $"\"{identifier}\"",
        DatabaseProvider.MySql => $"`{identifier}`",
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary>件数を絞る句。<c>@Limit</c> と <c>@Offset</c> を使う。</summary>
    /// <remarks>
    /// **書き方が 3 者で違う。** SQL Server は <c>OFFSET/FETCH</c>、
    /// PostgreSQL と MySQL は <c>LIMIT</c>。
    /// **MySQL は <c>OFFSET/FETCH</c> を解さない**（8.4 で確認）。
    /// **どちらも <c>ORDER BY</c> が要る**ので、呼ぶ側で必ず付けること。
    /// </remarks>
    public static string Page(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => "OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY",
        DatabaseProvider.PostgreSql or DatabaseProvider.MySql or DatabaseProvider.Sqlite =>
            "LIMIT @Limit OFFSET @Offset",
        _ => throw new NotSupportedException($"対応していない RDBMS: {provider}"),
    };

    /// <summary><c>LIKE</c> の逃がし文字。</summary>
    /// <remarks>
    /// **円記号を使わない。** MySQL は文字列リテラルの中でも円記号を逃がし文字として
    /// 解するので、<c>ESCAPE</c> の指定そのものが 3 者で書き分けになる。
    /// **記号を変えれば書き分けが要らない。**
    /// </remarks>
    public const char LikeEscape = '!';

    /// <summary><c>LIKE</c> へ渡す値から記号を逃がす。</summary>
    /// <remarks>
    /// **逃がさないと、利用者が書いた <c>%</c> が「何でも」になる。**
    /// SQL の挿し込みにはならない（値は引数で渡すため）が、
    /// **絞ったつもりで絞れていない**のは、監査ログを読む道具として致命的。
    /// </remarks>
    public static string EscapeLike(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            // **`[` は SQL Server だけの記号。** 3 者で同じ結果にするため常に逃がす
            if (character is LikeEscape or '%' or '_' or '[')
            {
                builder.Append(LikeEscape);
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

}
