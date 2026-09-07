param(
    [ValidateSet("Debug", "Release")] [string]$Configuration = "Release",
    [string]$ResultsRoot = "",
    [ValidateRange(1, 600)] [int]$AggregateDeadlineSeconds = 600,
    [switch]$NoRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$teamTestNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
$maximumTrxBytes = 1048576

function Assert-MacOSPlatformContractCondition { param([bool]$Condition, [string]$Message) if (-not $Condition) { throw $Message } }
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

function Assert-MacOSPlatformContractRelativePath {
    param([string]$Path, [string]$Description)
    Assert-MacOSPlatformContractCondition (-not [string]::IsNullOrWhiteSpace($Path) -and -not [IO.Path]::IsPathRooted($Path) -and $Path.IndexOf('\', [StringComparison]::Ordinal) -lt 0) "$Description must be an unrooted repository-relative path with '/' separators: $Path"
    Assert-MacOSPlatformContractCondition (@($Path.Split('/') | Where-Object { $_ -in @("", ".", "..") }).Count -eq 0) "$Description contains an unsafe path segment: $Path"
}

function Assert-MacOSPlatformContractSelection {
    param([object[]]$Selection)
    $expectedCounts = [ordered]@{
        "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj" = 2; "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj" = 4; "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj" = 2; "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj" = 1; "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj" = 5; "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj" = 1
    }
    Assert-MacOSPlatformContractCondition ($Selection.Count -eq 15 -and @($Selection | Group-Object Fact | Where-Object Count -ne 1).Count -eq 0 -and @($Selection | Group-Object Project).Count -eq 6) "MacOSPlatformContract requires exactly fifteen unique facts across six assemblies."
    foreach ($project in $expectedCounts.Keys) { Assert-MacOSPlatformContractCondition (@($Selection | Where-Object { [string]$_.Project -ceq $project }).Count -eq $expectedCounts[$project]) "MacOSPlatformContract has an invalid fact count for '$project'." }
    foreach ($entry in $Selection) {
        Assert-MacOSPlatformContractRelativePath ([string]$entry.Project) "MacOSPlatformContract project identity"; Assert-MacOSPlatformContractRelativePath ([string]$entry.Source) "MacOSPlatformContract source identity"
        $method = ([string]$entry.Fact -split '\.')[-1]; $source = Join-Path $repoRoot ([string]$entry.Source)
        Assert-MacOSPlatformContractCondition ((Test-Path -LiteralPath $source -PathType Leaf) -and (Get-Content -LiteralPath $source -Raw).IndexOf($method, [StringComparison]::Ordinal) -ge 0) "MacOSPlatformContract source mapping no longer owns '$($entry.Fact)'."
        if ($null -ne $entry.PSObject.Properties["HelperSource"]) { Assert-MacOSPlatformContractRelativePath ([string]$entry.HelperSource) "MacOSPlatformContract helper source identity"; Assert-MacOSPlatformContractCondition (Test-Path -LiteralPath (Join-Path $repoRoot ([string]$entry.HelperSource)) -PathType Leaf) "MacOSPlatformContract helper provenance is missing for '$($entry.Fact)'." }
    }
}

function Get-MacOSPlatformContractAssemblies {
    param([object[]]$Selection)
    return @($Selection | Group-Object Project | ForEach-Object { $project = [string]$_.Name; $assembly = [IO.Path]::GetFileNameWithoutExtension($project); [pscustomobject]@{ Project = $project; Assembly = $assembly; Group = @($_.Group | Sort-Object Fact); TestAssemblyPath = Join-Path $repoRoot ((Split-Path -Parent $project) + "/bin/$Configuration/net10.0/$assembly.dll") } } | Sort-Object Project)
}

function Assert-MacOSPlatformContractDiscoveries {
    param([object[]]$Selection, [object[]]$Discoveries)
    $assemblies = @((Get-MacOSPlatformContractAssemblies $Selection))
    $expectedProperties = @("assembly", "fact", "project", "testAssembly", "testId", "xunitTestCaseUniqueId") | Sort-Object
    Assert-MacOSPlatformContractCondition ($Discoveries.Count -eq 15 -and @($Discoveries | Group-Object fact | Where-Object Count -ne 1).Count -eq 0 -and @($Discoveries | Group-Object testId | Where-Object Count -ne 1).Count -eq 0 -and @($Discoveries | Group-Object xunitTestCaseUniqueId | Where-Object Count -ne 1).Count -eq 0) "MacOSPlatformContract discovery provenance is incomplete or duplicated."
    foreach ($selected in $Selection) {
        $assembly = @($assemblies | Where-Object { $_.Project -ceq $selected.Project })
        $discovery = @($Discoveries | Where-Object { [string]$_.project -ceq $selected.Project -and [string]$_.fact -ceq $selected.Fact })
        Assert-MacOSPlatformContractCondition ($assembly.Count -eq 1 -and $discovery.Count -eq 1) "MacOSPlatformContract discovery does not bind '$($selected.Fact)' to one selected assembly."
        $properties = @($discovery[0].PSObject.Properties.Name | Sort-Object)
        $testId = [Guid]::Empty
        $expectedAssemblyPath = [IO.Path]::GetRelativePath($repoRoot, $assembly[0].TestAssemblyPath).Replace([IO.Path]::DirectorySeparatorChar, "/")
        Assert-MacOSPlatformContractCondition (($properties -join "|") -ceq ($expectedProperties -join "|") -and [Guid]::TryParse([string]$discovery[0].testId, [ref]$testId) -and $testId -ne [Guid]::Empty -and -not [string]::IsNullOrWhiteSpace([string]$discovery[0].xunitTestCaseUniqueId) -and [string]$discovery[0].assembly -ceq $assembly[0].Assembly -and [string]$discovery[0].testAssembly -ceq $expectedAssemblyPath) "MacOSPlatformContract discovery provenance is invalid for '$($selected.Fact)'."
    }
}

function Get-MacOSPlatformContractRemainingSeconds { param([Diagnostics.Stopwatch]$Stopwatch, [string]$PhaseName) $remaining = [TimeSpan]::FromSeconds($AggregateDeadlineSeconds) - $Stopwatch.Elapsed; if ($remaining -le [TimeSpan]::Zero) { throw "MacOSPlatformContract exhausted its aggregate $AggregateDeadlineSeconds-second budget before '$PhaseName'." }; return [Math]::Max(1, [int][Math]::Floor($remaining.TotalSeconds)) }
function Invoke-MacOSPlatformContractPhase { param([string]$Name, [string]$FileName, [string[]]$Arguments, [Diagnostics.Stopwatch]$Stopwatch) Invoke-VerificationPhase -Name $Name -FileName $FileName -Arguments $Arguments -TimeoutSeconds (Get-MacOSPlatformContractRemainingSeconds $Stopwatch $Name) -WorkingDirectory $repoRoot }

function Invoke-MacOSPlatformContractDiscovery {
    param([object]$Assembly, [Diagnostics.Stopwatch]$Stopwatch, [string]$DiscoveryRoot)
    $output = Join-Path $DiscoveryRoot "$($Assembly.Assembly).json"; $diagnostic = Join-Path $DiscoveryRoot "$($Assembly.Assembly).diag.log"; $filter = (@($Assembly.Group | ForEach-Object { "FullyQualifiedName=$($_.Fact)" }) -join '|')
    $phaseDiagnostics = @(Invoke-MacOSPlatformContractPhase "discover-macos-$($Assembly.Assembly)" (Get-Process -Id $PID).Path @("-NoProfile", "-File", (Join-Path $PSScriptRoot "write-test-inventory.ps1"), "-TestAssemblyPath", $Assembly.TestAssemblyPath, "-Filter", $filter, "-OutputPath", $output, "-DiagnosticPath", $diagnostic, "-WorkingDirectory", $repoRoot) $Stopwatch)
    foreach ($phaseDiagnostic in $phaseDiagnostics) { Write-Host $phaseDiagnostic }
    $inventory = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json; $tests = @($inventory.tests)
    Assert-MacOSPlatformContractCondition ($inventory.schemaVersion -eq 1 -and [string]$inventory.source -ceq [IO.Path]::GetFullPath($Assembly.TestAssemblyPath) -and [string]$inventory.filter -ceq $filter -and [int]$inventory.totalTests -eq $Assembly.Group.Count -and $tests.Count -eq $Assembly.Group.Count) "MacOSPlatformContract discovery is not exact for '$($Assembly.Project)'."
    return @($Assembly.Group | ForEach-Object { $selected = $_; $match = @($tests | Where-Object { [string]$_.fullyQualifiedName -ceq [string]$selected.Fact }); $testId = [Guid]::Empty; Assert-MacOSPlatformContractCondition ($match.Count -eq 1 -and [Guid]::TryParse([string]$match[0].id, [ref]$testId) -and $testId -ne [Guid]::Empty -and -not [string]::IsNullOrWhiteSpace([string]$match[0].xunitTestCaseUniqueId)) "MacOSPlatformContract discovery does not bind '$($selected.Fact)' to one stable test identity."; [pscustomobject][ordered]@{ project = $Assembly.Project; assembly = $Assembly.Assembly; fact = $selected.Fact; testId = $testId.ToString("D"); xunitTestCaseUniqueId = [string]$match[0].xunitTestCaseUniqueId; testAssembly = [IO.Path]::GetRelativePath($repoRoot, $Assembly.TestAssemblyPath).Replace([IO.Path]::DirectorySeparatorChar, "/") } } | Sort-Object fact)
}

function Invoke-MacOSPlatformContractAssembly { param([object]$Assembly, [Diagnostics.Stopwatch]$Stopwatch) $directory = Join-Path $ResultsRoot $Assembly.Assembly; New-Item -ItemType Directory -Path $directory -Force | Out-Null; $filter = (@($Assembly.Group | ForEach-Object { "FullyQualifiedName=$($_.Fact)" }) -join '|'); Invoke-MacOSPlatformContractPhase "macos-$($Assembly.Assembly)" "dotnet" @("test", $Assembly.Project, "-c", $Configuration, "--no-build", "--no-restore", "--filter", $filter, "--logger", "trx;LogFileName=macos-platform-contract.trx", "--results-directory", $directory, "/p:RestoreIgnoreFailedSources=true") $Stopwatch }

function Read-MacOSPlatformContractXml {
    param([string]$Path, [string]$Description)
    $file = Get-Item -LiteralPath $Path; Assert-MacOSPlatformContractCondition ($file.Length -gt 0 -and $file.Length -le $maximumTrxBytes) "$Description exceeds the bounded TRX evidence size."
    try { $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null; $settings.MaxCharactersInDocument = $maximumTrxBytes; $stream = [IO.File]::OpenRead($Path); try { $reader = [Xml.XmlReader]::Create($stream, $settings); try { $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader); return $document } finally { $reader.Dispose() } } finally { $stream.Dispose() } } catch { throw "$Description is malformed XML: $Path. $($_.Exception.Message)" }
}
function Get-MacOSPlatformContractDirectNodes { param([Xml.XmlNode]$Parent, [string]$Name) return @($Parent.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element -and $_.NamespaceURI -ceq $teamTestNamespace -and $_.LocalName -ceq $Name }) }
function Assert-MacOSPlatformContractDirectElementShape {
    param([Xml.XmlNode]$Parent, [string[]]$AllowedNames, [string]$Description)
    $unexpected = @($Parent.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element -and ($_.NamespaceURI -cne $teamTestNamespace -or $_.LocalName -notin $AllowedNames) })
    Assert-MacOSPlatformContractCondition ($unexpected.Count -eq 0) "$Description contains an unexpected direct child element."
}
function Get-MacOSPlatformContractRequiredAttribute { param([Xml.XmlNode]$Node, [string]$Name, [string]$Description) $attribute = $Node.Attributes.GetNamedItem($Name); Assert-MacOSPlatformContractCondition ($null -ne $attribute -and -not [string]::IsNullOrWhiteSpace($attribute.Value)) "$Description is missing '$Name'."; return $attribute.Value }
function Get-MacOSPlatformContractGuid {
    param([Xml.XmlNode]$Node, [string]$Name, [string]$Description)
    try {
        $value = [Guid]::Empty
        Assert-MacOSPlatformContractCondition ([Guid]::TryParse((Get-MacOSPlatformContractRequiredAttribute $Node $Name $Description), [ref]$value) -and $value -ne [Guid]::Empty) "$Description has a malformed '$Name'."
        return $value.ToString("D")
    }
    catch { throw "$Description has a malformed '$Name'." }
}
function Assert-MacOSPlatformContractAssemblyBinding {
    param([string]$Value, [string]$AssemblyPath, [string]$Description, [switch]$AllowCaseInsensitiveStorage)
    $expectedRelative = [IO.Path]::GetRelativePath($repoRoot, $AssemblyPath).Replace('\', '/')
    $expectedName = [IO.Path]::GetFileName($AssemblyPath)
    $actual = $Value.Replace('\', '/')
    $comparison = if ($AllowCaseInsensitiveStorage) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $isLexicallyRooted = $actual.StartsWith('/', [StringComparison]::Ordinal) -or $actual -match '^[A-Za-z]:/'
    $relativeActual = if ($actual.StartsWith('/', [StringComparison]::Ordinal)) { $actual.Substring(1) } elseif ($actual -match '^[A-Za-z]:/') { $actual.Substring(3) } else { $actual }
    Assert-MacOSPlatformContractCondition (@($relativeActual.Split('/') | Where-Object { $_ -in @("", ".", "..") }).Count -eq 0) "$Description contains an unsafe assembly path."
    $matches = if ($isLexicallyRooted) { $actual.EndsWith('/' + $expectedRelative, $comparison) } else { $actual.Equals($expectedName, $comparison) }
    Assert-MacOSPlatformContractCondition $matches "$Description does not bind the selected test assembly."
}

function Read-MacOSPlatformContractResults {
    param([object[]]$Selection, [object[]]$Discoveries, [string]$ContractResultsRoot = $ResultsRoot)
    Assert-MacOSPlatformContractSelection $Selection
    Assert-MacOSPlatformContractDiscoveries $Selection $Discoveries
    $facts = [Collections.Generic.List[object]]::new(); $executions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($assembly in @(Get-MacOSPlatformContractAssemblies $Selection)) {
        $files = @(Get-ChildItem -LiteralPath (Join-Path $ContractResultsRoot $assembly.Assembly) -Filter "*.trx" -File); Assert-MacOSPlatformContractCondition ($files.Count -eq 1) "MacOSPlatformContract requires exactly one TRX for '$($assembly.Project)'; found $($files.Count)."
        $document = Read-MacOSPlatformContractXml $files[0].FullName "MacOSPlatformContract TRX for '$($assembly.Project)'"; Assert-MacOSPlatformContractCondition ($document.DocumentElement.LocalName -ceq "TestRun" -and $document.DocumentElement.NamespaceURI -ceq $teamTestNamespace) "MacOSPlatformContract TRX has an unexpected TeamTest namespace."
        $resultsContainers = @(Get-MacOSPlatformContractDirectNodes $document.DocumentElement "Results"); $definitionsContainers = @(Get-MacOSPlatformContractDirectNodes $document.DocumentElement "TestDefinitions"); $entriesContainers = @(Get-MacOSPlatformContractDirectNodes $document.DocumentElement "TestEntries"); $summaries = @(Get-MacOSPlatformContractDirectNodes $document.DocumentElement "ResultSummary")
        Assert-MacOSPlatformContractCondition ($resultsContainers.Count -eq 1 -and $definitionsContainers.Count -eq 1 -and $entriesContainers.Count -eq 1 -and $summaries.Count -eq 1) "MacOSPlatformContract TRX must contain exactly one result, definition, entry, and summary structure."
        Assert-MacOSPlatformContractDirectElementShape $resultsContainers[0] @("UnitTestResult") "MacOSPlatformContract TRX results"
        Assert-MacOSPlatformContractDirectElementShape $definitionsContainers[0] @("UnitTest") "MacOSPlatformContract TRX definitions"
        Assert-MacOSPlatformContractDirectElementShape $entriesContainers[0] @("TestEntry") "MacOSPlatformContract TRX entries"
        $results = @(Get-MacOSPlatformContractDirectNodes $resultsContainers[0] "UnitTestResult"); $definitions = @(Get-MacOSPlatformContractDirectNodes $definitionsContainers[0] "UnitTest"); $entries = @(Get-MacOSPlatformContractDirectNodes $entriesContainers[0] "TestEntry"); Assert-MacOSPlatformContractCondition ($results.Count -eq $assembly.Group.Count -and $definitions.Count -eq $assembly.Group.Count -and $entries.Count -eq $assembly.Group.Count) "MacOSPlatformContract TRX has unexpected, missing, or duplicate result records."
        $counters = @(Get-MacOSPlatformContractDirectNodes $summaries[0] "Counters"); Assert-MacOSPlatformContractCondition ((Get-MacOSPlatformContractRequiredAttribute $summaries[0] "outcome" "MacOSPlatformContract TRX summary") -ceq "Completed" -and $counters.Count -eq 1) "MacOSPlatformContract TRX summary must be completed with one counter structure."
        foreach ($name in @("total", "executed", "passed")) { Assert-MacOSPlatformContractCondition ([int](Get-MacOSPlatformContractRequiredAttribute $counters[0] $name "MacOSPlatformContract TRX counters") -eq $assembly.Group.Count) "MacOSPlatformContract TRX counter '$name' does not equal the selected fact count." }
        Assert-MacOSPlatformContractCondition ([int](Get-MacOSPlatformContractRequiredAttribute $counters[0] "completed" "MacOSPlatformContract TRX counters") -eq 0) "MacOSPlatformContract TRX counter 'completed' must be zero."
        foreach ($name in @("failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning", "inProgress", "pending")) { Assert-MacOSPlatformContractCondition ([int](Get-MacOSPlatformContractRequiredAttribute $counters[0] $name "MacOSPlatformContract TRX counters") -eq 0) "MacOSPlatformContract TRX counter '$name' must be zero." }
        foreach ($selected in $assembly.Group) {
            $discovery = @($Discoveries | Where-Object { [string]$_.project -ceq $assembly.Project -and [string]$_.fact -ceq $selected.Fact }); $result = @($results | Where-Object { [string]$_.testName -ceq $selected.Fact }); Assert-MacOSPlatformContractCondition ($discovery.Count -eq 1 -and $result.Count -eq 1 -and [string]$result[0].outcome -ceq "Passed") "MacOSPlatformContract requires one passing, non-skipped result for '$($selected.Fact)'."
            $testId = Get-MacOSPlatformContractGuid $result[0] "testId" "MacOSPlatformContract result"; $executionId = Get-MacOSPlatformContractGuid $result[0] "executionId" "MacOSPlatformContract result"; Assert-MacOSPlatformContractCondition ($testId -ceq [string]$discovery[0].testId -and $executions.Add($executionId)) "MacOSPlatformContract TRX contains crossed, foreign, or duplicate test execution identifiers."
            $definition = @($definitions | Where-Object { (Get-MacOSPlatformContractGuid $_ "id" "MacOSPlatformContract definition") -ceq $testId }); $entry = @($entries | Where-Object { (Get-MacOSPlatformContractGuid $_ "testId" "MacOSPlatformContract entry") -ceq $testId -and (Get-MacOSPlatformContractGuid $_ "executionId" "MacOSPlatformContract entry") -ceq $executionId }); Assert-MacOSPlatformContractCondition ($definition.Count -eq 1 -and $entry.Count -eq 1) "MacOSPlatformContract TRX does not join one exact definition and entry for '$($selected.Fact)'."
            $execution = @(Get-MacOSPlatformContractDirectNodes $definition[0] "Execution"); $method = @(Get-MacOSPlatformContractDirectNodes $definition[0] "TestMethod"); $parts = $selected.Fact -split '\.'; $class = $parts[0..($parts.Count - 2)] -join "."; Assert-MacOSPlatformContractCondition ((Get-MacOSPlatformContractRequiredAttribute $definition[0] "name" "MacOSPlatformContract definition") -ceq $selected.Fact -and $execution.Count -eq 1 -and $method.Count -eq 1 -and (Get-MacOSPlatformContractGuid $execution[0] "id" "MacOSPlatformContract definition execution") -ceq $executionId -and (Get-MacOSPlatformContractRequiredAttribute $method[0] "className" "MacOSPlatformContract method") -ceq $class -and (Get-MacOSPlatformContractRequiredAttribute $method[0] "name" "MacOSPlatformContract method") -ceq $parts[-1]) "MacOSPlatformContract TRX definition provenance is invalid for '$($selected.Fact)'."
            Assert-MacOSPlatformContractAssemblyBinding (Get-MacOSPlatformContractRequiredAttribute $definition[0] "storage" "MacOSPlatformContract definition") $assembly.TestAssemblyPath "MacOSPlatformContract TRX storage" -AllowCaseInsensitiveStorage; Assert-MacOSPlatformContractAssemblyBinding (Get-MacOSPlatformContractRequiredAttribute $method[0] "codeBase" "MacOSPlatformContract method") $assembly.TestAssemblyPath "MacOSPlatformContract TRX codeBase"
            $fact = [ordered]@{ project = $assembly.Project; source = $selected.Source; assembly = $assembly.Assembly; fact = $selected.Fact; outcome = "Passed"; testId = $testId; executionId = $executionId; xunitTestCaseUniqueId = [string]$discovery[0].xunitTestCaseUniqueId; trx = [IO.Path]::GetRelativePath((Split-Path -Parent $ContractResultsRoot), $files[0].FullName).Replace([IO.Path]::DirectorySeparatorChar, "/") }; if ($null -ne $selected.PSObject.Properties["HelperSource"]) { $fact.helperSource = $selected.HelperSource }; $facts.Add([pscustomobject]$fact)
        }
    }
    Assert-MacOSPlatformContractCondition ($facts.Count -eq 15 -and @($facts | Group-Object fact | Where-Object Count -ne 1).Count -eq 0 -and @($facts | Group-Object testId | Where-Object Count -ne 1).Count -eq 0 -and @($facts | Group-Object executionId | Where-Object Count -ne 1).Count -eq 0) "MacOSPlatformContract raw TRX reconciliation is incomplete or duplicated."
    return @($facts | Sort-Object fact)
}

if (-not $NoRun) {
    . (Join-Path $PSScriptRoot "verification-phase.ps1")
    if ([string]::IsNullOrWhiteSpace($ResultsRoot)) { throw "MacOSPlatformContract requires a result root when executing." }
    if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { throw "MacOSPlatformContract requires macOS." }
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::Arm64 -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::Arm64) { throw "MacOSPlatformContract requires ARM64 operating-system and PowerShell process architectures." }
    $selection = @(Get-MacOSPlatformContractSelection); Assert-MacOSPlatformContractSelection $selection
    if (Test-Path -LiteralPath $ResultsRoot) { Remove-Item -LiteralPath $ResultsRoot -Recurse -Force }; New-Item -ItemType Directory -Path $ResultsRoot -Force | Out-Null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew(); $assemblies = @(Get-MacOSPlatformContractAssemblies $selection); Invoke-MacOSPlatformContractPhase "build-macos-platform-contract" "dotnet" @("build", "EmbodySense.sln", "-c", $Configuration, "/p:RestoreIgnoreFailedSources=true") $stopwatch
    $discoveryRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-macos-platform-discovery-" + [Guid]::NewGuid().ToString("N")); New-Item -ItemType Directory -Path $discoveryRoot -Force | Out-Null
    try { $discoveries = [Collections.Generic.List[object]]::new(); foreach ($assembly in $assemblies) { $discoveries.AddRange([object[]](Invoke-MacOSPlatformContractDiscovery $assembly $stopwatch $discoveryRoot)); Invoke-MacOSPlatformContractAssembly $assembly $stopwatch }; $facts = @(Read-MacOSPlatformContractResults $selection @($discoveries) $ResultsRoot); [IO.File]::WriteAllText((Join-Path $ResultsRoot "macos-platform-contract-results.json"), ([ordered]@{ schemaVersion = 1; discoveries = @($discoveries | Sort-Object fact); facts = $facts } | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false)) } finally { if (Test-Path -LiteralPath $discoveryRoot) { Remove-Item -LiteralPath $discoveryRoot -Recurse -Force } }
    $stopwatch.Stop(); Write-Output "VERIFY_MACOS_PLATFORM_CONTRACT schema_version=1 status=passed facts=15 assemblies=6 elapsed_seconds=$($stopwatch.Elapsed.TotalSeconds.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))"
}
