using Npgsql;
using System.Text.Json;

public sealed class FacilityProfileService(IConfiguration configuration, OperationalDataService operationalData)
{
    public async Task EnsureSeedProfilesAsync(CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured) return;
        var facilities = await operationalData.GetFacilitiesAsync(cancellationToken);
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        foreach (var facility in facilities.Where(item => item.Slug is "bardymskoe" or "bereznikovskoe" or "votkinskoe"))
        {
            await using var select = new NpgsqlCommand("SELECT profile::text FROM app_facility_profiles WHERE slug=@slug", connection); select.Parameters.AddWithValue("slug", facility.Slug);
            var json = await select.ExecuteScalarAsync(cancellationToken) as string;
            var fields = string.IsNullOrWhiteSpace(json) ? new FacilityProfileFields() : JsonSerializer.Deserialize<FacilityProfileFields>(json) ?? new FacilityProfileFields();
            FacilityProfileSeedData.FillMissing(fields, FacilityProfileSeedData.Create(facility.Slug, facility.Name, facility.Address, facility.NvocCategory));
            fields.StructuredProfile ??= FacilityProfileMigration.FromLegacy(facility.Slug, fields);
            await using var upsert = new NpgsqlCommand("INSERT INTO app_facility_profiles (slug,profile,latitude,longitude) VALUES (@slug,CAST(@profile AS jsonb),@latitude,@longitude) ON CONFLICT (slug) DO UPDATE SET profile=EXCLUDED.profile, latitude=COALESCE(app_facility_profiles.latitude,EXCLUDED.latitude), longitude=COALESCE(app_facility_profiles.longitude,EXCLUDED.longitude), updated_at=now()", connection);
            upsert.Parameters.AddWithValue("slug", facility.Slug); upsert.Parameters.AddWithValue("profile", JsonSerializer.Serialize(fields)); upsert.Parameters.AddWithValue("latitude", (object?)facility.Latitude ?? DBNull.Value); upsert.Parameters.AddWithValue("longitude", (object?)facility.Longitude ?? DBNull.Value); await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

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
            var savedSlug = reader.GetString(0);
            var fields = JsonSerializer.Deserialize<FacilityProfileFields>(reader.GetString(1)) ?? new FacilityProfileFields();
            FacilityProfileSeedData.FillMissing(fields, FacilityProfileSeedData.Create(savedSlug, fields.ShortName, fields.Address, fields.Category));
            var structured = fields.StructuredProfile ?? FacilityProfileMigration.FromLegacy(savedSlug, fields);
            fields.StructuredProfile = structured;
            return new FacilityProfile(savedSlug, fields, reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3), structured, FacilityProfileReadiness.Evaluate(structured));
        }

        await reader.CloseAsync();
        await using var facilityCommand = new NpgsqlCommand("SELECT name, address, nvoc_category, latitude, longitude FROM app_facilities WHERE slug = @slug", connection);
        facilityCommand.Parameters.AddWithValue("slug", slug);
        await using var facilityReader = await facilityCommand.ExecuteReaderAsync(cancellationToken);
        if (!await facilityReader.ReadAsync(cancellationToken)) return null;
        var initial = FacilityProfileSeedData.Create(slug, facilityReader.GetString(0), facilityReader.GetString(1), facilityReader.GetString(2));
        var initialStructured = FacilityProfileMigration.FromLegacy(slug, initial);
        initial.StructuredProfile = initialStructured;
        return new FacilityProfile(slug, initial, facilityReader.IsDBNull(3) ? null : facilityReader.GetDecimal(3), facilityReader.IsDBNull(4) ? null : facilityReader.GetDecimal(4), initialStructured, FacilityProfileReadiness.Evaluate(initialStructured));
    }

    public async Task<string?> SaveAsync(string? slug, FacilityProfileFields profile, decimal? latitude, decimal? longitude, FacilityProfileV2? structuredProfile, CancellationToken cancellationToken)
    {
        if (!IsDatabaseConfigured || string.IsNullOrWhiteSpace(profile.FullName) || string.IsNullOrWhiteSpace(profile.ShortName)) return null;
        await operationalData.GetFacilitiesAsync(cancellationToken);
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var savedSlug = string.IsNullOrWhiteSpace(slug)
            ? await CreateAvailableSlugAsync(connection, transaction, profile.ShortName, cancellationToken)
            : slug;
        profile.StructuredProfile = NormalizeStructuredProfile(savedSlug, profile.ShortName, structuredProfile ?? profile.StructuredProfile ?? FacilityProfileMigration.FromLegacy(savedSlug, profile));
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

    public async Task<FacilityProfileReadinessResult?> ConfirmAsync(string slug, string verifiedBy, CancellationToken cancellationToken)
    {
        var facility = await GetAsync(slug, cancellationToken);
        if (facility is null) return null;
        var structured = facility.StructuredProfile ?? FacilityProfileMigration.FromLegacy(slug, facility.Profile);
        var unknown = FacilityProfileV2.RequiredFeatureCodes
            .Where(code => !structured.Features.TryGetValue(code, out var fact) || fact.State == FacilityFactState.Unknown)
            .ToArray();
        if (unknown.Length > 0) return FacilityProfileReadiness.Evaluate(structured);

        structured.VerificationStatus = "verified";
        structured.VerifiedAt = DateTimeOffset.UtcNow;
        structured.VerifiedBy = verifiedBy;
        facility.Profile.StructuredProfile = structured;
        await EnsureTableAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE app_facility_profiles SET profile = CAST(@profile AS jsonb), updated_at = now() WHERE slug = @slug", connection);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("profile", JsonSerializer.Serialize(facility.Profile));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return FacilityProfileReadiness.Evaluate(structured);
    }

    private static FacilityProfileV2 NormalizeStructuredProfile(string slug, string shortName, FacilityProfileV2 source)
    {
        var normalized = FacilityProfileV2.CreateEmpty(slug, shortName);
        foreach (var code in FacilityProfileV2.RequiredFeatureCodes)
        {
            if (source.Features.TryGetValue(code, out var fact))
                normalized.Features[code] = new(fact.State, fact.Details?.Trim() ?? "");
        }
        normalized.ObjectTypeCodes.AddRange(source.ObjectTypeCodes.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        normalized.Documents.AddRange(source.Documents);
        normalized.LegacySource = source.LegacySource;
        normalized.VerificationStatus = "needs_review";
        return normalized;
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

}

public sealed record FacilityProfile(string Slug, FacilityProfileFields Profile, decimal? Latitude, decimal? Longitude, FacilityProfileV2? StructuredProfile = null, FacilityProfileReadinessResult? Readiness = null);
public sealed record FacilityProfileSaveRequest(FacilityProfileFields Profile, decimal? Latitude, decimal? Longitude, FacilityProfileV2? StructuredProfile = null);
public sealed class FacilityProfileFields
{
    public FacilityProfileV2? StructuredProfile { get; set; }
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
