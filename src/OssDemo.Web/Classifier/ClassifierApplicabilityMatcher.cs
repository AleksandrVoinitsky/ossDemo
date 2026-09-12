internal sealed record ChecklistHistoryReference(string CriterionCode, string Title, bool HadNonconformity);

internal sealed record ApplicableClassifierCriterion(
    ClassifierSection Section,
    ClassifierCriterion Criterion,
    string Reason,
    string MatchedField,
    string MatchedValue,
    int Priority);

internal static class ClassifierApplicabilityMatcher
{
    public static IReadOnlyList<ApplicableClassifierCriterion> Match(
        ClassifierTree tree,
        FacilityFacts facts,
        IReadOnlyList<ChecklistHistoryReference> history)
    {
        var result = new List<ApplicableClassifierCriterion>();
        foreach (var section in tree.Sections.OrderBy(item => item.Position))
        foreach (var criterion in section.Criteria.Where(item => item.IsActive).OrderBy(item => item.Position))
        {
            if (criterion.IsBase)
            {
                result.Add(new(section,criterion,"Базовый общеэкологический критерий.","*","always",100));
                continue;
            }
            if (facts.ScheduleCriterionCodes.Contains(criterion.Code))
            {
                result.Add(new(section,criterion,"Критерий указан в охвате проверки.","scheduleCriteria",criterion.Code,95));
                continue;
            }
            var matched = MatchRule(criterion.ApplicabilityRules,facts);
            if (matched is not null)
            {
                var historical = history.Any(item => item.CriterionCode.Equals(criterion.Code,StringComparison.OrdinalIgnoreCase));
                result.Add(new(section,criterion,$"Поле карточки «{matched.Value.Field}» содержит признак «{matched.Value.Value}».",matched.Value.Field,matched.Value.Value,historical?90:80));
                continue;
            }
            var historyItem = history.FirstOrDefault(item => item.CriterionCode.Equals(criterion.Code,StringComparison.OrdinalIgnoreCase));
            if (historyItem is not null)
                result.Add(new(section,criterion,"Критерий применялся в релевантной истории проверок.","history",historyItem.Title,historyItem.HadNonconformity?85:70));
        }
        return result.OrderBy(item=>item.Section.Position).ThenBy(item=>item.Criterion.Position).ToArray();
    }

    private static (string Field,string Value)? MatchRule(IReadOnlyList<ClassifierApplicabilityRule> rules,FacilityFacts facts)
    {
        foreach (var rule in rules)
        {
            if (rule.Operator.Equals("always",StringComparison.OrdinalIgnoreCase)) return ("*","always");
            var names=rule.Field.Split('|',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            foreach(var name in names)
            foreach(var actual in facts.Values(name))
            {
                if(rule.Operator.Equals("equals",StringComparison.OrdinalIgnoreCase) && rule.Values.Any(expected=>actual.Equals(expected,StringComparison.OrdinalIgnoreCase))) return(name,actual);
                if(rule.Operator.Equals("contains-any",StringComparison.OrdinalIgnoreCase) && rule.Values.Any(expected=>actual.Contains(expected,StringComparison.OrdinalIgnoreCase))) return(name,actual);
            }
        }
        return null;
    }
}
