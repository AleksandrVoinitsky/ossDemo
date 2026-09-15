internal static class ChecklistRules
{
    private static readonly string[] UnverifiedBasisMarkers =
    [
        "требует проверки",
        "не найден",
        "не найдено",
        "заполнить вручную",
        "уточнить основание"
    ];

    public static bool RequiresBasisReview(string? basis) =>
        string.IsNullOrWhiteSpace(basis) || UnverifiedBasisMarkers.Any(marker => basis.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static ChecklistOperationResult<ChecklistTemplateWriteRequest> NormalizeTemplate(ChecklistTemplateWriteRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) errors["name"] = ["Укажите название шаблона."];
        if (name.Contains("ЛПУМГ", StringComparison.OrdinalIgnoreCase))
            errors["name"] = ["Системный шаблон не должен содержать название конкретного объекта."];

        var sections = (request.Sections ?? [])
            .Select((section, sectionIndex) => new ChecklistTemplateSectionWrite(
                section.Title?.Trim(),
                sectionIndex + 1,
                (section.Items ?? []).Select((item, itemIndex) => new ChecklistTemplateItemWrite(
                    item.Title?.Trim(),
                    item.Basis?.Trim(),
                    item.Note?.Trim(),
                    itemIndex + 1)).ToArray()))
            .ToArray();

        if (sections.Length == 0)
        {
            errors["sections"] = ["Добавьте хотя бы один раздел с пунктом."];
        }

        for (var sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            var section = sections[sectionIndex];
            if (string.IsNullOrWhiteSpace(section.Title))
                errors[$"sections.{sectionIndex}.title"] = ["Укажите название раздела."];
            if (section.Items.Count == 0)
                errors[$"sections.{sectionIndex}.items"] = ["Добавьте хотя бы один пункт."];

            for (var itemIndex = 0; itemIndex < section.Items.Count; itemIndex++)
            {
                var item = section.Items[itemIndex];
                if (string.IsNullOrWhiteSpace(item.Title))
                    errors[$"sections.{sectionIndex}.items.{itemIndex}.title"] = ["Укажите контролируемую позицию."];
                if (string.IsNullOrWhiteSpace(item.Basis))
                    errors[$"sections.{sectionIndex}.items.{itemIndex}.basis"] = ["Укажите основание."];
            }
        }

        if (errors.Count > 0)
            return ChecklistOperationResult<ChecklistTemplateWriteRequest>.Fail("validation", "Проверьте поля шаблона.", errors);

        return ChecklistOperationResult<ChecklistTemplateWriteRequest>.Success(
            new ChecklistTemplateWriteRequest(name, request.FacilityId, request.Version, sections,
                request.Header is null ? null : new ChecklistTemplateHeader(
                    request.Header.Title?.Trim(), request.Header.ApprovalBlock?.Trim(), request.Header.IntroText?.Trim())));
    }
}
