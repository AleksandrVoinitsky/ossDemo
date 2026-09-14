internal sealed record ChecklistHistoryReference(string CriterionCode, string Title, bool HadNonconformity);

internal sealed record ApplicableClassifierCriterion(
    ClassifierSection Section,
    ClassifierCriterion Criterion,
    string Reason,
    string MatchedField,
    string MatchedValue,
    int Priority,
    string? HistoryExample = null);

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
            if (criterion.IsBase && criterion.ApplicabilityRules.Any(rule => rule.Operator.Equals("always",StringComparison.OrdinalIgnoreCase)))
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
                var historyExample=history.FirstOrDefault(item=>item.CriterionCode.Equals(criterion.Code,StringComparison.OrdinalIgnoreCase));
                result.Add(new(section,criterion,$"Поле карточки «{matched.Value.Field}» содержит признак «{matched.Value.Value}».",matched.Value.Field,matched.Value.Value,historical?90:80,historyExample?.Title));
                continue;
            }
            var historyItem = history.FirstOrDefault(item => item.CriterionCode.Equals(criterion.Code,StringComparison.OrdinalIgnoreCase));
            if (historyItem is not null)
                result.Add(new(section,criterion,"Критерий применялся в релевантной истории проверок.","history",historyItem.Title,historyItem.HadNonconformity?85:70,historyItem.Title));
        }
        return result.OrderBy(item=>item.Section.Position).ThenBy(item=>item.Criterion.Position).ToArray();
    }

    private static (string Field,string Value)? MatchRule(IReadOnlyList<ClassifierApplicabilityRule> rules,FacilityFacts facts)
    {
        var required=rules.Where(rule=>rule.Operator.Equals("required-any",StringComparison.OrdinalIgnoreCase)).ToArray();
        (string Field,string Value)? requiredMatch=null;
        foreach(var rule in required)
        {
            var match=MatchValues(rule,facts);
            if(match is null) return null;
            requiredMatch??=match;
        }
        var triggers=rules.Where(rule=>!rule.Operator.Equals("required-any",StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach(var rule in triggers)
        {
            if (rule.Operator.Equals("always",StringComparison.OrdinalIgnoreCase)) return ("*","always");
            var match=MatchValues(rule,facts);
            if(match is not null) return match;
        }
        return triggers.Length==0?requiredMatch:null;
    }

    private static (string Field,string Value)? MatchValues(ClassifierApplicabilityRule rule,FacilityFacts facts)
    {
        var names=rule.Field.Split('|',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        foreach(var name in names)
        foreach(var actual in facts.Values(name))
        {
            if(rule.Operator.Equals("equals",StringComparison.OrdinalIgnoreCase) && rule.Values.Any(expected=>actual.Equals(expected,StringComparison.OrdinalIgnoreCase))) return(name,actual);
            if((rule.Operator.Equals("contains-any",StringComparison.OrdinalIgnoreCase) || rule.Operator.Equals("required-any",StringComparison.OrdinalIgnoreCase))
                && rule.Values.Any(expected=>ContainsExpected(actual,expected) || ContainsExpected(expected,actual))) return(name,actual);
        }
        return null;
    }

    private static bool ContainsExpected(string actual,string expected)
    {
        if (expected.ToLowerInvariant() is "i" or "ii" or "iii" or "iv" or "v" or "да" or "нет")
            return ContainsWholeToken(actual,expected);
        return actual.Contains(expected,StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWholeToken(string actual,string expected)
    {
        var start=0;
        while ((start=actual.IndexOf(expected,start,StringComparison.OrdinalIgnoreCase))>=0)
        {
            var before=start==0 || !char.IsLetterOrDigit(actual[start-1]);
            var end=start+expected.Length;
            var after=end==actual.Length || !char.IsLetterOrDigit(actual[end]);
            if(before && after) return true;
            start++;
        }
        return false;
    }
}
