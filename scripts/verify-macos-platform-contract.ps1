param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$ResultsRoot = "",

    [ValidateRange(1, 600)]
    [int]$AggregateDeadlineSeconds = 600,

    [switch]$NoRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-MacOSPlatformContractSelection {
    return @(
        [pscustomobject]@{ Project = "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj"; Source = "tests/EmbodySense.Cli.Command.Tests/ConsoleAgentRuntimeHostTests.cs"; Fact = "EmbodySense.Cli.Command.Tests.ConsoleAgentRuntimeHostTests.RunAsync_runs_reusable_loop_through_cli_console_adapter" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj"; Source = "tests/EmbodySense.Cli.Command.Tests/ConsoleAgentRuntimeHostTests.cs"; Fact = "EmbodySense.Cli.Command.Tests.ConsoleAgentRuntimeHostTests.RunAsync_projects_the_canonical_human_input_catalog_without_a_model_turn" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"; Source = "tests/EmbodySense.Core.Startup.Tests/Loops/Execution/CustomLoopRuntimeTestsPublicationAndConcurrency.cs"; HelperSource = "tests/EmbodySense.Core.Startup.Tests/Loops/Execution/CustomLoopRuntimeTests.cs"; Fact = "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTestsPublicationAndConcurrency.Public_runtime_admits_executes_publishes_and_exposes_inspectable_artifacts_without_changing_default_turns" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"; Source = "tests/EmbodySense.Core.Startup.Tests/Loops/Execution/CustomLoopRuntimeTestsDurabilityAndRecovery.cs"; HelperSource = "tests/EmbodySense.Core.Startup.Tests/Loops/Execution/CustomLoopRuntimeTests.cs"; Fact = "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTestsDurabilityAndRecovery.Production_factory_keeps_saved_custom_loop_invocation_on_the_legacy_runtime_without_canonical_proof" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"; Source = "tests/EmbodySense.Core.Startup.Tests/Runtime/AgentRuntimeFactoryTests.cs"; Fact = "EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryTests.RunTurnAsync_uses_startup_context_and_streams_response_through_public_runtime" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj"; Source = "tests/EmbodySense.Core.Startup.Tests/Loops/Execution/CustomLoopRuntimeReceiptRecoveryTests.cs"; Fact = "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeReceiptRecoveryTests.Receipt_completion_failure_after_admission_parks_the_run_and_a_later_replay_completes_without_dispatch" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"; Source = "tests/EmbodySense.Web.Tests/WebGovernedLoopBackgroundLifetimeTests.cs"; HelperSource = "tests/EmbodySense.Web.Tests/WebBackgroundLifetimeCodexExecutable.cs"; Fact = "EmbodySense.Web.Tests.WebGovernedLoopBackgroundLifetimeTests.Shutdown_reports_stopped_and_disposes_the_pinned_runtime_idempotently" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"; Source = "tests/EmbodySense.Web.Tests/WebGovernedLoopBackgroundLifetimeTests.cs"; HelperSource = "tests/EmbodySense.Web.Tests/WebPinnedRuntimeCodexExecutable.cs"; Fact = "EmbodySense.Web.Tests.WebGovernedLoopBackgroundLifetimeTests.Pinned_runtime_quarantines_cancelled_default_provider_without_recreating_background_runtime" }
        [pscustomobject]@{ Project = "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"; Source = "tests/EmbodySense.IntegrationTests/Cli/CliBehaviorTests.cs"; Fact = "EmbodySense.IntegrationTests.Cli.CliBehaviorTests.Run_command_accepts_app_server_options_and_exits_without_inference" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"; Source = "tests/EmbodySense.Core.Clients.Tests/Capabilities/IsolatedCapabilityExecutableHostTests.cs"; Fact = "EmbodySense.Core.Clients.Tests.Capabilities.IsolatedCapabilityExecutableHostTests.Default_host_and_secret_requiring_artifact_fail_closed" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"; Source = "tests/EmbodySense.Core.Clients.Tests/Capabilities/IsolatedCapabilityExecutableHostTests.cs"; Fact = "EmbodySense.Core.Clients.Tests.Capabilities.IsolatedCapabilityExecutableHostTests.Configured_boundary_obeys_the_host_supported_lease_binding_contract" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"; Source = "tests/EmbodySense.Core.Clients.Tests/Capabilities/IsolatedCapabilityExecutableHostTests.cs"; Fact = "EmbodySense.Core.Clients.Tests.Capabilities.IsolatedCapabilityExecutableHostTests.Caller_supplied_artifact_root_is_unavailable_without_a_proved_resolver" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"; Source = "tests/EmbodySense.Core.Clients.Tests/Capabilities/IsolatedCapabilityExecutableHostTests.cs"; Fact = "EmbodySense.Core.Clients.Tests.Capabilities.IsolatedCapabilityExecutableHostTests.Host_redacts_platform_availability_diagnostics" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj"; Source = "tests/EmbodySense.Core.Clients.Tests/Capabilities/IsolatedCapabilityExecutableHostTests.cs"; Fact = "EmbodySense.Core.Clients.Tests.Capabilities.IsolatedCapabilityExecutableHostTests.Path_escape_and_malformed_input_never_start_process" }
        [pscustomobject]@{ Project = "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"; Source = "tests/EmbodySense.Core.Persistence.Tests/Loops/CustomLoopRunStoreTests.cs"; Fact = "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunStoreTests.MacOS_canonical_publication_uses_full_native_durability_barriers_for_run_and_tombstone" }
    )
}

function Assert-MacOSPlatformContractHost {
    if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) {
        throw "MacOSPlatformContract requires macOS."
    }
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::Arm64 -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::Arm64) {
        throw "MacOSPlatformContract requires ARM64 operating-system and PowerShell process architectures."
    }
}

