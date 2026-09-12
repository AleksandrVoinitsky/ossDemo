param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '../src/OssDemo.Web/Data/Checklists'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '../src/OssDemo.Web/Checklists/ChecklistSeedData.cs')
)

Add-Type -AssemblyName Microsoft.VisualBasic

function Convert-ToCSharpString([string]$Value) {
    if ($null -eq $Value) { return '""' }
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n') + '"'
}

function Read-ChecklistCsv([string]$Path) {
    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($Path, [System.Text.Encoding]::UTF8)
    try {
        $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
        $parser.SetDelimiters(',')
        $parser.HasFieldsEnclosedInQuotes = $true
        $null = $parser.ReadFields()
        $rows = [System.Collections.Generic.List[object]]::new()
        while (-not $parser.EndOfData) {
            $fields = $parser.ReadFields()
            if ($fields.Count -ge 7 -and $fields[0] -match '^\d+$') {
                $rows.Add([pscustomobject]@{ Section=$fields[1]; Title=$fields[2]; Basis=$fields[3]; Result=$fields[4]; Nonconformity=$fields[5]; Note=$fields[6] })
            }
        }
        return $rows
    }
    finally { $parser.Dispose() }
}

$ids = @{
    'bardym-2024'='10000000-0000-0000-0000-000000000001'; 'votkinsk-2024'='10000000-0000-0000-0000-000000000002'; 'perm-2024'='10000000-0000-0000-0000-000000000003';
    'votkinsk-2026-template'='20000000-0000-0000-0000-000000000001'; 'gornozavodsk-2026-template'='20000000-0000-0000-0000-000000000002'; 'perm-2026-template'='20000000-0000-0000-0000-000000000003'
}
$dates = @{
    'bardym-2024'=@('2024-08-27','2024-08-30'); 'votkinsk-2024'=@('2024-05-20','2024-05-22'); 'perm-2024'=@('2024-10-28','2024-10-30')
}
$catalog = Get-Content -LiteralPath (Join-Path $SourceDirectory 'catalog.json') -Raw | ConvertFrom-Json
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('internal static class ChecklistSeedData')
$lines.Add('{')
$lines.Add('    public static readonly IReadOnlyList<ChecklistSeedTemplate> Templates =')
$lines.Add('    [')
foreach ($entry in $catalog | Where-Object kind -eq 'template') {
    $rows = Read-ChecklistCsv (Join-Path $SourceDirectory $entry.fileName)
    $lines.Add("        new(new Guid(`"$($ids[$entry.id])`"), $(Convert-ToCSharpString $entry.id), $(Convert-ToCSharpString $entry.title), $(Convert-ToCSharpString $entry.facility),")
    $lines.Add('        [')
    foreach ($group in $rows | Group-Object Section) {
        $lines.Add("            new($(Convert-ToCSharpString $group.Name),")
        $lines.Add('            [')
        foreach ($row in $group.Group) {
            $lines.Add("                new($(Convert-ToCSharpString $row.Title), $(Convert-ToCSharpString $row.Basis), $(Convert-ToCSharpString $row.Note)),")
        }
        $lines.Add('            ]),')
    }
    $lines.Add('        ]),')
}
$lines.Add('    ];')
$lines.Add('')
$lines.Add('    public static readonly IReadOnlyList<ChecklistSeedHistory> History =')
$lines.Add('    [')
foreach ($entry in $catalog | Where-Object kind -eq 'history') {
    $rows = Read-ChecklistCsv (Join-Path $SourceDirectory $entry.fileName)
    $range = $dates[$entry.id]
    $approved = "$($range[1])T15:00:00+05:00"
    $lines.Add("        new(new Guid(`"$($ids[$entry.id])`"), $(Convert-ToCSharpString $entry.id), $(Convert-ToCSharpString $entry.title), $(Convert-ToCSharpString $entry.facility), new DateOnly($($range[0].Substring(0,4)), $([int]$range[0].Substring(5,2)), $([int]$range[0].Substring(8,2))), new DateOnly($($range[1].Substring(0,4)), $([int]$range[1].Substring(5,2)), $([int]$range[1].Substring(8,2))), DateTimeOffset.Parse(`"$approved`"), ChecklistStatus.Approved,")
    $lines.Add('        [')
    foreach ($row in $rows) {
        $lines.Add("            new($(Convert-ToCSharpString $row.Section), $(Convert-ToCSharpString $row.Title), $(Convert-ToCSharpString $row.Basis), $(Convert-ToCSharpString $row.Result), $(Convert-ToCSharpString $row.Nonconformity), $(Convert-ToCSharpString $row.Note)),")
    }
    $lines.Add('        ]),')
}
$lines.Add('    ];')
$lines.Add('}')
[System.IO.File]::WriteAllLines((Resolve-Path (Split-Path $OutputPath)).Path + '/' + (Split-Path $OutputPath -Leaf), $lines, [System.Text.UTF8Encoding]::new($false))
