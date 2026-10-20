namespace ScreenshotApi;

public sealed class FileRetention(FileStorage storage, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sweep at startup, then once an hour; only the configured dedicated directory.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                storage.SweepExpired(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(1), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
