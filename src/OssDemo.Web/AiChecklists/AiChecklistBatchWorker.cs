internal sealed class AiChecklistBatchWorker(
    AiChecklistAgent agent,
    IAiChecklistRunStore runStore,
    ILogger<AiChecklistBatchWorker> logger) : BackgroundService
{
    internal const int WorkerCount = 1;
    internal static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await runStore.ResetInterruptedAsync(stoppingToken);
        await RunWorkerAsync(stoppingToken);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await agent.ProcessNextBatchAsync(stoppingToken))
                    await Task.Delay(IdlePollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Фоновый worker ИИ-чек-листов завершил итерацию с ошибкой.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