function Assert-MacOSPlatformContractSelection {
    param([object[]]$Selection)

    $expectedCounts = @{ "EmbodySense.Cli.Command.Tests.csproj" = 2; "EmbodySense.Core.Startup.Tests.csproj" = 4; "EmbodySense.Web.Tests.csproj" = 2; "EmbodySense.IntegrationTests.csproj" = 1; "EmbodySense.Core.Clients.Tests.csproj" = 5; "EmbodySense.Core.Persistence.Tests.csproj" = 1 }
    if ($Selection.Count -ne 15 -or @($Selection | Group-Object Fact | Where-Object Count -ne 1).Count -ne 0 -or @($Selection | Group-Object Project).Count -ne 6) {
        throw "MacOSPlatformContract requires exactly fifteen unique facts across six assemblies."
    }
    foreach ($project in $expectedCounts.Keys) {
        if (@($Selection | Where-Object Project -ceq $project).Count -ne $expectedCounts[$project]) {
            throw "MacOSPlatformContract has an invalid fact count for '$project'."
        }
    }
    foreach ($entry in $Selection) {
        $sourcePath = Join-Path $repoRoot $entry.Source
        $method = ($entry.Fact -split '\.')[-1]
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or (Get-Content -LiteralPath $sourcePath -Raw).IndexOf($method, [StringComparison]::Ordinal) -lt 0) {
            throw "MacOSPlatformContract source mapping no longer owns '$($entry.Fact)'."
        }
        if ($null -ne $entry.PSObject.Properties["HelperSource"] -and -not (Test-Path -LiteralPath (Join-Path $repoRoot $entry.HelperSource) -PathType Leaf)) {
            throw "MacOSPlatformContract helper provenance is missing for '$($entry.Fact)'."
        }
    }
}

