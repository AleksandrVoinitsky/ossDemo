param(
    [switch]$Live,
    [switch]$RequireIndexedRag,
    [ValidateRange(10, 1800)]
    [int]$StartupTimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repositoryRoot 'compose.local.yml'
$environmentFile = Join-Path $repositoryRoot '.env.local'
$environmentExample = Join-Path $repositoryRoot '.env.local.example'
$dockerIgnoreFile = Join-Path $repositoryRoot '.dockerignore'
$databaseInitFile = Join-Path $repositoryRoot 'docker/initdb/001-vector.sql'

if (-not (Test-Path -LiteralPath $composeFile)) {
    throw "Local Compose file is missing: $composeFile"
}

if (-not (Test-Path -LiteralPath $environmentFile)) {
    throw "Local environment file is missing: $environmentFile"
}

if (-not (Test-Path -LiteralPath $environmentExample)) {
    throw "Local environment template is missing: $environmentExample"
}

if (-not (Test-Path -LiteralPath $databaseInitFile)) {
    throw "PostgreSQL initialization script is missing: $databaseInitFile"
}

& git -C $repositoryRoot check-ignore --quiet '.env.local.example'
if ($LASTEXITCODE -eq 0) {
    throw '.env.local.example must be tracked by Git.'
}

$dockerIgnore = Get-Content -LiteralPath $dockerIgnoreFile -Raw
foreach ($pattern in @('.env', '.env.*', '.verify-publish*/')) {
    if ($dockerIgnore -notmatch "(?m)^$([regex]::Escape($pattern))$") {
        throw ".dockerignore must contain '$pattern'."
    }
}

& docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile config --quiet
if ($LASTEXITCODE -ne 0) {
    throw 'Local Compose configuration is invalid.'
}

$rendered = & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile config --format json
if ($LASTEXITCODE -ne 0) {
    throw 'Could not render the local Compose configuration.'
}

$configuration = $rendered | ConvertFrom-Json
$applicationPort = $configuration.services.application.ports | Where-Object { $_.target -eq 8080 }
$databasePort = $configuration.services.database.ports | Where-Object { $_.target -eq 5432 }
if ($applicationPort.host_ip -ne '127.0.0.1' -or [int]$applicationPort.published -ne 18080) {
    throw 'The application must only listen on 127.0.0.1:18080.'
}
if ($databasePort.host_ip -ne '127.0.0.1' -or [int]$databasePort.published -ne 15432) {
    throw 'PostgreSQL must only listen on 127.0.0.1:15432.'
}
if ($configuration.services.database.image -ne 'pgvector/pgvector:pg17') {
    throw 'The local database must use pgvector/pgvector:pg17.'
}
if ($configuration.services.database.volumes.target -notcontains '/docker-entrypoint-initdb.d/001-vector.sql') {
    throw 'The local database must install pgvector during first initialization.'
}
if ($configuration.services.application.environment.ConnectionStrings__OssDatabase -notmatch '^Host=database;Port=5432;') {
    throw 'The application must connect to the Compose database service.'
}
foreach ($volume in @('ossdemo-db-data', 'ossdemo-app-data')) {
    if ($configuration.volumes.PSObject.Properties.Name -notcontains $volume) {
        throw "Named volume '$volume' is missing."
    }
}

if (-not $Live) {
    Write-Output 'Local Docker configuration checks passed.'
    exit 0
}

$databaseReady = & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile exec -T database pg_isready -U ossdemo -d ossdemo
if ($LASTEXITCODE -ne 0 -or $databaseReady -notmatch 'accepting connections') {
    throw 'Local PostgreSQL is not ready.'
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
$applicationResponse = $null
do {
    $applicationContainer = & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile ps --status running --quiet application
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($applicationContainer)) {
        throw 'Local web application container stopped during startup.'
    }
    try {
        $applicationResponse = Invoke-WebRequest -Uri 'http://127.0.0.1:18080/api/operations/facilities' -Headers @{ Cookie = 'oss.auth=true' } -UseBasicParsing -TimeoutSec 5
    }
    catch {
        Start-Sleep -Seconds 2
    }
} while ($null -eq $applicationResponse -and [DateTimeOffset]::UtcNow -lt $deadline)

if ($null -eq $applicationResponse -or $applicationResponse.StatusCode -ne 200) {
    throw "Local web application did not return HTTP 200 within $StartupTimeoutSeconds seconds. Inspect logs with ./scripts/local-docker.ps1 Logs."
}

try {
    $facilities = $applicationResponse.Content | ConvertFrom-Json
}
catch {
    throw 'The facilities API did not return JSON. The smoke check may have been redirected to the login page.'
}
if ($facilities.Count -lt 1) {
    throw 'The local database does not contain seeded facilities.'
}

$profileDictionary = Invoke-RestMethod -Uri 'http://127.0.0.1:18080/api/operations/facility-profile-dictionaries' -Headers @{ Cookie = 'oss.auth=true' } -TimeoutSec 10
if ($profileDictionary.features.code -notcontains 'water.discharge') {
    throw 'The facility profile dictionary is missing stable water.discharge feature.'
}
if ($profileDictionary.states.code -notcontains 'unknown') {
    throw 'The facility profile dictionary is missing tri-state unknown value.'
}
$facilityEditPage = Invoke-WebRequest -Uri "http://127.0.0.1:18080/Facilities/Edit/$($facilities[0].slug)" -Headers @{ Cookie = 'oss.auth=true' } -UseBasicParsing -TimeoutSec 10
if ($facilityEditPage.Content -notmatch 'data-facility-readiness' -or $facilityEditPage.Content -notmatch 'data-structured-features') {
    throw 'The facility editor does not expose structured readiness controls.'
}
$aiChecklistPage = Invoke-WebRequest -Uri 'http://127.0.0.1:18080/Checklists/AiNew' -Headers @{ Cookie = 'oss.auth=true' } -UseBasicParsing -TimeoutSec 10
foreach ($marker in @('data-ai-profile-readiness', 'data-ai-composition-summary', 'data-ai-coverage-gaps', 'data-ai-item-traces', 'data-ai-load-traces')) {
    if ($aiChecklistPage.Content -notmatch $marker) {
        throw "The automated checklist page is missing explainability marker '$marker'."
    }
}

$probeSuffix = [Guid]::NewGuid().ToString('N')
$probeNames = @("acceptance-office-$probeSuffix", "acceptance-industrial-$probeSuffix")
$probeSlugs = [System.Collections.Generic.List[string]]::new()
$requiredFeatureCodes = @(
    'air.emissions', 'air.gasTreatment', 'water.intake', 'water.discharge',
    'water.treatment', 'waste.generation', 'waste.disposalSite', 'land.disturbance',
    'subsoil.wells', 'nature.forest', 'nature.oopt', 'zone.waterProtection'
)

function New-ChecklistProbeBody {
    param([string]$Name, [bool]$HasEmissions)

    $features = [ordered]@{}
    foreach ($code in $requiredFeatureCodes) {
        $features[$code] = @{ state = 'absent'; details = '' }
    }
    if ($HasEmissions) {
        $features['air.emissions'] = @{ state = 'present'; details = 'Стационарные источники выбросов' }
    }

    return @{
        profile = @{
            fullName = "Контрольный объект $Name"
            shortName = $Name
            type = 'Компрессорная станция'
            category = 'III категория'
            region = 'Пермский край'
            address = 'Локальная приёмка'
        }
        latitude = $null
        longitude = $null
        structuredProfile = @{
            schemaVersion = 2
            slug = ''
            shortName = $Name
            verificationStatus = 'needs_review'
            objectTypeCodes = @()
            features = $features
            documents = @()
        }
    }
}

function Remove-ChecklistProbes {
    if ($probeSlugs.Count -eq 0) { return }

    $cleanupSql = @'
DELETE FROM app_ai_checklist_evidence WHERE run_id IN (SELECT r.id FROM app_ai_checklist_runs r JOIN app_facilities f ON f.id=r.facility_id WHERE f.slug IN (:'probe_one', :'probe_two'));
DELETE FROM app_ai_checklist_batches WHERE run_id IN (SELECT r.id FROM app_ai_checklist_runs r JOIN app_facilities f ON f.id=r.facility_id WHERE f.slug IN (:'probe_one', :'probe_two'));
DELETE FROM app_ai_checklist_runs WHERE facility_id IN (SELECT id FROM app_facilities WHERE slug IN (:'probe_one', :'probe_two'));
DELETE FROM app_facility_profiles WHERE slug IN (:'probe_one', :'probe_two');
DELETE FROM app_facilities WHERE slug IN (:'probe_one', :'probe_two');
'@
    $first = $probeSlugs[0]
    $second = if ($probeSlugs.Count -gt 1) { $probeSlugs[1] } else { $first }
    $cleanupSql | & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile exec -T database `
        psql -v ON_ERROR_STOP=1 -U ossdemo -d ossdemo --set="probe_one=$first" --set="probe_two=$second" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove checklist acceptance probes.' }
}

try {
    $probeRuns = foreach ($index in 0..1) {
        $body = New-ChecklistProbeBody -Name $probeNames[$index] -HasEmissions:($index -eq 1)
        $created = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:18080/api/operations/facility-profiles' -Headers @{ Cookie = 'oss.auth=true' } `
            -ContentType 'application/json; charset=utf-8' -Body ($body | ConvertTo-Json -Depth 10 -Compress) -TimeoutSec 30
        $probeSlugs.Add([string]$created.slug)
        Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:18080/api/operations/facility-profiles/$($created.slug)/confirm" `
            -Headers @{ Cookie = 'oss.auth=true' } -TimeoutSec 30 | Out-Null
        $runBody = @{ facilitySlug = [string]$created.slug } | ConvertTo-Json -Compress
        $runResponse = Invoke-WebRequest -Method Post -Uri 'http://127.0.0.1:18080/api/ai-checklists/runs' -Headers @{ Cookie = 'oss.auth=true' } `
            -ContentType 'application/json; charset=utf-8' -Body $runBody -TimeoutSec 60
        if ($runResponse.RawContentLength -gt 524288) {
            throw "The compact checklist run response exceeds 512 KiB: $($runResponse.RawContentLength) bytes."
        }
        $run = $runResponse.Content | ConvertFrom-Json
        if ($run.snapshot.PSObject.Properties.Name -contains 'itemTraces') {
            throw 'The compact checklist run response embeds provenance traces.'
        }
        $tracePage = Invoke-RestMethod -Uri "http://127.0.0.1:18080/api/ai-checklists/runs/$($run.id)/traces?offset=0&limit=50" `
            -Headers @{ Cookie = 'oss.auth=true' } -TimeoutSec 30
        if (@($tracePage.items).Count -ne 50 -or [int]$tracePage.total -ne [int]$run.snapshot.expectedItemCount) {
            throw 'The first checklist provenance page is incomplete or inconsistent with the snapshot.'
        }
        $run
    }

    $officeIds = @($probeRuns[0].snapshot.selectedItemIds | Sort-Object)
    $industrialIds = @($probeRuns[1].snapshot.selectedItemIds | Sort-Object)
    if ($officeIds.Count -eq 0 -or $industrialIds.Count -eq 0) {
        throw 'A checklist acceptance probe returned an empty item set.'
    }
    if (($officeIds -join "`n") -eq ($industrialIds -join "`n")) {
        throw 'Contrasting facility profiles returned identical checklist item sets.'
    }
}
finally {
    Remove-ChecklistProbes
}

