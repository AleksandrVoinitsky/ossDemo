param(
    [ValidateSet('Up', 'Down', 'Build', 'Status', 'Logs', 'Verify')]
    [string]$Action = 'Status'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repositoryRoot 'compose.local.yml'
$environmentFile = Join-Path $repositoryRoot '.env.local'
$verificationScript = Join-Path $PSScriptRoot 'verify-local-docker.ps1'

if (-not (Test-Path -LiteralPath $environmentFile)) {
    throw "Create $environmentFile from .env.local.example before starting the local stack."
}

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & docker compose --project-name ossdemo-local --env-file $environmentFile -f $composeFile @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    switch ($Action) {
        'Up' {
            & $verificationScript
            Invoke-Compose up --detach --build
            & $verificationScript -Live
        }
        'Down' {
            Invoke-Compose down
        }
        'Build' {
            Invoke-Compose build
        }
        'Status' {
            Invoke-Compose ps --all
        }
        'Logs' {
            Invoke-Compose logs --tail 200
        }
        'Verify' {
            & $verificationScript -Live
        }
    }
}
finally {
    Pop-Location
}
