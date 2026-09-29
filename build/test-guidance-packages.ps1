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
        }
        finally { $referenceBytes.Dispose() }

        if ($manifest.schemaVersion -ne 1 -or @($manifest.documents).Count -ne 1 -or
            $manifest.documents[0].path -ne $referencePath -or
            $manifest.documents[0].sha256 -ne $hash -or
            @($manifest.entryPoints).Count -ne 1 -or $manifest.entryPoints[0] -ne $referencePath) {
            throw "$id manifest does not match the packed reference bytes and entry point."
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
        if ($metadata.SelectSingleNode('//*[local-name()="readme"]')?.InnerText -ne 'README.md') {
            throw "$id must declare its packed README.md as the NuGet readme."
        }
        $readmeEntry = $archive.GetEntry('README.md')
        if (-not $readmeEntry) { throw "$id is missing its NuGet readme." }
        $readmeReader = [System.IO.StreamReader]::new($readmeEntry.Open())
        try { $readme = $readmeReader.ReadToEnd() }
        finally { $readmeReader.Dispose() }
        if ($readme -notmatch '(?m)^dotnet tool install Trellis\.AgentDocs --version \S+ --tool-manifest \.config/dotnet-tools\.json\r?$' -or
            -not $readme.Contains('dotnet tool run agentdocs init <solution-or-project>')) {
            throw "$id NuGet readme must explain how to install and initialize AgentDocs."
        }
        Write-Host "PASS $($packages[0].Name) ships validated guidance without consumer targets"
    }
    finally { $archive.Dispose() }
}
