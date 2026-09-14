public sealed record FacilityProfileReadinessResult(
    bool CanFinalizeChecklist,
    IReadOnlyList<string> UnknownFeatureCodes,
    IReadOnlyList<string> Reasons);

public static class FacilityProfileReadiness
{
    public static FacilityProfileReadinessResult Evaluate(FacilityProfileV2 profile)
    {
        var unknown = FacilityProfileV2.RequiredFeatureCodes
            .Where(code => !profile.Features.TryGetValue(code, out var feature) || feature.State == FacilityFactState.Unknown)
            .ToArray();
        var reasons = new List<string>();
        if (!profile.VerificationStatus.Equals("verified", StringComparison.OrdinalIgnoreCase))
            reasons.Add("Карточка не подтверждена инспектором.");
        if (unknown.Length > 0)
            reasons.Add($"Не подтверждены признаки: {string.Join(", ", unknown)}.");
        return new(reasons.Count == 0, unknown, reasons);
    }
}