try {
    $ragStatus = Invoke-RestMethod -Uri 'http://127.0.0.1:18080/api/rag/status' -Headers @{ Cookie = 'oss.auth=true' } -TimeoutSec 10
}
catch {
    throw "The RAG status API is unavailable: $($_.Exception.Message)"
}
if (-not $ragStatus.databaseConfigured) {
    throw 'The RAG status API reports that PostgreSQL is not configured.'
}
if (-not [string]::IsNullOrWhiteSpace([string]$ragStatus.problem)) {
    throw "The RAG schema is not ready: $($ragStatus.problem)"
}
if ($RequireIndexedRag) {
    if (-not $ragStatus.ready -or [int]$ragStatus.documentCount -lt 1 -or [int]$ragStatus.chunkCount -lt 1) {
        throw "The RAG index is empty or incomplete. Documents: $($ragStatus.documentCount); chunks: $($ragStatus.chunkCount)."
    }

    $probeBody = @{
        message = '!производственный экологический контроль требования'
        conversation = @()
        stream = $false
    } | ConvertTo-Json -Compress
    try {
        $ragProbe = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:18080/api/ai/chat' -Headers @{ Cookie = 'oss.auth=true' } -ContentType 'application/json; charset=utf-8' -Body $probeBody -TimeoutSec 180
    }
    catch {
        throw "The indexed RAG search probe failed: $($_.Exception.Message)"
    }
    if ($ragProbe.mode -ne 'rag-debug-candidates' -or -not $ragProbe.grounded -or [string]::IsNullOrWhiteSpace([string]$ragProbe.answer)) {
        throw 'The indexed RAG search probe did not return grounded diagnostic matches.'
    }
}

$extension = & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile exec -T database psql -U ossdemo -d ossdemo -Atc "SELECT extname FROM pg_extension WHERE extname='vector';"
if ($LASTEXITCODE -ne 0 -or ([string]$extension).Trim() -ne 'vector') {
    throw 'The pgvector extension is not installed in the local database.'
}

$tableCount = & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile exec -T database psql -U ossdemo -d ossdemo -Atc "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name LIKE 'app_%';"
if ($LASTEXITCODE -ne 0 -or [int]$tableCount -lt 10) {
    throw "Application schema is incomplete. Found $tableCount app_* tables."
}

$ragSummary = if ($RequireIndexedRag) {
    " RAG documents: $($ragStatus.documentCount); chunks: $($ragStatus.chunkCount)."
} else {
    ''
}
Write-Output "Local Docker live checks passed. Application tables: $tableCount.$ragSummary"
