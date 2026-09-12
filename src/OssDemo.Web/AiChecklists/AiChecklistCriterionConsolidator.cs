internal static class AiChecklistCriterionConsolidator
{
    private static readonly (string[] Codes,string Title)[] Groups =
    [
        (["1.5","1.8"], "Проверить полноту, достоверность и своевременность представления экологической и статистической отчётности."),
        (["2.2","2.3"], "Проверить наличие действующего разрешения на выбросы и соблюдение установленных в нём условий."),
        (["3.1","3.2"], "Проверить наличие документов на водопользование и соответствие фактического водозабора и сброса установленным условиям."),
        (["3.3","3.4"], "Проверить организацию контроля сточных вод и соблюдение установленных нормативов допустимых сбросов."),
        (["4.2","4.3","4.4"], "Проверить наличие нормативов образования отходов и лимитов на их размещение, а также соблюдение установленных лимитов и требований к размещению."),
        (["4.6","4.7"], "Проверить полноту, достоверность и своевременность учёта образования и движения отходов."),
        (["5.2","5.3"], "Проверить наличие согласованного проекта рекультивации и выполнение предусмотренных им работ в полном объёме."),
        (["7.4","7.5","7.6","7.7"], "Проверить выполнение требований и согласованных мер по сохранению животного мира, водных биоресурсов и среды их обитания.")
    ];

    public static IReadOnlyList<AiGeneratedDraftItem> Consolidate(IReadOnlyList<AiChecklistBatchState> batches)
    {
        var byCode = batches
            .SelectMany(batch => (batch.CriterionCodes ?? []).Select(code => (Code:code,Batch:batch)))
            .GroupBy(item=>item.Code,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group=>group.Key,group=>group.First().Batch,StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AiGeneratedDraftItem>();
        foreach (var group in Groups)
        {
            var available=group.Codes.Where(byCode.ContainsKey).ToArray();
            if(available.Length<2) continue;
            result.Add(Build(available.Select(code=>byCode[code]).ToArray(),available,group.Title));
            consumed.UnionWith(available);
        }
        foreach(var pair in byCode.Where(pair=>!consumed.Contains(pair.Key)).OrderBy(pair=>CodeSort(pair.Key)))
            result.Add(Build([pair.Value],[pair.Key],null));
        return result.OrderBy(item=>CodeSort(item.CriterionCodes?.FirstOrDefault() ?? "99.99")).ToArray();
    }

    private static AiGeneratedDraftItem Build(IReadOnlyList<AiChecklistBatchState> batches,IReadOnlyList<string> codes,string? groupedTitle)
    {
        var best=batches.SelectMany(batch=>batch.Items).OrderByDescending(item=>item.Confidence).ThenBy(item=>item.Title.Length).FirstOrDefault();
        var fallback=batches.Select(batch=>batch.FallbackTitle).FirstOrDefault(value=>!string.IsNullOrWhiteSpace(value)) ?? "Проверить соблюдение применимого критерия классификатора.";
        var candidate=best is not null && best.Title.Length<=280 ? best.Title : fallback;
        var title=groupedTitle ?? EnsureAction(candidate);
        var evidence=batches.SelectMany(batch=>batch.BatchEvidence ?? []).GroupBy(item=>item.Id,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.OrdinalIgnoreCase);
        var sources=batches.SelectMany(batch=>batch.Items).SelectMany(item=>item.Citations).Select(citation=>evidence.GetValueOrDefault(citation.SourceId))
            .Where(item=>item is not null).Select(item=>CompactSource(item!)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        var basis=sources.Length==0?"Основание требует проверки по актуальной базе знаний.":string.Join("; ",sources);
        var reasons=batches.Select(batch=>batch.ApplicabilityReason).Where(value=>!string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase);
        var section=batches.Select(batch=>batch.Section).FirstOrDefault(value=>!string.IsNullOrWhiteSpace(value)) ?? "Экологический контроль";
        var codeText=string.Join(", ",codes.OrderBy(CodeSort));
        return new(section!,title,basis,$"Применимость: {string.Join(" ",reasons)}",codes.ToArray(),$"Классификатор {codeText} + база знаний");
    }

    private static string EnsureAction(string value)
    {
        var text=value.Trim();
        if(text.StartsWith("Проверить",StringComparison.OrdinalIgnoreCase)) return text.EndsWith('.')?text:text+".";
        return $"Проверить {char.ToLowerInvariant(text[0])}{text[1..].TrimEnd('.')}.";
    }

    private static string CompactSource(AiChecklistEvidence evidence)
    {
        var value=string.IsNullOrWhiteSpace(evidence.SourceLabel)?evidence.DocumentTitle:$"{evidence.DocumentTitle}, {evidence.SourceLabel}";
        return value.Length<=180?value:value[..177]+"…";
    }

    private static int CodeSort(string code)
    {
        var parts=code.Split('.');
        return (int.TryParse(parts.ElementAtOrDefault(0),out var section)?section:99)*100+(int.TryParse(parts.ElementAtOrDefault(1),out var item)?item:99);
    }
}
