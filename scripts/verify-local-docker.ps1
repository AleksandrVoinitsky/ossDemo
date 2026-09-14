param(
    [switch]$Live,
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

$extension = & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile exec -T database psql -U ossdemo -d ossdemo -Atc "SELECT extname FROM pg_extension WHERE extname='vector';"
if ($LASTEXITCODE -ne 0 -or ([string]$extension).Trim() -ne 'vector') {
    throw 'The pgvector extension is not installed in the local database.'
}

$tableCount = & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile exec -T database psql -U ossdemo -d ossdemo -Atc "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name LIKE 'app_%';"
if ($LASTEXITCODE -ne 0 -or [int]$tableCount -lt 10) {
    throw "Application schema is incomplete. Found $tableCount app_* tables."
}

Write-Output "Local Docker live checks passed. Application tables: $tableCount."
