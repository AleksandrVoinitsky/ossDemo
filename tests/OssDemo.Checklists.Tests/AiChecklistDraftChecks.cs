internal static class AiChecklistDraftChecks
{
    public static void Run()
    {
        AssertTrue(InspectionBasisDocumentType.TryParse("order", out var order) && order == "order", "Приказ должен приниматься.");
        AssertTrue(InspectionBasisDocumentType.TryParse("directive", out _), "Распоряжение должно приниматься.");
        AssertTrue(InspectionBasisDocumentType.TryParse("license", out _), "Лицензия должна приниматься.");
        AssertTrue(!InspectionBasisDocumentType.TryParse("report", out _), "Отчёт не входит в справочник ОРД.");
        AssertTrue(AiChecklistDraftRules.CanContinue([]), "ОРД не должны блокировать процесс.");
        AssertTrue(AiChecklistDraftRules.IsAllowedFile("основание.pdf", 1024), "PDF должен приниматься.");
        AssertTrue(!AiChecklistDraftRules.IsAllowedFile("script.exe", 1024), "Исполняемый файл должен отклоняться.");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
