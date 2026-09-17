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
    private static readonly IReadOnlyDictionary<string, string[]> CriterionFeatures = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["1.4"] = ["land.disturbance"], ["1.7"] = ["waste.generation"],
        ["2.1"] = ["air.emissions"], ["2.2"] = ["air.emissions"], ["2.3"] = ["air.emissions"],
        ["2.4"] = ["air.gasTreatment"], ["2.5"] = ["air.emissions"], ["2.6"] = ["air.emissions"],
        ["3.1"] = ["water.intake", "water.discharge"], ["3.2"] = ["water.intake"],
        ["3.3"] = ["water.discharge"], ["3.4"] = ["water.discharge"], ["3.5"] = ["water.discharge"],
        ["3.6"] = ["water.intake"], ["3.7"] = ["zone.waterProtection"], ["3.8"] = ["zone.waterProtection"],
        ["3.9"] = ["water.intake", "water.discharge", "water.treatment"],
        ["4.1"] = ["waste.generation"], ["4.2"] = ["waste.disposalSite"], ["4.3"] = ["waste.generation"],
        ["4.4"] = ["waste.disposalSite"], ["4.5"] = ["waste.generation"], ["4.6"] = ["waste.generation"],
        ["4.7"] = ["waste.generation"], ["4.8"] = ["waste.disposalSite"], ["4.9"] = ["waste.disposalSite"],
        ["4.10"] = ["waste.generation", "waste.disposalSite"], ["4.11"] = ["waste.generation"],
        ["4.12"] = ["waste.disposalSite"], ["4.13"] = ["waste.generation"],
        ["5.1"] = ["land.disturbance"], ["5.2"] = ["land.disturbance"], ["5.3"] = ["land.disturbance"], ["5.4"] = ["land.disturbance"],
        ["6.1"] = ["subsoil.wells"], ["6.2"] = ["subsoil.wells"],
        ["7.1"] = ["nature.forest"], ["7.2"] = ["nature.forest"], ["7.3"] = ["nature.forest", "nature.oopt"],
        ["7.4"] = ["nature.oopt"], ["7.5"] = ["zone.waterProtection"], ["7.6"] = ["zone.waterProtection"], ["7.7"] = ["zone.waterProtection"]
    };

    public static IReadOnlyList<ClassifierDecision> Decide(
        ClassifierTree tree,
        FacilityFacts facts,
        IReadOnlyList<ChecklistHistoryReference> history)
    {
        var result = new List<ClassifierDecision>();
        foreach (var section in tree.Sections.OrderBy(item => item.Position))
        foreach (var criterion in section.Criteria.Where(item => item.IsActive).OrderBy(item => item.Position))
        {
            var historyItem = history.FirstOrDefault(item => item.CriterionCode.Equals(criterion.Code, StringComparison.OrdinalIgnoreCase));
            if (criterion.IsBase && criterion.ApplicabilityRules.Any(rule => rule.Operator.Equals("always", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new(criterion.Code, "included", [], "Базовый общеэкологический критерий.", section, criterion, historyItem?.HadNonconformity == true ? 110 : 100, historyItem?.Title));
                continue;
            }
            if (facts.ScheduleCriterionCodes.Contains(criterion.Code))
            {
                result.Add(new(criterion.Code, "included", [], "Критерий явно указан в охвате проверки.", section, criterion, 105, historyItem?.Title));
                continue;
            }
            if (IsMappingCriterion(criterion.ApplicabilityRules))
            {
                var mapping = DecideFromMapping(criterion.ApplicabilityRules, facts);
                result.Add(new(criterion.Code, mapping.Included ? "included" : "excluded", [], mapping.Reason,
                    section, criterion, mapping.Included ? (historyItem is null ? 80 : 90) : 0, historyItem?.Title));
                continue;
            }
            if (!facts.HasStructuredProfile)
            {
                var legacyMatch = MatchRule(criterion.ApplicabilityRules, facts);
                var included = legacyMatch is not null;
                result.Add(new(criterion.Code, included ? "included" : "excluded", [], included
                    ? $"Предварительное legacy-сопоставление: поле «{legacyMatch!.Value.Field}» содержит «{legacyMatch.Value.Value}»."
                    : "В legacy-карточке не найдено основание применимости.", section, criterion, included ? (historyItem is null ? 70 : 80) : 0, historyItem?.Title));
                continue;
            }

            var identity = EvaluateIdentity(criterion.ApplicabilityRules, facts);
            if (identity.Outcome is not null)
            {
                result.Add(new(criterion.Code, identity.Outcome, [], identity.Reason, section, criterion, 0, historyItem?.Title));
                continue;
            }

            if (!CriterionFeatures.TryGetValue(criterion.Code, out var featureCodes))
            {
                result.Add(new(criterion.Code, "included", [], "Применимость подтверждена идентификационными данными объекта.", section, criterion, historyItem is null ? 80 : 90, historyItem?.Title));
                continue;
            }

            var decisionFacts = featureCodes.Select(code =>
            {
                var fact = facts.Feature(code);
                return new DecisionFact(code, fact.State, fact.Details);
            }).ToArray();
            var present = decisionFacts.Where(item => item.State == FacilityFactState.Present).ToArray();
            if (present.Length > 0)
            {
                result.Add(new(criterion.Code, "included", decisionFacts,
                    $"Подтвержден признак: {string.Join(", ", present.Select(item => item.Code))}.", section, criterion, historyItem is null ? 80 : 90, historyItem?.Title));
                continue;
            }
            if (decisionFacts.Any(item => item.State == FacilityFactState.Unknown))
            {
                result.Add(new(criterion.Code, "blocked_unknown", decisionFacts,
                    $"Нужно уточнить признак: {string.Join(", ", decisionFacts.Where(item => item.State == FacilityFactState.Unknown).Select(item => item.Code))}.", section, criterion, 0, historyItem?.Title));
                continue;
            }
            result.Add(new(criterion.Code, "excluded", decisionFacts,
                $"Подтверждено отсутствие признаков: {string.Join(", ", featureCodes)}.", section, criterion, 0, historyItem?.Title));
        }
        return result;
    }

    private static bool IsMappingCriterion(IReadOnlyList<ClassifierApplicabilityRule> rules) =>
        rules.Any(rule => rule.Field.Equals("category", StringComparison.OrdinalIgnoreCase))
        && rules.Any(rule => rule.Field.Equals("region", StringComparison.OrdinalIgnoreCase));

    private static (bool Included, string Reason) DecideFromMapping(
        IReadOnlyList<ClassifierApplicabilityRule> rules,
        FacilityFacts facts)
    {
        foreach (var field in new[] { "category", "region", "type" })
        {
            var rule = rules.FirstOrDefault(item => item.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (rule is null) continue;
            var match = MatchValues(rule, facts);
            if (match is null)
                return (false, facts.Values(field).Count == 0
                    ? $"Поле карточки «{field}» не заполнено."
                    : $"Поле карточки «{field}» не соответствует mapping.");
        }

        var aspectRule = rules.FirstOrDefault(item => item.Field.Equals("environmentalAspects", StringComparison.OrdinalIgnoreCase));
        var aspectMatch = aspectRule is null ? null : MatchValues(aspectRule, facts);
        if (aspectRule is not null && aspectMatch is null)
            return (false, "Экологические аспекты карточки не соответствуют mapping.");

        var specificRules = rules.Where(item => item.Field.Equals("zones", StringComparison.OrdinalIgnoreCase)
            || item.Field.Equals("equipment", StringComparison.OrdinalIgnoreCase)
            || item.Field.Equals("specialZones", StringComparison.OrdinalIgnoreCase)).ToArray();
        var specificMatch = specificRules.Select(rule => MatchValues(rule, facts)).FirstOrDefault(match => match is not null);
        if (specificRules.Length > 0 && specificMatch is null)
            return (false, "Зоны, оборудование и особые природные зоны карточки не соответствуют mapping.");

        var evidence = specificMatch ?? aspectMatch;
        return evidence is null
            ? (true, "Идентификационные поля карточки соответствуют mapping.")
            : (true, $"Mapping: поле «{evidence.Value.Field}» содержит «{evidence.Value.Value}».");
    }

    public static IReadOnlyList<ApplicableClassifierCriterion> Match(
        ClassifierTree tree,
        FacilityFacts facts,
        IReadOnlyList<ChecklistHistoryReference> history)
        => Match(Decide(tree, facts, history), facts);

    public static IReadOnlyList<ApplicableClassifierCriterion> Match(
        IReadOnlyList<ClassifierDecision> decisions,
        FacilityFacts facts)
    {
        return decisions.Where(item => item.Outcome == "included").Select(item =>
        {
            var fact = item.Facts.FirstOrDefault(value => value.State == FacilityFactState.Present);
            return new ApplicableClassifierCriterion(item.Section, item.Criterion, item.Reason,
                fact?.Code ?? (facts.ScheduleCriterionCodes.Contains(item.Code) ? "scheduleCriteria" : "*"),
                fact?.Details ?? item.Code, item.Priority, item.HistoryExample);
        }).ToArray();
    }

    private static (string? Outcome, string Reason) EvaluateIdentity(IReadOnlyList<ClassifierApplicabilityRule> rules, FacilityFacts facts)
    {
        foreach (var rule in rules.Where(rule => rule.Field.Equals("category", StringComparison.OrdinalIgnoreCase)
            || rule.Field.Equals("region", StringComparison.OrdinalIgnoreCase)
            || rule.Field.Equals("type", StringComparison.OrdinalIgnoreCase)))
        {
            var actual = facts.Values(rule.Field).ToArray();
            if (actual.Length == 0)
            {
                if (rule.Operator.Equals("required-any", StringComparison.OrdinalIgnoreCase))
                    return ("blocked_unknown", $"Не заполнено обязательное идентификационное поле «{rule.Field}».");
                continue;
            }
            if (!actual.Any(value => rule.Values.Any(expected => ContainsExpected(value, expected))))
                return ("excluded", $"Значение поля «{rule.Field}» не входит в область применимости критерия.");
        }
        return (null, "");
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
                && rule.Values.Any(expected=>ContainsExpected(actual,expected))) return(name,actual);
        }
        return null;
    }

    private static bool ContainsExpected(string actual,string expected)
    {
        if (expected.ToLowerInvariant() is "i" or "ii" or "iii" or "iv" or "v" or "да" or "нет")
            return ContainsWholeToken(actual,expected);
        var normalizedActual = NormalizeComparable(actual);
        var normalizedExpected = NormalizeComparable(expected);
        if (normalizedActual.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase)
            || normalizedExpected.Contains(normalizedActual, StringComparison.OrdinalIgnoreCase)) return true;
        var expectedWords = normalizedExpected.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 5).ToArray();
        var actualWords = normalizedActual.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return expectedWords.Length > 0 && expectedWords.All(expectedWord =>
            actualWords.Any(actualWord => actualWord.StartsWith(expectedWord[..Math.Min(6, expectedWord.Length)], StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeComparable(string value)
    {
        var characters = value.Trim().ToLowerInvariant().Replace('ё', 'е')
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray();
        return string.Join(' ', new string(characters).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
