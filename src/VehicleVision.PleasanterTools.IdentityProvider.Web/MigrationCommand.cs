using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web;

/// <summary>コマンドラインからマイグレーションを当てる／状態を見る。</summary>
/// <remarks>
/// <para>
/// 自動適用を無効にした導入先のため、当てる口をアプリ本体と同じ実行ファイルに持たせる。
/// 別の道具にすると接続文字列の読み方が二重になり、片方だけ直す事故が起きる。
/// </para>
/// <para>
///   当てる:   <c>dotnet run --project src/…Web -- --migrate</c><br/>
///   見るだけ: <c>dotnet run --project src/…Web -- --migrate-status</c>
/// </para>
/// <para>
/// **公開済みのイメージからも同じことができる**（<c>dotnet …Web.dll --migrate</c>）。
/// デプロイ手順とコンテナの起動順の両方で同じ口を使う。
/// </para>
/// <para>
/// **コンソールへ出す文字列は英語で書くこと。** Azure の Kudu の Debug console では
/// 日本語が化け、「当たった」のか「当たっていない」のかを運用者が読めない。
/// コメントとドキュメントは日本語のままでよい。
/// </para>
/// </remarks>
public static class MigrationCommand
{
    /// <summary>コマンドラインの合図。</summary>
    public const string ApplyArgument = "--migrate";

    /// <summary>当てずに状態だけ見る合図。</summary>
    public const string StatusArgument = "--migrate-status";

    /// <summary>この起動がマイグレーションのためのものか。</summary>
    public static bool IsRequested(string[] args) =>
        args.Contains(ApplyArgument, StringComparer.Ordinal)
        || args.Contains(StatusArgument, StringComparer.Ordinal);

    /// <summary>当てる、または状態を見る。</summary>
    /// <returns>終了コード。0 なら「当たっている」。</returns>
    public static async Task<int> RunAsync(
        DatabaseProvider provider,
        string connectionString,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var apply = args.Contains(ApplyArgument, StringComparer.Ordinal);

        // **DB より先にこちらが動き出すことがある**（コンテナで一緒に立ち上げたとき）。
        // 既定は待たない。設定の誤りを起動の遅さで隠さないため
        var wait = WaitFor(args);
        if (wait > TimeSpan.Zero)
        {
            Console.WriteLine($"Waiting for the database (up to {wait.TotalSeconds:0}s)...");
            var failure = await IdentityProviderDatabase.Instance.Migrator
                .WaitForDatabaseAsync(provider, connectionString, wait, cancellationToken)
                .ConfigureAwait(false);

            if (failure is not null)
            {
                await Console.Error.WriteLineAsync(
                    $"Database not reachable after {wait.TotalSeconds:0}s: {failure.Message}")
                    .ConfigureAwait(false);
                return 2;
            }
        }

        var pending = IdentityProviderDatabase.Instance.Migrator.PendingMigrations(provider, connectionString);
        if (pending.Count == 0)
        {
            Console.WriteLine($"All migrations are applied ({provider}).");
            return 0;
        }

        Console.WriteLine($"Pending migrations ({provider}):");
        foreach (var migration in pending)
        {
            Console.WriteLine($"  - {migration}");
        }

        if (!apply)
        {
            // **見るだけのときは 0 を返さない。** CI や起動前の確認で気付けるようにする
            return 1;
        }

        var result = await IdentityProviderDatabase.Instance.Migrator.MigrateUpWithLockAsync(
            provider,
            connectionString,
            TimeSpan.FromSeconds(DatabaseStartupMigration.DefaultLockTimeoutSeconds),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Applied {result.AppliedCount} migration(s).");
        return 0;
    }

    /// <summary><c>--wait-for-db=90</c> の秒数。無ければ待たない。</summary>
    private static TimeSpan WaitFor(string[] args)
    {
        const string prefix = "--wait-for-db=";

        var argument = args.FirstOrDefault(
            value => value.StartsWith(prefix, StringComparison.Ordinal));

        return argument is not null
            && int.TryParse(argument[prefix.Length..], out var seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
    }
}
