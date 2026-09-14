internal static class FacilityProfileV2Checks
{
    public static void Run()
    {
        var legacy = new FacilityProfileFields
        {
            FullName = "Тестовый объект",
            ShortName = "ТО",
            Type = "Линейная часть магистрального газопровода",
            EnvironmentalAspects = "Выбросы в атмосферный воздух\nОбращение с отходами"
        };

        var migrated = FacilityProfileMigration.FromLegacy("test", legacy);
        AssertEqual(2, migrated.SchemaVersion);
        AssertEqual("needs_review", migrated.VerificationStatus);
        AssertTrue(migrated.LegacySource.EnvironmentalAspects.Contains("Выбросы"),
            "Исходный legacy-текст должен сохраняться без потери.");
        AssertEqual(FacilityFactState.Present, migrated.Features["air.emissions"].State);
        AssertEqual(FacilityFactState.Unknown, migrated.Features["water.discharge"].State);

        var empty = FacilityProfileV2.CreateEmpty("new-object", "Новый объект");
        AssertTrue(empty.Features.Values.All(item => item.State == FacilityFactState.Unknown),
            "Новая карточка не должна получать вымышленные признаки.");
        var incomplete = FacilityProfileReadiness.Evaluate(empty);
        AssertTrue(!incomplete.CanFinalizeChecklist);
        AssertTrue(incomplete.UnknownFeatureCodes.Contains("water.discharge"));

        foreach (var code in FacilityProfileV2.RequiredFeatureCodes)
            empty.Features[code] = new(FacilityFactState.Absent);
        empty.VerificationStatus = "verified";
        var verified = FacilityProfileReadiness.Evaluate(empty);
        AssertTrue(verified.CanFinalizeChecklist);
        AssertEqual(0, verified.UnknownFeatureCodes.Count);

        AssertTrue(FacilityProfileDictionaries.Features.Any(item => item.Code == "water.discharge"),
            "Справочник должен публиковать стабильный код сброса сточных вод.");
        AssertTrue(FacilityProfileDictionaries.States.Any(item => item.Code == "unknown"),
            "Справочник должен явно публиковать состояние unknown.");
        AssertEqual(FacilityFactState.Unknown, FacilityProfileDictionaries.ParseState("unknown"));
    }

    private static void AssertTrue(bool value, string message = "Assertion failed")
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