function Invoke-MacOSPlatformContractAssembly {
    param([object]$Assembly, [Diagnostics.Stopwatch]$Stopwatch)

    $remaining = [TimeSpan]::FromSeconds($AggregateDeadlineSeconds) - $Stopwatch.Elapsed
    if ($remaining -le [TimeSpan]::Zero) {
        throw "MacOSPlatformContract exhausted its aggregate $AggregateDeadlineSeconds-second budget before '$($Assembly.Name)'."
    }
    $resultDirectory = Join-Path $ResultsRoot ([IO.Path]::GetFileNameWithoutExtension($Assembly.Name))
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $filter = (@($Assembly.Group | ForEach-Object { "FullyQualifiedName=$($_.Fact)" }) -join '|')
    $arguments = @("test", $Assembly.Name, "-c", $Configuration, "--no-build", "--no-restore", "--filter", $filter, "--logger", "trx;LogFileName=macos-platform-contract.trx", "--results-directory", $resultDirectory, "/p:RestoreIgnoreFailedSources=true")
    Invoke-VerificationPhase -Name "macos-platform-$([IO.Path]::GetFileNameWithoutExtension($Assembly.Name))" -FileName "dotnet" -Arguments $arguments -TimeoutSeconds ([Math]::Max(1, [int][Math]::Floor($remaining.TotalSeconds))) -WorkingDirectory $repoRoot
}

function Read-MacOSPlatformContractResults {
    param([object[]]$Selection)

    $facts = [Collections.Generic.List[object]]::new()
    foreach ($assembly in @($Selection | Group-Object Project)) {
        $resultDirectory = Join-Path $ResultsRoot ([IO.Path]::GetFileNameWithoutExtension($assembly.Name))
        $trxFiles = @(Get-ChildItem -LiteralPath $resultDirectory -Filter "*.trx" -File)
        if ($trxFiles.Count -ne 1) {
            throw "MacOSPlatformContract requires exactly one TRX for '$($assembly.Name)'; found $($trxFiles.Count)."
        }
        [xml]$trx = Get-Content -LiteralPath $trxFiles[0].FullName -Raw
        $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        if ($results.Count -ne $assembly.Group.Count) {
            throw "MacOSPlatformContract TRX for '$($assembly.Name)' has unexpected, missing, or duplicate results."
        }
        foreach ($entry in $assembly.Group) {
            $matches = @($results | Where-Object { [string]$_.testName -ceq $entry.Fact })
            if ($matches.Count -ne 1 -or [string]$matches[0].outcome -cne "Passed") {
                throw "MacOSPlatformContract requires one passing, non-skipped result for '$($entry.Fact)'."
            }
            $fact = [ordered]@{ project = $entry.Project; source = $entry.Source; assembly = [IO.Path]::GetFileNameWithoutExtension($entry.Project); fact = $entry.Fact; outcome = "Passed"; trx = [IO.Path]::GetRelativePath((Split-Path -Parent $ResultsRoot), $trxFiles[0].FullName).Replace([IO.Path]::DirectorySeparatorChar, "/") }
            if ($null -ne $entry.PSObject.Properties["HelperSource"]) { $fact.helperSource = $entry.HelperSource }
            $facts.Add($fact)
        }
    }
    return @($facts | Sort-Object fact)
}

if (-not $NoRun) {
    . (Join-Path $PSScriptRoot "verification-phase.ps1")
    if ([string]::IsNullOrWhiteSpace($ResultsRoot)) { throw "MacOSPlatformContract requires a result root when executing." }
    Assert-MacOSPlatformContractHost
    $selection = @(Get-MacOSPlatformContractSelection)
    Assert-MacOSPlatformContractSelection -Selection $selection
    if (Test-Path -LiteralPath $ResultsRoot) { Remove-Item -LiteralPath $ResultsRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    foreach ($assembly in @($selection | Group-Object Project)) {
        Invoke-MacOSPlatformContractAssembly -Assembly $assembly -Stopwatch $stopwatch
    }
    $facts = @(Read-MacOSPlatformContractResults -Selection $selection)
    if ($facts.Count -ne 15 -or @($facts | Group-Object fact | Where-Object Count -ne 1).Count -ne 0 -or @($facts | Group-Object assembly).Count -ne 6) {
        throw "MacOSPlatformContract result map is incomplete."
    }
    $stopwatch.Stop()
    [IO.File]::WriteAllText((Join-Path $ResultsRoot "macos-platform-contract-results.json"), ([ordered]@{ schemaVersion = 1; facts = $facts } | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Output "VERIFY_MACOS_PLATFORM_CONTRACT schema_version=1 status=passed facts=15 assemblies=6 elapsed_seconds=$($stopwatch.Elapsed.TotalSeconds.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))"
}
