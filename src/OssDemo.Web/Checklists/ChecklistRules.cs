internal static class ChecklistRules
{
    public static ChecklistOperationResult<ChecklistTemplateWriteRequest> NormalizeTemplate(ChecklistTemplateWriteRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) errors["name"] = ["Укажите название шаблона."];
        if (request.FacilityId == Guid.Empty) errors["facilityId"] = ["Выберите объект проверки."];

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
            new ChecklistTemplateWriteRequest(name, request.FacilityId, request.Version, sections));
    }
}
