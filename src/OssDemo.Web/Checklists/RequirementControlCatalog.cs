using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class RequirementControlCatalog
{
    private const int MaximumRequirementsPerControl = 8;

    public static IReadOnlyList<InspectionControl> Build(
        ClassifierTree tree,
        IReadOnlyList<RequirementCatalogItem> requirements,
        IReadOnlySet<string> includedCriterionCodes)
    {
        var criteria = tree.Sections.SelectMany(section => section.Criteria.Select(criterion => (section, criterion)))
            .ToDictionary(item => item.criterion.Code, StringComparer.OrdinalIgnoreCase);
        var assigned = requirements.Select(item => new
        {
            Requirement = item,
            CriterionCode = item.ClassifierCodes.Where(includedCriterionCodes.Contains)
                .OrderBy(CodeOrder).First()
        });

        var controls = new List<InspectionControl>();
        foreach (var group in assigned.GroupBy(item => new
                 {
                     item.CriterionCode,
                     Level = string.Join(" / ", item.Requirement.Levels.Order(StringComparer.OrdinalIgnoreCase)),
                     Topic = item.Requirement.RequirementGroups?.FirstOrDefault()
                         ?? item.Requirement.Groups.FirstOrDefault()
                         ?? "Общие требования"
                 }).OrderBy(item => CodeOrder(item.Key.CriterionCode)).ThenBy(item => item.Key.Level).ThenBy(item => item.Key.Topic))
        {
            var chunks = group.Select(item => item.Requirement).OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Chunk(MaximumRequirementsPerControl).ToArray();
            for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                var (section, criterion) = criteria[group.Key.CriterionCode];
                var topic = TopicNumberRegex().Replace(group.Key.Topic, string.Empty).Trim();
                var suffix = chunks.Length > 1 ? $" Часть {chunkIndex + 1}." : string.Empty;
                var ids = chunk.Select(item => item.Id).ToArray();
                controls.Add(new InspectionControl(
                    StableId(group.Key.CriterionCode, group.Key.Level, group.Key.Topic, ids),
                    controls.Count + 1,
                    section.Code,
                    section.Title,
                    $"{criterion.CheckText} Направление: {topic}.{suffix}".Replace("..", "."),
                    string.Join(Environment.NewLine, chunk.Select(item => item.Basis).Distinct(StringComparer.OrdinalIgnoreCase)),
                    [group.Key.CriterionCode],
                    ids,
                    "verified",
                    "requirements-registry-v2",
                    $"Сформировано из {ids.Length} применимых требований уровня «{group.Key.Level}»."));
            }
        }
        return controls;
    }

    private static (int Section, int Criterion) CodeOrder(string code)
    {
        var parts = code.Split('.');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static string StableId(string code, string level, string topic, IReadOnlyList<string> ids)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{code}\0{level}\0{topic}\0{string.Join(',', ids)}"));
        return $"registry-{Convert.ToHexString(bytes)[..24].ToLowerInvariant()}";
    }

    [GeneratedRegex(@"^\s*\d+\.\s*")]
    private static partial Regex TopicNumberRegex();
}
