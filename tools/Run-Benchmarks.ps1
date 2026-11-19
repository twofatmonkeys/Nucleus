<#
.SYNOPSIS
    Runs the Nucleus benchmarks and gates them against the committed baselines, or writes the
    baselines with -Accept.

.DESCRIPTION
    Allocations are counted for the whole test process, and a database benchmark suite allocates
    15-20% more after another suite's heavy work in the same process. So this runs the
    database-free suite and then every database suite (each provider, Save and Read) in a
    test process of its own, for each target framework. A provider with no test database
    configured skips cleanly. See the README's Benchmarks section.

.PARAMETER Accept
    Writes the baselines instead of gating. Review the diff and give the reason in the commit.

.PARAMETER Framework
    The target frameworks to run. Both by default.

.PARAMETER Provider
    The database providers to run: Sqlite, Postgres, SqlServer. All by default.

.PARAMETER Suite
    The database suites to run: Save, Read. Both by default.

.PARAMETER NoBuild
    Runs the last Release build instead of building first.

.EXAMPLE
    ./tools/Run-Benchmarks.ps1
    ./tools/Run-Benchmarks.ps1 -Provider Sqlite -Suite Save -Framework net10.0
    ./tools/Run-Benchmarks.ps1 -Accept
#>
[CmdletBinding()]
param(
    [switch] $Accept,
    [ValidateSet('net8.0', 'net10.0')] [string[]] $Framework = @('net8.0', 'net10.0'),
    [ValidateSet('Sqlite', 'Postgres', 'SqlServer')] [string[]] $Provider = @('Sqlite', 'Postgres', 'SqlServer'),
    [ValidateSet('Save', 'Read')] [string[]] $Suite = @('Save', 'Read'),
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$unitTests = Join-Path $root 'tests/TFM.Nucleus.Tests'
$integrationTests = Join-Path $root 'tests/TFM.Nucleus.IntegrationTests'

$saved = @{
    NUCLEUS_BENCHMARK        = $env:NUCLEUS_BENCHMARK
    NUCLEUS_BENCHMARK_ACCEPT = $env:NUCLEUS_BENCHMARK_ACCEPT
}
$failed = [System.Collections.Generic.List[string]]::new()

function Invoke-BenchmarkRun([string] $Label, [string] $Project, [string] $Fw, [string] $Filter) {
    Write-Host "== $Label ($Fw)" -ForegroundColor Cyan
    # A failing test writes to stderr. Under 'Stop', Windows PowerShell turns that into a
    # terminating error when the output is redirected, which would end the whole run at the first
    # failure; the exit code is what decides here.
    $ErrorActionPreference = 'Continue'
    & dotnet test $Project --configuration Release --no-build --framework $Fw --filter $Filter --logger 'console;verbosity=detailed' 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        $failed.Add("$Label ($Fw)")
    }
}

try {
    $env:NUCLEUS_BENCHMARK = '1'
    if ($Accept) { $env:NUCLEUS_BENCHMARK_ACCEPT = '1' } else { $env:NUCLEUS_BENCHMARK_ACCEPT = $null }

    if (-not $NoBuild) {
        & dotnet build (Join-Path $root 'TFM.Nucleus.slnx') --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "The Release build failed (exit code $LASTEXITCODE)." }
    }

    foreach ($fw in $Framework) {
        Invoke-BenchmarkRun 'database-free' $unitTests $fw 'FullyQualifiedName~InProcessBenchmarks'
        foreach ($p in $Provider) {
            foreach ($s in $Suite) {
                Invoke-BenchmarkRun "$p $s" $integrationTests $fw "FullyQualifiedName~$p$($s)Benchmarks"
            }
        }
    }
}
finally {
    foreach ($name in $saved.Keys) {
        # A null value removes the variable, as it was before the run.
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
}

if ($failed.Count -gt 0) {
    Write-Host "Failed: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host ($(if ($Accept) { 'Baselines written. Review the diff.' } else { 'Every benchmark is within its baseline.' })) -ForegroundColor Green
