namespace VehicleVision.PleasanterTools.IdentityProvider.Data;

/// <summary>起動時マイグレーション完了まで受付と常駐処理を止める状態。</summary>
public sealed class DatabaseStartupState
{
    private readonly TaskCompletionSource ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MigrationStatus? migrationStatus;

    public bool IsReady => ready.Task.IsCompletedSuccessfully;

    public MigrationStatus? MigrationStatus => Volatile.Read(ref migrationStatus);

    public void MarkReady(MigrationStatus? status = null)
    {
        Volatile.Write(ref migrationStatus, status);
        ready.TrySetResult();
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
        ready.Task.WaitAsync(cancellationToken);
}
