[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$localDb = Get-Command SqlLocalDB -ErrorAction Stop
$instance = "TrellisSliTests_" + [Guid]::NewGuid().ToString("N")
$previousConnectionString = $env:TRELLIS_SLI_SQL_CONNECTION_STRING
$created = $false

function Invoke-LocalDb {
    param([string[]] $Arguments)

    & $localDb.Source @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "SqlLocalDB $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    Invoke-LocalDb -Arguments @("create", $instance)
    $created = $true
    Invoke-LocalDb -Arguments @("start", $instance)
    $env:TRELLIS_SLI_SQL_CONNECTION_STRING = "Server=(localdb)\$instance;Database=master;Integrated Security=true;Encrypt=false;Pooling=false;Connect Timeout=15;"

    dotnet test --project Trellis.ServiceLevelIndicators.Asp\tests\Trellis.ServiceLevelIndicators.Asp.Tests.csproj `
        --no-restore -c $Configuration --no-ansi `
        --filter-method "Trellis.ServiceLevelIndicators.Asp.Tests.ProviderCancellationTests.SqlClient_cancellation_preserves_abort_first_semantics"
    if ($LASTEXITCODE -ne 0) {
        throw "SqlClient cancellation tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    try {
        $env:TRELLIS_SLI_SQL_CONNECTION_STRING = $previousConnectionString
        if ($created) {
            try {
                Invoke-LocalDb -Arguments @("stop", $instance)
            }
            finally {
                Invoke-LocalDb -Arguments @("delete", $instance)
            }
        }
    }
    finally {
        Pop-Location
    }
}
