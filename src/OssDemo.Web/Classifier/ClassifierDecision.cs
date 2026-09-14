internal sealed record DecisionFact(string Code, FacilityFactState State, string Details);

internal sealed record ClassifierDecision(
    string Code,
    string Outcome,
    IReadOnlyList<DecisionFact> Facts,
    string Reason,
    ClassifierSection Section,
    ClassifierCriterion Criterion,
    int Priority,
    string? HistoryExample = null);
