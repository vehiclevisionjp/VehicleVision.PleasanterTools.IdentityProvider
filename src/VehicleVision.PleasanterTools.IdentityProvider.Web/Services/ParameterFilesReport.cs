namespace VehicleVision.PleasanterTools.IdentityProvider.Web.Services;

/// <summary>読み込んだ設定ファイルを起動時に記録へ出す。</summary>
/// <remarks>
/// ⚠️ **設定ファイルは <c>optional</c> なので、置き場を間違えても黙って既定で動く。**
/// 実際に「README には JSON で設定すると書いてあるのに、
/// どこからも読まれていない」状態が長く残った。**読めた分を必ず言わせる。**
/// </remarks>
public sealed class ParameterFilesReport(
    IConfiguration configuration,
    ILogger<ParameterFilesReport> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var directory = configuration[ParameterFiles.DirectoryKey];
        var found = configuration[ParameterFiles.FoundFilesKey];

        if (string.IsNullOrEmpty(found))
        {
            // **止めない。** すべて環境変数で与える構成は正しい姿の 1 つ
            logger.LogInformation(
                "設定ファイルは 1 つも見つからなかった（{Directory}）。設定は環境変数だけで決まる。",
                directory);
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "設定ファイルを読んだ（{Directory}）: {Found}", directory, found);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
