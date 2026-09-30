[CmdletBinding()]
param([string] $PackagesDirectory = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$references = [ordered]@{
    'Trellis.ServiceLevelIndicators' = 'trellis-api-sli.md'
    'Trellis.ServiceLevelIndicators.Asp' = 'trellis-api-sli-asp.md'
    'Trellis.ServiceLevelIndicators.Asp.ApiVersioning' = 'trellis-api-sli-apiversioning.md'
}

foreach ($id in $references.Keys) {
    $packages = @(Get-ChildItem -LiteralPath $PackagesDirectory -Filter '*.nupkg' |
        Where-Object { $_.Name -match "^$([regex]::Escape($id))\.\d" -and $_.Name -notlike '*.symbols.nupkg' })
    if ($packages.Count -ne 1) {
        throw "Expected one packed $id in $PackagesDirectory, found $($packages.Count)."
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
    try {
        $manifestEntry = $archive.GetEntry('guidance/reference-manifest.json')
        $referencePath = $references[$id]
        $referenceEntry = $archive.GetEntry($referencePath)
        if (-not $manifestEntry -or -not $referenceEntry) {
            throw "$id must pack its guidance manifest and $referencePath."
        }
        if (@($archive.Entries | Where-Object {
            $_.FullName.StartsWith('trellis/', [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 0) {
            throw "$id must not pack a trellis/ directory."
        }

        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        $referenceBytes = [System.IO.MemoryStream]::new()
        try {
            $referenceEntry.Open().CopyTo($referenceBytes)
            $hash = [Convert]::ToHexString(
                [System.Security.Cryptography.SHA256]::HashData($referenceBytes.ToArray())).ToLowerInvariant()
            $referenceText = [System.Text.Encoding]::UTF8.GetString($referenceBytes.ToArray())
        }
        finally { $referenceBytes.Dispose() }

        $description = [string] $manifest.documents[0].description
        if ($manifest.schemaVersion -ne 1 -or @($manifest.documents).Count -ne 1 -or
            $manifest.documents[0].path -ne $referencePath -or
            $manifest.documents[0].sha256 -ne $hash -or
            $manifest.documents[0].usage -ne 'onDemand' -or
            $description.Length -eq 0 -or $description.Length -gt 200 -or $description -notmatch '^Open when ' -or
            $null -ne $manifest.PSObject.Properties['entryPoints']) {
            throw "$id manifest does not match the packed reference bytes, on-demand usage, and description."
        }
        # Each package installs into its own directory, so a relative link to a sibling package's reference breaks.
        if ($referenceText -match '\]\(trellis-[a-z0-9-]+\.md') {
            throw "$id links to another package's reference file; refer to other packages by ID instead."
        }

        if (@($archive.Entries | Where-Object {
            $_.FullName -match '^(build|buildTransitive)/'
        }).Count -ne 0) {
            throw "$id still ships a consumer build target."
        }
        $nuspec = $archive.GetEntry("$id.nuspec")
        if (-not $nuspec) { throw "$id is missing its package metadata." }
        $metadataReader = [System.IO.StreamReader]::new($nuspec.Open())
        try { [xml] $metadata = $metadataReader.ReadToEnd() }
        finally { $metadataReader.Dispose() }
        if ($metadata.SelectSingleNode('//*[local-name()="dependency" and (@id="Trellis.AgentDocs.Packaging" or @id="Trellis.Core")]')) {
            throw "$id leaks a publisher-only dependency or introduces a Core dependency."
        }
        Write-Host "PASS $($packages[0].Name) ships validated guidance without consumer targets"
    }
    finally { $archive.Dispose() }
}
