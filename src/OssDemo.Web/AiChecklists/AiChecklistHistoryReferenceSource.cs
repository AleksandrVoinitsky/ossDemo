using System.Text.RegularExpressions;
using Npgsql;

internal interface IAiChecklistHistoryReferenceSource
{
    Task<IReadOnlyList<ChecklistHistoryReference>> GetAsync(string facilityName, ClassifierTree tree, CancellationToken cancellationToken);
}

internal sealed partial class AiChecklistHistoryReferenceSource(IConfiguration configuration) : IAiChecklistHistoryReferenceSource
{
    public async Task<IReadOnlyList<ChecklistHistoryReference>> GetAsync(string facilityName, ClassifierTree tree, CancellationToken cancellationToken)
    {
        await using var connection=new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));
        await connection.OpenAsync(cancellationToken);
        await using var command=new NpgsqlCommand("""
            SELECT i.section,i.title,i.nonconformity FROM app_checklists c
            JOIN app_checklist_items i ON i.checklist_id=c.id
            WHERE c.status='approved' AND lower(c.facility_name)=lower(@facility)
            ORDER BY c.approved_at DESC NULLS LAST,i.position LIMIT 400
            """,connection);
        command.Parameters.AddWithValue("facility",facilityName);
        var result=new List<ChecklistHistoryReference>();
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))
        {
            var section=reader.GetString(0); var title=reader.GetString(1); var nonconformity=reader.GetString(2);
            var sectionCode=SectionCode(section); var candidates=tree.Sections.Where(item=>sectionCode is null || item.Code==sectionCode).SelectMany(item=>item.Criteria);
            var titleTerms=Terms(title);
            var match=candidates.Select(criterion=>(Criterion:criterion,Score:Terms(criterion.RiskText+" "+criterion.CheckText).Intersect(titleTerms).Count()))
                .OrderByDescending(item=>item.Score).FirstOrDefault();
            if(match.Criterion is not null && match.Score>=2) result.Add(new(match.Criterion.Code,title,!string.IsNullOrWhiteSpace(nonconformity)));
        }
        return result.GroupBy(item=>item.CriterionCode,StringComparer.OrdinalIgnoreCase).Select(group=>group.OrderByDescending(item=>item.HadNonconformity).First()).ToArray();
    }

    private static string? SectionCode(string value)
    {
        var text=value.ToLowerInvariant();
        if(text.Contains("атмосфер")) return "2"; if(text.Contains("вод")) return "3"; if(text.Contains("отход")) return "4";
        if(text.Contains("зем")) return "5"; if(text.Contains("недр")) return "6"; if(text.Contains("живот")||text.Contains("растит")||text.Contains("лес")) return "7";
        if(text.Contains("общ")) return "1"; return null;
    }

    private static HashSet<string> Terms(string value)=>WordRegex().Matches(value.ToLowerInvariant().Replace('ё','е')).Select(match=>match.Value).Where(term=>term.Length>=5).ToHashSet(StringComparer.OrdinalIgnoreCase);
    [GeneratedRegex(@"[а-яa-z0-9]+")]
    private static partial Regex WordRegex();
}
