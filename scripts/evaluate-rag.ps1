param(
    [ValidateRange(1, 100)]
    [int]$MaxCases = 100,
    [switch]$UseLlm
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$casesPath = Join-Path $repositoryRoot 'data/evals/rag-grounding-cases.json'
$headers = @{ Cookie = 'oss.auth=true' }

if (-not (Test-Path -LiteralPath $casesPath)) { throw "RAG evaluation cases are missing: $casesPath" }
$suite = Get-Content -LiteralPath $casesPath -Raw | ConvertFrom-Json
if ([int]$suite.schemaVersion -ne 1 -or @($suite.cases).Count -eq 0) { throw 'RAG evaluation suite has an unsupported or empty schema.' }

$results = foreach ($case in @($suite.cases) | Select-Object -First $MaxCases) {
    $question = if ($UseLlm) { [string]$case.question } else { "!!$($case.question)" }
    $body = @{ message = $question; conversation = @(); stream = $false } | ConvertTo-Json -Compress
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:18080/api/ai/chat' -Headers $headers `
            -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 300
        $sourceText = if ($UseLlm) { [string]::Join("`n", @($response.sources | ForEach-Object title)) } else { [string]$response.answer }
        $sourceMatch = @($case.expectedSourceTerms | Where-Object { $sourceText.Contains([string]$_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        $answerMatch = -not $UseLlm -or @($case.requiredAnswerTerms | Where-Object { ([string]$response.answer).Contains([string]$_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
        $citationMatch = -not $UseLlm -or [string]$response.answer -match '\[S\d+\]'
        [pscustomobject]@{
            Id = $case.id
            Passed = [bool]$response.grounded -and $sourceMatch -and $answerMatch -and $citationMatch
            Grounded = [bool]$response.grounded
            SourceMatch = $sourceMatch
            AnswerMatch = $answerMatch
            Citations = $citationMatch
            Seconds = [math]::Round($timer.Elapsed.TotalSeconds, 2)
        }
    }
    catch {
        [pscustomobject]@{ Id=$case.id; Passed=$false; Grounded=$false; SourceMatch=$false; AnswerMatch=$false; Citations=$false; Seconds=[math]::Round($timer.Elapsed.TotalSeconds, 2) }
    }
    finally {
        $timer.Stop()
    }
}

$results | Format-Table -AutoSize
$failed = @($results | Where-Object { -not $_.Passed })
if ($failed.Count -gt 0) { throw "RAG evaluation failed for $($failed.Count) of $($results.Count) cases." }
Write-Output "RAG evaluation passed: $($results.Count) of $($results.Count) cases. LLM: $UseLlm."
