using Npgsql;
using System.Text.Json;

public sealed class FacilityProfileService(IConfiguration configuration, OperationalDataService operationalData)
{
    public async Task<FacilityProfile?> GetAsync(string slug, CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured) return null;
        await operationalData.GetFacilitiesAsync(cancellationToken);
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT slug, profile::text, latitude, longitude FROM app_facility_profiles WHERE slug = @slug", connection);
        command.Parameters.AddWithValue("slug", slug);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new FacilityProfile(reader.GetString(0), JsonSerializer.Deserialize<FacilityProfileFields>(reader.GetString(1)) ?? new FacilityProfileFields(), reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3));
        }

        await reader.CloseAsync();
        await using var facilityCommand = new NpgsqlCommand("SELECT name, address, nvoc_category, latitude, longitude FROM app_facilities WHERE slug = @slug", connection);
        facilityCommand.Parameters.AddWithValue("slug", slug);
        await using var facilityReader = await facilityCommand.ExecuteReaderAsync(cancellationToken);
        if (!await facilityReader.ReadAsync(cancellationToken)) return null;
        return new FacilityProfile(slug, CreateInitialProfile(slug, facilityReader.GetString(0), facilityReader.GetString(1), facilityReader.GetString(2)), facilityReader.IsDBNull(3) ? null : facilityReader.GetDecimal(3), facilityReader.IsDBNull(4) ? null : facilityReader.GetDecimal(4));
    }

    public async Task<string?> SaveAsync(string? slug, FacilityProfileFields profile, decimal? latitude, decimal? longitude, CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured || string.IsNullOrWhiteSpace(profile.FullName) || string.IsNullOrWhiteSpace(profile.ShortName)) return null;
        await operationalData.GetFacilitiesAsync(cancellationToken);
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var savedSlug = string.IsNullOrWhiteSpace(slug)
            ? await CreateAvailableSlugAsync(connection, transaction, profile.ShortName, cancellationToken)
            : slug;
        await using (var existingFacilityCommand = new NpgsqlCommand("SELECT slug FROM app_facilities WHERE name = @name", connection, transaction))
        {
            existingFacilityCommand.Parameters.AddWithValue("name", profile.ShortName.Trim());
            var existingSlug = await existingFacilityCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.IsNullOrWhiteSpace(existingSlug) && !string.Equals(existingSlug, savedSlug, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO app_facility_profiles (slug, profile, latitude, longitude)
            VALUES (@slug, CAST(@profile AS jsonb), @latitude, @longitude)
            ON CONFLICT (slug) DO UPDATE SET profile = EXCLUDED.profile, latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude, updated_at = now()
            """, connection, transaction);
        command.Parameters.AddWithValue("slug", savedSlug);
        command.Parameters.AddWithValue("profile", JsonSerializer.Serialize(profile));
        command.Parameters.AddWithValue("latitude", (object?)latitude ?? DBNull.Value);
        command.Parameters.AddWithValue("longitude", (object?)longitude ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var facilityCommand = new NpgsqlCommand("""
            INSERT INTO app_facilities (name, address, nvoc_category, latitude, longitude, slug)
            VALUES (@name, @address, @category, @latitude, @longitude, @slug)
            ON CONFLICT (name) DO UPDATE SET address = EXCLUDED.address, nvoc_category = EXCLUDED.nvoc_category, latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude, slug = EXCLUDED.slug
            """, connection, transaction);
        facilityCommand.Parameters.AddWithValue("name", profile.ShortName.Trim());
        facilityCommand.Parameters.AddWithValue("address", profile.Address?.Trim() ?? string.Empty);
        facilityCommand.Parameters.AddWithValue("category", profile.Category?.Trim() ?? string.Empty);
        facilityCommand.Parameters.AddWithValue("latitude", (object?)latitude ?? DBNull.Value);
        facilityCommand.Parameters.AddWithValue("longitude", (object?)longitude ?? DBNull.Value);
        facilityCommand.Parameters.AddWithValue("slug", savedSlug);
        await facilityCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return savedSlug;
    }

    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app_facilities (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(), name TEXT NOT NULL UNIQUE, address TEXT NOT NULL,
                nvoc_category TEXT NOT NULL, latitude NUMERIC(9,6), longitude NUMERIC(9,6), slug TEXT NOT NULL DEFAULT '',
                created_at TIMESTAMPTZ NOT NULL DEFAULT now());
            ALTER TABLE app_facilities ALTER COLUMN latitude DROP NOT NULL;
            ALTER TABLE app_facilities ALTER COLUMN longitude DROP NOT NULL;
            ALTER TABLE app_facilities ADD COLUMN IF NOT EXISTS slug TEXT NOT NULL DEFAULT '';
            CREATE TABLE IF NOT EXISTS app_facility_profiles (
                slug TEXT PRIMARY KEY, profile JSONB NOT NULL, latitude NUMERIC(9,6), longitude NUMERIC(9,6),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private bool IsDatabaseConfigured => !string.IsNullOrWhiteSpace(configuration.GetConnectionString("OssDatabase"));
    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase"));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string CreateSlug(string value)
    {
        var map = new Dictionary<char, string> { ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya" };
        var text = string.Concat(value.ToLowerInvariant().Select(character => map.TryGetValue(character, out var replacement) ? replacement : char.IsLetterOrDigit(character) ? character.ToString() : "-"));
        return string.Join('-', text.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<string> CreateAvailableSlugAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string shortName, CancellationToken cancellationToken)
    {
        var baseSlug = CreateSlug(shortName);
        if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = "facility";
        for (var suffix = 1; ; suffix++)
        {
            var candidate = suffix == 1 ? baseSlug : $"{baseSlug}-{suffix}";
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM app_facility_profiles WHERE slug = @slug
                    UNION ALL
                    SELECT 1 FROM app_facilities WHERE slug = @slug)
                """, connection, transaction);
            command.Parameters.AddWithValue("slug", candidate);
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!) return candidate;
        }
    }

    private static FacilityProfileFields CreateInitialProfile(string slug, string shortName, string address, string category)
    {
        var profile = new FacilityProfileFields
        {
            FullName = slug switch
            {
                "bardymskoe" => "Бардымское линейное производственное управление магистральных газопроводов",
                "votkinskoe" => "Воткинское линейное производственное управление магистральных газопроводов",
                _ => "Березниковское линейное производственное управление магистральных газопроводов"
            },
            ShortName = shortName, Type = "Линейная часть магистрального газопровода", Branch = "ООО «Газпром трансгаз Чайковский" + "»",
            Category = category, Region = slug == "votkinskoe" ? "Удмуртская Республика" : "Пермский край", Address = address,
            SpecialZones = slug == "bereznikovskoe" ? "Водоохранная зона реки Кама" : "Отсутствуют",
            Responsible = slug == "votkinskoe" ? "Черепанов Александр Анатольевич, начальник отдела охраны окружающей среды" : "Иванов И.И., инженер по ООС филиала",
            Zones = "Компрессорный цех\nГазораспределительный пункт (ГРП)\nКотельная\nДизельная электростанция (ДЭС)\nСклад ГСМ\nСанитарно-защитная зона (СЗЗ)\nАдминистративное здание",
            EnvironmentalAspects = "Выбросы в атмосферный воздух\nСбросы в водные объекты\nОбразование и обращение с отходами\nПотребление природных ресурсов\nВоздействие на земельные ресурсы и почвы\nАварийные и залповые воздействия",
            EmissionSources = "Выбросы от газотурбинной установки (ГТУ)\nВыбросы от котельных установок\nВыбросы от дизельной электростанции (ДЭС)\nСбросы газа через факельную установку"
        };
        if (slug == "bereznikovskoe")
        {
            profile.Zones = "Компрессорный цех\nГазораспределительный пункт (ГРП)\nКотельная\nОчистные сооружения канализационные (КОС/ЛОС)\nДизельная электростанция (ДЭС)\nСклад ГСМ\nСанитарно-защитная зона (СЗЗ)\nАртезианская скважина\nАдминистративное здание";
            profile.Equipment = "Газоперекачивающие агрегаты ГПА-1, ГПА-2\nФакельные устройства\nАртезианские скважины — 2 шт.\nДизель-генераторы";
            profile.GasTreatment = "Да"; profile.TreatmentFacilities = "Да (локальные очистные сооружения)"; profile.WaterSupply = "Да (для хозпитьевого водоснабжения)";
            profile.Permits = "Разрешение на выбросы № 123 от 15.01.2024\nДоговор водопользования № 456 от 10.03.2023\nЛицензия на недропользование (скважины) № 789 от 01.06.2022";
            profile.PecProgram = "Да (утверждена приказом № 45 от 20.12.2023)"; profile.WasteStandard = "Да (утверждён 30.11.2023)"; profile.SanitaryZoneProject = "Да (утверждён 15.08.2020)";
        }
        else if (slug == "bardymskoe")
        {
            profile.Equipment = "Газоперекачивающие агрегаты (ГПА)\nКомпрессорные цеха КЦ № 3, № 4, № 6\nГРС «Орда», «Усть-Кишерты», «Голдыревский», «Уинское», «Барда», «Большая Ась»\nУзлы запуска и приёма внутритрубных устройств\nЗапорно-регулирующая арматура\nКотельные установки на ГРС\nРезервные ДЭС\nАГНКС";
            profile.Permits = "КЭР для объектов I категории НВОС\nРазрешение на выбросы загрязняющих веществ\nРазрешение на сбросы\nПЛАРН\nЛицензия на эксплуатацию взрывопожароопасных объектов";
        }
        else if (slug == "votkinskoe")
        {
            profile.Equipment = "Компрессорная станция «Игринская»\nВоткинская газокомпрессорная служба\nГРС-4 в Ижевске, ГРС «Глазов», «Колхоз Искра», «Зюино»\nИжевская ЛЭС\nУзлы запуска и приёма внутритрубных устройств\nЗапорно-регулирующая арматура\nКотельные установки на ГРС\nРезервные ДЭС\nАГНКС";
            profile.Permits = "КЭР для объектов I категории НВОС\nРазрешение на выбросы загрязняющих веществ\nРазрешение на сбросы\nПЛАРН\nЛицензия на эксплуатацию взрывопожароопасных объектов";
        }
        return profile;
    }
}

public sealed record FacilityProfile(string Slug, FacilityProfileFields Profile, decimal? Latitude, decimal? Longitude);
public sealed record FacilityProfileSaveRequest(FacilityProfileFields Profile, decimal? Latitude, decimal? Longitude);
public sealed class FacilityProfileFields
{
    public string FullName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Type { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Category { get; set; } = "";
    public string Region { get; set; } = "";
    public string Address { get; set; } = "";
    public string SpecialZones { get; set; } = "";
    public string Responsible { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Zones { get; set; } = "";
    public string EnvironmentalAspects { get; set; } = "";
    public string Equipment { get; set; } = "";
    public string GasTreatment { get; set; } = "";
    public string TreatmentFacilities { get; set; } = "";
    public string WaterSupply { get; set; } = "";
    public string EmissionSources { get; set; } = "";
    public string Permits { get; set; } = "";
    public string PecProgram { get; set; } = "";
    public string WasteStandard { get; set; } = "";
    public string SanitaryZoneProject { get; set; } = "";
}
