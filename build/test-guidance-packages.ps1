[CmdletBinding()]
param([string] $PackagesDirectory = (Join-Path (Join-Path $PSScriptRoot '..') 'artifacts'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-AgentDocsInstallCommand {
    param([string] $Readme, [string] $ExpectedCommand)

    $commands = [regex]::Matches($Readme,
        '(?im)^[\t ]*dotnet[\t ]+tool[\t ]+install[\t ]+Trellis\.AgentDocs(?=[\t \r\n]|$)[^\r\n]*\r?$')
    return $commands.Count -eq 1 -and $commands[0].Value.TrimEnd("`r") -ceq $ExpectedCommand
}

$references = [ordered]@{
    'Trellis.ServiceLevelIndicators' = 'trellis-api-sli.md'
    'Trellis.ServiceLevelIndicators.Asp' = 'trellis-api-sli-asp.md'
    'Trellis.ServiceLevelIndicators.Asp.ApiVersioning' = 'trellis-api-sli-apiversioning.md'
}

# The published validator checks the contract and discoverability (links that leave the package, front matter,
# size budgets); --strict makes a warning fail like an error. It is pinned to the packaging helper's version, which
# is published in lockstep with the tool.
$props = [xml] (Get-Content -LiteralPath (Join-Path (Join-Path $PSScriptRoot '..') 'Directory.Packages.props') -Raw)
$toolVersion = $props.SelectSingleNode('//PackageVersion[@Include="Trellis.AgentDocs.Packaging"]').Version
$installCommand = "dotnet tool install Trellis.AgentDocs --version $toolVersion --tool-manifest .config/dotnet-tools.json"
$staleCommand = $installCommand.Replace($toolVersion, '0.0.0')
$installCases = @(
    @{ Name = 'correct LF'; Readme = "$installCommand`n"; Expected = $true },
    @{ Name = 'correct CRLF'; Readme = "$installCommand`r`n"; Expected = $true },
    @{ Name = 'missing'; Readme = ''; Expected = $false },
    @{ Name = 'stale only'; Readme = $staleCommand; Expected = $false },
    @{ Name = 'duplicate correct'; Readme = "$installCommand`n$installCommand"; Expected = $false },
    @{ Name = 'conflicting version'; Readme = "$installCommand`n$staleCommand"; Expected = $false },
    @{ Name = 'indented conflicting version'; Readme = "$installCommand`n  $staleCommand"; Expected = $false }
)
foreach ($case in $installCases) {
    if ((Test-AgentDocsInstallCommand -Readme $case.Readme -ExpectedCommand $installCommand) -ne $case.Expected) {
        throw "AgentDocs install-command regression failed: $($case.Name)."
    }
}
Write-Host 'PASS AgentDocs single-command and version-pin regression cases'
$toolDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "agentdocs-validate-$([guid]::NewGuid().ToString('N'))"
$install = & dotnet tool install Trellis.AgentDocs --version $toolVersion --tool-path $toolDirectory 2>&1
if ($LASTEXITCODE -ne 0) { throw "Could not install Trellis.AgentDocs $toolVersion for validation:`n$($install | Out-String)" }

try {
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
            # Any non-absolute link to a trellis-*.md file counts, whatever its prefix (./, ../, subfolders) or anchor,
            # in inline form [text](target) or reference form [id]: target.
            if ($referenceText -match '\]\(\s*(?!https?:|mailto:|#)[^)\s]*trellis-[a-z0-9-]+\.md' -or
                $referenceText -match '(?m)^\s*\[[^\]]+\]:\s*(?!https?:|mailto:|#)\S*trellis-[a-z0-9-]+\.md') {
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
            if ($metadata.SelectSingleNode('//*[local-name()="readme"]')?.InnerText -ne 'README.md') {
                throw "$id must declare its packed README.md as the NuGet readme."
            }
            $readmeEntry = $archive.GetEntry('README.md')
            if (-not $readmeEntry) { throw "$id is missing its NuGet readme." }
            $readmeReader = [System.IO.StreamReader]::new($readmeEntry.Open())
            try { $readme = $readmeReader.ReadToEnd() }
            finally { $readmeReader.Dispose() }
            if (-not (Test-AgentDocsInstallCommand -Readme $readme -ExpectedCommand $installCommand) -or
                -not $readme.Contains('dotnet tool run agentdocs init <solution-or-project>')) {
                throw "$id NuGet readme must contain exactly one Trellis.AgentDocs install command pinned to $toolVersion and explain how to initialize it."
            }
            # Restoring a package never activates its guide: the readme must describe approval and sync.
            if (-not $readme.Contains('approvedPackages') -or -not $readme.Contains('dotnet tool run agentdocs sync')) {
                throw "$id NuGet readme must explain approving the package and running sync."
            }
            $validation = & (Join-Path $toolDirectory 'agentdocs') validate $packages[0].FullName --strict 2>&1
            if ($LASTEXITCODE -ne 0) { throw "agentdocs validate --strict rejected ${id}:`n$($validation | Out-String)" }
            Write-Host "PASS $($packages[0].Name) ships validated guidance without consumer targets"
        }
        finally { $archive.Dispose() }
    }
}
finally { Remove-Item -LiteralPath $toolDirectory -Recurse -Force -ErrorAction SilentlyContinue }
