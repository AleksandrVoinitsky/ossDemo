public sealed record FacilityDictionaryItem(string Code, string Label, string Description = "");

public static class FacilityProfileDictionaries
{
    public static readonly IReadOnlyList<FacilityDictionaryItem> States =
    [
        new("unknown", "Не установлено", "Требуется уточнить до формирования итогового чек-листа."),
        new("present", "Есть", "Признак подтвержден для объекта."),
        new("absent", "Нет", "Отсутствие признака подтверждено для объекта.")
    ];

    public static readonly IReadOnlyList<FacilityDictionaryItem> Features =
    [
        new("air.emissions", "Источники выбросов", "Есть стационарные источники выбросов в атмосферный воздух."),
        new("air.gasTreatment", "Газоочистные установки", "Эксплуатируются установки очистки газа."),
        new("water.intake", "Забор воды", "Осуществляется забор воды из природных источников или скважин."),
        new("water.discharge", "Сброс сточных вод", "Осуществляется сброс сточных вод в водный объект или систему водоотведения."),
        new("water.treatment", "Очистка сточных вод", "Эксплуатируются очистные сооружения."),
        new("waste.generation", "Образование отходов", "При деятельности образуются отходы производства и потребления."),
        new("waste.disposalSite", "Объект размещения отходов", "На объекте есть полигон или иной объект размещения отходов."),
        new("land.disturbance", "Нарушение земель", "Работы связаны с нарушением почвенного покрова или рекультивацией."),
        new("subsoil.wells", "Скважины и недропользование", "Есть скважины или лицензируемое пользование недрами."),
        new("nature.forest", "Лесные участки", "Объект или работы затрагивают земли лесного фонда."),
        new("nature.oopt", "Особо охраняемые территории", "Объект расположен на ООПТ или влияет на нее."),
        new("zone.waterProtection", "Водоохранная зона", "Объект расположен в водоохранной или прибрежной защитной зоне.")
    ];

    public static FacilityFactState ParseState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "present" => FacilityFactState.Present,
        "absent" => FacilityFactState.Absent,
        _ => FacilityFactState.Unknown
    };
}
