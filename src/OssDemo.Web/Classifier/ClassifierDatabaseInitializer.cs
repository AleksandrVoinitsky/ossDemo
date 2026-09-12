using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

internal sealed class ClassifierDatabaseInitializer(IConfiguration configuration)
{
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));
        await connection.OpenAsync(cancellationToken);
        await using (var schema = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app_classifier_versions (
                id uuid PRIMARY KEY, version text NOT NULL UNIQUE, status text NOT NULL, effective_from date NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS app_classifier_sections (
                id uuid PRIMARY KEY, version_id uuid NOT NULL REFERENCES app_classifier_versions(id), code text NOT NULL,
                title text NOT NULL, position integer NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now(), UNIQUE(version_id,code));
            CREATE TABLE IF NOT EXISTS app_classifier_criteria (
                id uuid PRIMARY KEY, section_id uuid NOT NULL REFERENCES app_classifier_sections(id), code text NOT NULL,
                risk_text text NOT NULL, check_text text NOT NULL, search_terms text NOT NULL DEFAULT '',
                applicability_rules jsonb NOT NULL DEFAULT '[]'::jsonb, source_hints jsonb NOT NULL DEFAULT '[]'::jsonb,
                is_base boolean NOT NULL DEFAULT false, is_active boolean NOT NULL DEFAULT true, position integer NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(), UNIQUE(section_id,code));
            CREATE INDEX IF NOT EXISTS ix_classifier_sections_version_position ON app_classifier_sections(version_id,position);
            CREATE INDEX IF NOT EXISTS ix_classifier_criteria_section_position ON app_classifier_criteria(section_id,position);
            """, connection))
            await schema.ExecuteNonQueryAsync(cancellationToken);

        var tree = ClassifierSeedData.Tree;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var version = new NpgsqlCommand("""
            INSERT INTO app_classifier_versions(id,version,status,effective_from) VALUES(@id,@version,@status,@date)
            ON CONFLICT(version) DO NOTHING
            """, connection, transaction))
        {
            version.Parameters.AddWithValue("id", tree.VersionId);
            version.Parameters.AddWithValue("version", tree.Version);
            version.Parameters.AddWithValue("status", tree.Status);
            version.Parameters.AddWithValue("date", tree.EffectiveFrom);
            await version.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var section in tree.Sections)
        {
            await using var sectionCommand = new NpgsqlCommand("""
                INSERT INTO app_classifier_sections(id,version_id,code,title,position) VALUES(@id,@version,@code,@title,@position)
                ON CONFLICT(version_id,code) DO NOTHING
                """, connection, transaction);
            sectionCommand.Parameters.AddWithValue("id", section.Id);
            sectionCommand.Parameters.AddWithValue("version", tree.VersionId);
            sectionCommand.Parameters.AddWithValue("code", section.Code);
            sectionCommand.Parameters.AddWithValue("title", section.Title);
            sectionCommand.Parameters.AddWithValue("position", section.Position);
            await sectionCommand.ExecuteNonQueryAsync(cancellationToken);
            foreach (var criterion in section.Criteria)
            {
                await using var criterionCommand = new NpgsqlCommand("""
                    INSERT INTO app_classifier_criteria(id,section_id,code,risk_text,check_text,search_terms,applicability_rules,source_hints,is_base,is_active,position)
                    VALUES(@id,@section,@code,@risk,@check,@terms,@rules,@hints,@base,@active,@position)
                    ON CONFLICT(section_id,code) DO NOTHING
                    """, connection, transaction);
                criterionCommand.Parameters.AddWithValue("id", criterion.Id);
                criterionCommand.Parameters.AddWithValue("section", section.Id);
                criterionCommand.Parameters.AddWithValue("code", criterion.Code);
                criterionCommand.Parameters.AddWithValue("risk", criterion.RiskText);
                criterionCommand.Parameters.AddWithValue("check", criterion.CheckText);
                criterionCommand.Parameters.AddWithValue("terms", criterion.SearchTerms);
                criterionCommand.Parameters.AddWithValue("rules", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(criterion.ApplicabilityRules));
                criterionCommand.Parameters.AddWithValue("hints", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(criterion.SourceHints));
                criterionCommand.Parameters.AddWithValue("base", criterion.IsBase);
                criterionCommand.Parameters.AddWithValue("active", criterion.IsActive);
                criterionCommand.Parameters.AddWithValue("position", criterion.Position);
                await criterionCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
