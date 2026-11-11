using VehicleVision.PleasanterTools.Core.Cli.Testing;
using VehicleVision.PleasanterTools.IdentityProvider.Data;

namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Tests;

/// <summary>
/// <c>--migrate</c>・<c>--migrate-status</c>（Core.Data の <see cref="MigrationCommand"/>）に、
/// CLI の共通仕様の契約テスト（Core.Cli.Testing）を、このアプリのマイグレーターで当てる。
/// <c>--help</c>・<c>--version</c>・<c>--json</c>・<c>--yes</c>・中断（130）は持たない。
/// </summary>
public sealed class MigrationCommandContractTests : CliContractTests, IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), $"idp-contract-{Guid.NewGuid():N}.db");
    private string? connection;

    private string Fresh => $"Data Source={file};Pooling=False";

    // 存在しないファイルを読み取り専用で開く。外部の DB へは接続しない
    private string Unreachable => Fresh + ";Mode=ReadOnly";

    private static DatabaseMigrator Migrator => IdentityProviderDatabase.Instance.Migrator;

    private Task Reset(string? useConnection = null, bool applied = false)
    {
        Cleanup();
        connection = useConnection ?? Fresh;
        if (applied)
        {
            Migrator.MigrateUp(DatabaseProvider.Sqlite, connection);
        }

        return Task.CompletedTask;
    }

    private void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(file);
        File.Delete(file + ".migration-lock.sqlite");
    }

    private async Task<int> RunCommand(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        TextReader input,
        Func<string, string?> env,
        CancellationToken token) =>
        await MigrationCommand.RunAsync(
            Migrator,
            DatabaseProvider.Sqlite,
            connection ?? Fresh,
            [.. args],
            TimeSpan.FromSeconds(DatabaseStartupMigration.DefaultLockTimeoutSeconds),
            token,
            stdout,
            stderr);

    protected override CliContract CreateContract() => new()
    {
        Name = "MigrationCommand (IdentityProvider)",
        Run = RunCommand,
        Scenarios =
        [
            new CliScenario
            {
                Name = "status-applied",
                Args = ["--migrate-status"],
                Outcome = CliOutcome.Success,
                RequiresOkLine = true,
                Arrange = () => Reset(applied: true),
            },
            // 未適用ありは「検査の失敗」の 1
            new CliScenario
            {
                Name = "status-pending",
                Args = ["--migrate-status"],
                Outcome = CliOutcome.CheckFailed,
                Arrange = () => Reset(),
            },
            new CliScenario
            {
                Name = "migrate-applies",
                Args = ["--migrate"],
                Outcome = CliOutcome.Success,
                RequiresOkLine = true,
                Arrange = () => Reset(),
            },
            // DB の待機期限切れは実行時の失敗の 1（以前の自前の複製は 2 だった）
            new CliScenario
            {
                Name = "wait-timeout",
                Args = ["--migrate", "--wait-for-db=1"],
                Outcome = CliOutcome.Failed,
                Arrange = () => Reset(Unreachable),
            },
        ],
        ArgumentForms =
        [
            new CliArgumentForms
            {
                Name = "wait-for-db",
                Forms = [["--migrate", "--wait-for-db=1"], ["--migrate", "--wait-for-db", "1"]],
                Arrange = () => Reset(Unreachable),
            },
        ],
    };

    public void Dispose() => Cleanup();
}
