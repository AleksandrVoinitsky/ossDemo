internal static class FacilityProfileSeedData
{
    public static void FillMissing(FacilityProfileFields target, FacilityProfileFields seed)
    {
        foreach (var property in typeof(FacilityProfileFields).GetProperties().Where(property => property.PropertyType == typeof(string)))
            if (string.IsNullOrWhiteSpace((string?)property.GetValue(target))) property.SetValue(target, property.GetValue(seed));
    }

    public static FacilityProfileFields Create(string slug, string shortName, string address, string category)
    {
        var profile = new FacilityProfileFields
        {
            FullName = shortName,
            ShortName = shortName,
            Type = "Линейная часть магистрального газопровода",
            Branch = "ООО «Газпром трансгаз Чайковский»",
            Category = string.IsNullOrWhiteSpace(category) ? "I" : category,
            Region = slug == "votkinskoe" ? "Удмуртская Республика" : "Пермский край",
            Address = address,
            EnvironmentalAspects = "Выбросы в атмосферный воздух\nСбросы сточных вод\nОбращение с отходами\nИспользование природных ресурсов\nОхрана земель и почв\nПредупреждение аварийных ситуаций",
            GasTreatment = "Есть",
            TreatmentFacilities = "Есть",
            WaterSupply = "Есть"
        };

        if (slug == "bereznikovskoe")
        {
            profile.Zones = "Компрессорный цех\nГазораспределительные станции\nКотельная\nОчистные сооружения\nАртезианская скважина\nСклад ГСМ\nСанитарно-защитная зона";
            profile.Equipment = "Газоперекачивающие агрегаты\nКомпрессорные установки\nГазораспределительные станции\nФакельные установки\nКотлы\nДизель-генераторы\nАртезианские скважины";
            profile.SpecialZones = "Водоохранная зона реки Кама; особо охраняемые природные территории";
            profile.EmissionSources = "Газоперекачивающие агрегаты\nКотельные установки\nФакельные установки\nДизель-генераторы\nАвтотранспорт";
            profile.Permits = "Комплексное экологическое разрешение\nРазрешение на выбросы\nРешение о предоставлении водного объекта\nЛицензия на пользование недрами\nПлан предупреждения и ликвидации разливов нефти";
            profile.PecProgram = "Есть";
            profile.WasteStandard = "Есть";
            profile.SanitaryZoneProject = "Есть";
            profile.Responsible = "Иванов И.И.";
        }
        else
        {
            profile.Zones = "Компрессорный цех\nГазораспределительные станции\nКотельная\nДизельная электростанция\nСклад ГСМ\nСанитарно-защитная зона\nАдминистративно-бытовая зона";
            profile.Equipment = slug == "votkinskoe"
                ? "Компрессорная станция Игринская\nГазораспределительные станции\nЛинейно-эксплуатационная служба\nКамеры запуска и приема очистных устройств\nЗапорная арматура\nКотлы\nДизельные установки\nАГНКС"
                : "Газоперекачивающие агрегаты\nКомпрессорные цеха\nГазораспределительные станции\nКамеры запуска и приема очистных устройств\nЗапорная арматура\nКотлы\nДизельные установки\nАГНКС";
            profile.SpecialZones = "Отсутствуют";
            profile.EmissionSources = "Газоперекачивающие агрегаты\nКотельные установки\nДизельные установки\nАвтотранспорт";
            profile.Permits = "Разрешение на выбросы\nРазрешение на сбросы\nПлан предупреждения и ликвидации разливов нефти\nЛицензия на эксплуатацию взрывопожароопасных объектов";
            profile.Responsible = slug == "votkinskoe" ? "Черепанов А.А." : "Иванов И.И.";
        }

        return profile;
    }
}
