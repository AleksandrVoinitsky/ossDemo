internal static class AiChecklistRunChecks
{
    public static async Task RunAsync()
    {
        var store = new InMemoryAiChecklistRunStore();
        var profile = new FacilityProfile("test", new FacilityProfileFields { ShortName = "Объект", Category = "I" }, null, null);
        var evidence = new[] { new AiChecklistEvidence("S1", "ПЭК", "ФЗ-7", "67", "Проверить программу ПЭК", .9) };
        var run = await store.CreateAsync(profile, Guid.NewGuid(), "Объект", evidence, AiChecklistBatchPlanner.Build(evidence), CancellationToken.None);
        AssertEqual("pending", run.Batches[0].Status);
        AssertTrue(await store.QueueBatchAsync(run.Id, 0, CancellationToken.None));
        AssertTrue(await store.QueueBatchAsync(run.Id, 0, CancellationToken.None), "Повторная постановка должна быть идемпотентной.");
        var work = await store.ClaimNextBatchAsync(CancellationToken.None);
        AssertEqual(run.Id, work!.RunId);
        AssertEqual(1, work.Evidence.Count);
        await store.FailBatchAsync(run.Id, 0, "Ошибка", 1200, CancellationToken.None);
        AssertTrue(await store.QueueBatchAsync(run.Id, 0, CancellationToken.None), "Упавший пакет можно повторить.");
        work = await store.ClaimNextBatchAsync(CancellationToken.None);
        await store.CompleteBatchAsync(run.Id, 0, [new("ПЭК", "Проверить программу ПЭК", "Основание", .9, [new("S1", "Проверить программу ПЭК")])], 900, CancellationToken.None);
        var completed = await store.GetAsync(run.Id, CancellationToken.None);
        AssertEqual("completed", completed!.Batches[0].Status);
        AssertEqual(1, completed.Batches[0].ItemCount);
        AssertTrue(await store.BeginFinalizeAsync(run.Id, CancellationToken.None));
        AssertTrue(await store.BeginFinalizeAsync(run.Id, CancellationToken.None), "Повторная финализация должна быть идемпотентно допустима.");
        AssertTrue(!await store.QueueBatchAsync(run.Id, 0, CancellationToken.None), "Во время финализации пакет нельзя вернуть в очередь.");

        var emptyRun = await store.CreateAsync(profile, Guid.NewGuid(), "Объект", evidence, AiChecklistBatchPlanner.Build(evidence), CancellationToken.None);
        await store.QueueBatchAsync(emptyRun.Id, 0, CancellationToken.None);
        await store.ClaimNextBatchAsync(CancellationToken.None);
        await store.CompleteBatchAsync(emptyRun.Id, 0, [], 500, CancellationToken.None);
        AssertTrue(await store.QueueBatchAsync(emptyRun.Id, 0, CancellationToken.None), "Пакет без принятых пунктов можно поставить на повтор.");
        AssertEqual("queued", (await store.GetAsync(emptyRun.Id, CancellationToken.None))!.Batches[0].Status);

        var twoEvidence = new[]
        {
            new AiChecklistEvidence("M1", "ПЭК", "Документ", "1", "Первый пункт", .9),
            new AiChecklistEvidence("M2", "Отходы", "Документ", "2", "Второй пункт", .8)
        };
        var multiRun = await store.CreateAsync(profile, Guid.NewGuid(), "Объект", twoEvidence, AiChecklistBatchPlanner.Build(twoEvidence), CancellationToken.None);
        AssertTrue(await store.QueueBatchesAsync(multiRun.Id, [0, 1], CancellationToken.None), "Все пакеты запуска должны ставиться в очередь одной операцией.");
        AssertTrue((await store.GetAsync(multiRun.Id, CancellationToken.None))!.Batches.All(item => item.Status == "queued"));
        AssertTrue(await store.QueueBatchesAsync(multiRun.Id, [], CancellationToken.None), "Повторный запуск без новых пакетов должен быть идемпотентным.");

        var criterionPlan = new AiChecklistBatchPlan(0, "Очистные сооружения", [], 0,
            ["3.9"], "Карточка содержит КОС", "эксплуатация очистных сооружений КОС", "Проверить эксплуатацию очистных сооружений.", "Охрана водных объектов");
        var criterionStore = new InMemoryAiChecklistRunStore();
        var criterionRun = await criterionStore.CreateAsync(profile, Guid.NewGuid(), "Объект", [], [criterionPlan], CancellationToken.None);
        await criterionStore.QueueBatchAsync(criterionRun.Id, 0, CancellationToken.None);
        var criterionWork = await criterionStore.ClaimNextBatchAsync(CancellationToken.None);
        AssertEqual("3.9", criterionWork!.Batch.CriterionCodes![0]);
        AssertEqual("эксплуатация очистных сооружений КОС", criterionWork.Batch.Query);

        var stopStore = new InMemoryAiChecklistRunStore();
        var stopRun = await stopStore.CreateAsync(profile, Guid.NewGuid(), "Объект", twoEvidence, AiChecklistBatchPlanner.Build(twoEvidence), CancellationToken.None);
        await stopStore.QueueBatchesAsync(stopRun.Id, stopRun.Batches.Select(item => item.Index).ToArray(), CancellationToken.None);
        var runningAtStop = await stopStore.ClaimNextBatchAsync(CancellationToken.None);
        AssertTrue(runningAtStop is not null, "Перед остановкой один пакет должен выполняться.");
        AssertTrue(await stopStore.StopAsync(stopRun.Id, CancellationToken.None), "Активный запуск должен принимать запрос остановки.");
        var stopping = await stopStore.GetAsync(stopRun.Id, CancellationToken.None);
        AssertEqual("stopping", stopping!.Status);
        AssertTrue(stopping.Batches.Where(item => item.Index != runningAtStop!.Batch.Index).All(item => item.Status == "skipped"), "Не начатые критерии должны перейти в базовые пункты.");
        AssertTrue(await stopStore.ClaimNextBatchAsync(CancellationToken.None) is null, "После остановки новые критерии не должны запускаться.");
        await stopStore.CompleteBatchAsync(stopRun.Id, runningAtStop!.Batch.Index, [], 250, CancellationToken.None);
        var stopped = await stopStore.GetAsync(stopRun.Id, CancellationToken.None);
        AssertEqual("stopped", stopped!.Status);
        AssertTrue(await stopStore.BeginFinalizeAsync(stopRun.Id, CancellationToken.None), "Остановленный запуск должен разрешать создание черновика.");
    }

    private static void AssertTrue(bool value, string message = "Expected true.") { if (!value) throw new InvalidOperationException(message); }
    private static void AssertEqual<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'."); }
}
