[CmdletBinding()]
param(
    [ValidateRange(1, 10)]
    [int]$MaximumExecutions = 10,

    [ValidateRange(1, 120)]
    [int]$TotalTimeoutSeconds = 120,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expectedBase = "ebf457d4afaad9b93293101e846baefcffc5dae1"
& git -C $repositoryRoot merge-base --is-ancestor $expectedBase HEAD
if ($LASTEXITCODE -ne 0) {
    throw "The diagnostic runner must execute on a branch descended from exact sample-3 head $expectedBase."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssZ")
    $OutputDirectory = Join-Path $repositoryRoot "tests/VerificationResults/Diagnostics/Sample3Persistence/$stamp"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

. (Join-Path $PSScriptRoot "verification-phase.ps1")
Reset-VerificationPhaseState

$filter = "FullyQualifiedName=EmbodySense.Core.Persistence.Tests.Loops.Execution.Reconciliation.GovernedLoopEffectReconciliationCaseStoreTests.Malformed_pending_journal_is_corrupt_for_read_and_not_silently_replayed"
$project = Join-Path $repositoryRoot "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
$modes = @("terminal-signal", "exit-code")
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

Write-Output "SAMPLE3_DIAGNOSTIC_START schema_version=1 expected_base=$expectedBase maximum_executions=$MaximumExecutions timeout_seconds=$TotalTimeoutSeconds output=$OutputDirectory"
try {
    for ($execution = 1; $execution -le $MaximumExecutions; $execution++) {
        $remainingSeconds = [Math]::Floor($TotalTimeoutSeconds - $stopwatch.Elapsed.TotalSeconds)
        if ($remainingSeconds -lt 1) {
            throw "The sample-3 diagnostic exhausted its $TotalTimeoutSeconds-second total bound before execution $execution."
        }

        $mode = $modes[($execution - 1) % $modes.Count]
        $label = "execution-$($execution.ToString('00'))-$mode"
        $env:EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_MODE = $mode
        $env:EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_LOG = Join-Path $OutputDirectory "$label.stages.log"
        $testOutput = Join-Path $OutputDirectory "$label.dotnet.log"
        $results = Join-Path $OutputDirectory "$label-results"
        $arguments = @(
            "test",
            $project,
            "--configuration", $Configuration,
            "--no-build",
            "--no-restore",
            "--filter", $filter,
            "--logger", "console;verbosity=normal",
            "--results-directory", $results
        )

        Write-Output "SAMPLE3_DIAGNOSTIC_EXECUTION_START execution=$execution mode=$mode remaining_seconds=$remainingSeconds"
        try {
            Invoke-VerificationPhase -Name "sample3-$label" -FileName "dotnet" -Arguments $arguments -TimeoutSeconds $remainingSeconds -WorkingDirectory $repositoryRoot -OutputPath $testOutput
        }
        catch {
            Write-Output "SAMPLE3_DIAGNOSTIC_MISMATCH execution=$execution mode=$mode stages=$env:EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_LOG test_output=$testOutput"
            throw
        }

        if (-not (Test-Path -LiteralPath $env:EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_LOG -PathType Leaf)) {
            throw "Execution $execution completed without the required diagnostic stage log."
        }
        Write-Output "SAMPLE3_DIAGNOSTIC_EXECUTION_COMPLETE execution=$execution mode=$mode stages=$env:EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_LOG test_output=$testOutput"
    }
}
finally {
    Remove-Item Env:EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:EMBODYSENSE_SAMPLE3_PERSISTENCE_DIAGNOSTIC_LOG -ErrorAction SilentlyContinue
}

$stopwatch.Stop()
Write-Output "SAMPLE3_DIAGNOSTIC_COMPLETE schema_version=1 status=passed executions=$MaximumExecutions elapsed_seconds=$([Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)) output=$OutputDirectory"
