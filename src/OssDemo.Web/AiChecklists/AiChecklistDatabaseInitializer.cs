using Npgsql;

internal sealed class AiChecklistDatabaseInitializer(IConfiguration configuration)
{
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString("OssDatabase")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings__OssDatabase."));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app_ai_checklist_runs (
                id uuid PRIMARY KEY, facility_id uuid NOT NULL REFERENCES app_facilities(id), facility_name text NOT NULL,
                facility_profile jsonb NOT NULL, status text NOT NULL DEFAULT 'ready', checklist_id uuid NULL REFERENCES app_checklists(id),
                created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT app_ai_checklist_runs_status CHECK (status IN ('ready','finalizing','completed')));
            CREATE TABLE IF NOT EXISTS app_ai_checklist_evidence (
                run_id uuid NOT NULL REFERENCES app_ai_checklist_runs(id) ON DELETE CASCADE, evidence_id text NOT NULL,
                query_label text NOT NULL, document_title text NOT NULL, source_label text NOT NULL, content text NOT NULL,
                score double precision NOT NULL, position integer NOT NULL, PRIMARY KEY (run_id,evidence_id));
            CREATE TABLE IF NOT EXISTS app_ai_checklist_batches (
                run_id uuid NOT NULL REFERENCES app_ai_checklist_runs(id) ON DELETE CASCADE, batch_index integer NOT NULL,
                topic text NOT NULL, evidence_ids text[] NOT NULL, status text NOT NULL DEFAULT 'pending', items jsonb NOT NULL DEFAULT '[]'::jsonb,
                error text NULL, duration_ms bigint NULL, created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY (run_id,batch_index),
                CONSTRAINT app_ai_checklist_batches_status CHECK (status IN ('pending','queued','running','completed','failed')));
            CREATE INDEX IF NOT EXISTS ix_ai_checklist_batches_queue ON app_ai_checklist_batches(status,created_at);
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS criterion_codes text[] NOT NULL DEFAULT '{}';
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS applicability_reason text NULL;
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS search_query text NULL;
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS fallback_title text NULL;
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS classifier_section text NULL;
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS evidence jsonb NOT NULL DEFAULT '[]'::jsonb;
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS stage text NOT NULL DEFAULT 'waiting';
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS stage_message text NOT NULL DEFAULT 'Ожидает запуска';
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS found_source_count integer NOT NULL DEFAULT 0;
            ALTER TABLE app_ai_checklist_batches ADD COLUMN IF NOT EXISTS draft_output text NOT NULL DEFAULT '';
            UPDATE app_ai_checklist_batches SET
                stage=CASE
                    WHEN status='completed' AND jsonb_array_length(items)>0 THEN 'completed_ai'
                    WHEN status='completed' THEN 'completed_base'
                    WHEN status='failed' THEN 'failed'
                    WHEN status='skipped' THEN 'skipped'
                    WHEN status='queued' THEN 'queued'
                    WHEN status='running' THEN 'searching'
                    ELSE stage END,
                stage_message=CASE
                    WHEN status='completed' AND jsonb_array_length(items)>0 THEN 'ИИ-формулировка проверена и сохранена'
                    WHEN status='completed' THEN 'Сохранён базовый пункт классификатора'
                    WHEN status='failed' THEN 'Не удалось выполнить обработку; будет использован базовый пункт'
                    WHEN status='skipped' THEN 'Остановлено до начала обработки'
                    WHEN status='queued' THEN 'Ожидает последовательной обработки'
                    WHEN status='running' THEN 'Ищем основания в базе знаний'
                    ELSE stage_message END
            WHERE stage='waiting' AND status<>'pending';
            ALTER TABLE app_ai_checklist_runs DROP CONSTRAINT IF EXISTS app_ai_checklist_runs_status;
            ALTER TABLE app_ai_checklist_runs ADD CONSTRAINT app_ai_checklist_runs_status CHECK (status IN ('ready','stopping','stopped','finalizing','completed'));
            ALTER TABLE app_ai_checklist_batches DROP CONSTRAINT IF EXISTS app_ai_checklist_batches_status;
            ALTER TABLE app_ai_checklist_batches ADD CONSTRAINT app_ai_checklist_batches_status CHECK (status IN ('pending','queued','running','completed','failed','skipped'));
            ALTER TABLE app_checklists ADD COLUMN IF NOT EXISTS ai_run_id uuid NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_app_checklists_ai_run_id ON app_checklists(ai_run_id) WHERE ai_run_id IS NOT NULL;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
