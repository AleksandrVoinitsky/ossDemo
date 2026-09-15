using Npgsql;

internal sealed class RequirementDatabaseInitializer(IConfiguration configuration)
{
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app_requirement_revisions (
                requirement_id text PRIMARY KEY,
                requirement_text text NOT NULL,
                basis text NOT NULL,
                version bigint NOT NULL CHECK (version >= 2),
                updated_by text NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS app_classifier_requirement_links (
                criterion_id uuid NOT NULL REFERENCES app_classifier_criteria(id) ON DELETE CASCADE,
                requirement_id text NOT NULL,
                action text NOT NULL CHECK (action IN ('include','exclude')),
                version bigint NOT NULL DEFAULT 1,
                updated_by text NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (criterion_id, requirement_id)
            );
            CREATE TABLE IF NOT EXISTS app_requirement_change_log (
                id uuid PRIMARY KEY,
                entity_type text NOT NULL,
                criterion_id uuid NULL,
                requirement_id text NOT NULL,
                action text NOT NULL,
                before_value jsonb NULL,
                after_value jsonb NULL,
                actor text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_requirement_change_log_requirement
                ON app_requirement_change_log(requirement_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_classifier_requirement_links_criterion
                ON app_classifier_requirement_links(criterion_id);
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
