internal sealed class AiChecklistBatchWorker(
    AiChecklistAgent agent,
    IAiChecklistRunStore runStore,
    ILogger<AiChecklistBatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await runStore.ResetInterruptedAsync(stoppingToken);
        await Task.WhenAll(RunWorkerAsync(1, stoppingToken), RunWorkerAsync(2, stoppingToken));
    }

    private async Task RunWorkerAsync(int workerNumber, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await agent.ProcessNextBatchAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Фоновый worker ИИ-чек-листов {WorkerNumber} завершил итерацию с ошибкой.", workerNumber);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
