internal sealed record ClassifierApplicabilityRule(
    string Field,
    string Operator,
    IReadOnlyList<string> Values);

internal sealed record ClassifierCriterionWrite(
    string? RiskText,
    string? CheckText,
    string? SearchTerms,
    IReadOnlyList<ClassifierApplicabilityRule> ApplicabilityRules,
    IReadOnlyList<string> SourceHints,
    bool IsActive,
    int Position);

internal sealed record ClassifierCriterionCreate(
    Guid SectionId,
    string? Code,
    string? RiskText,
    string? CheckText,
    string? SearchTerms,
    IReadOnlyList<ClassifierApplicabilityRule> ApplicabilityRules,
    IReadOnlyList<string> SourceHints,
    bool IsActive,
    int Position);

internal sealed record ClassifierCriterion(
    Guid Id,
    string Code,
    string RiskText,
    string CheckText,
    string SearchTerms,
    IReadOnlyList<ClassifierApplicabilityRule> ApplicabilityRules,
    IReadOnlyList<string> SourceHints,
    bool IsBase,
    bool IsActive,
    int Position,
    int HistoryUsageCount,
    IReadOnlyList<string> RecentSources);

internal sealed record ClassifierSection(
    Guid Id,
    string Code,
    string Title,
    int Position,
    IReadOnlyList<ClassifierCriterion> Criteria);

internal sealed record ClassifierTree(
    Guid VersionId,
    string Version,
    string Status,
    DateOnly EffectiveFrom,
    IReadOnlyList<ClassifierSection> Sections);

internal sealed record ClassifierSeedCriterion(string Code, string RiskText);
internal sealed record ClassifierSeedSection(string Code, string Title, IReadOnlyList<ClassifierSeedCriterion> Criteria);

internal static class ClassifierRules
{
    public static ChecklistOperationResult<ClassifierCriterionWrite> Normalize(ClassifierCriterionWrite request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.RiskText)) errors["riskText"] = ["Укажите формулировку риска."];
        if (string.IsNullOrWhiteSpace(request.CheckText)) errors["checkText"] = ["Укажите формулировку проверки."];
        if (errors.Count > 0)
            return ChecklistOperationResult<ClassifierCriterionWrite>.Fail("validation", "Проверьте поля критерия.", errors);

        var normalized = request with
        {
            RiskText = request.RiskText!.Trim(),
            CheckText = request.CheckText!.Trim(),
            SearchTerms = request.SearchTerms?.Trim() ?? string.Empty,
            ApplicabilityRules = request.ApplicabilityRules
                .Where(rule => !string.IsNullOrWhiteSpace(rule.Field) && !string.IsNullOrWhiteSpace(rule.Operator))
                .Select(rule => rule with
                {
                    Field = rule.Field.Trim(),
                    Operator = rule.Operator.Trim().ToLowerInvariant(),
                    Values = rule.Values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                }).ToArray(),
            SourceHints = request.SourceHints.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Position = Math.Max(0, request.Position)
        };
        return ChecklistOperationResult<ClassifierCriterionWrite>.Success(normalized);
    }
}
