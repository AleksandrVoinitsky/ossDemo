$ErrorActionPreference = 'Stop'

$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspacePrefix = $workspace + [IO.Path]::DirectorySeparatorChar
$relativeRoots = @('data/import/knowledge-inbox', 'data/knowledge-base')

foreach ($relativeRoot in $relativeRoots) {
    $root = [IO.Path]::GetFullPath((Join-Path $workspace $relativeRoot))
    if (-not ($root + [IO.Path]::DirectorySeparatorChar).StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe knowledge root: $root"
    }

    $corporate = Join-Path $root 'Корпоративные документы'
    $regional = Join-Path $root 'Региональные документы'
    $other = Join-Path $root 'Прочие нормативные документы'
    New-Item -ItemType Directory -Force -Path $corporate, $regional, $other | Out-Null

    foreach ($oldName in @('Документы ГТЧ', 'Документы ПАО')) {
        $source = Join-Path $root $oldName
        if (-not (Test-Path -LiteralPath $source)) { continue }

        foreach ($file in @(Get-ChildItem -LiteralPath $source -File -Recurse)) {
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
            $destination = [IO.Path]::GetFullPath((Join-Path $corporate $relative))
            $corporatePrefix = $corporate + [IO.Path]::DirectorySeparatorChar
            if (-not $destination.StartsWith($corporatePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Unsafe knowledge destination: $destination"
            }

            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
            if (Test-Path -LiteralPath $destination) {
                $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
                if ($sourceHash -ne $destinationHash) { throw "Knowledge file collision: $destination" }
                Remove-Item -LiteralPath $file.FullName
            } else {
                Move-Item -LiteralPath $file.FullName -Destination $destination
            }
        }

        foreach ($directory in @(Get-ChildItem -LiteralPath $source -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending)) {
            if (-not (Get-ChildItem -LiteralPath $directory.FullName -Force)) { Remove-Item -LiteralPath $directory.FullName }
        }
        if (-not (Get-ChildItem -LiteralPath $source -Force)) { Remove-Item -LiteralPath $source }
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Filter '*.md' | Where-Object { $_.Name -like 'Реестр *требован*.md' })) {
        $destination = Join-Path $other $file.Name
        if (Test-Path -LiteralPath $destination) {
            $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            if ($sourceHash -ne $destinationHash) { throw "Knowledge file collision: $destination" }
            Remove-Item -LiteralPath $file.FullName
        } else {
            Move-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}

Write-Output 'Knowledge folders migrated successfully.'
