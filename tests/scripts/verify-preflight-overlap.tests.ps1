Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$scheduleScriptPath = Join-Path $repoRoot "scripts\verification-schedule.ps1"
$artifactScriptPath = Join-Path $repoRoot "scripts\verification-artifacts.ps1"
$tempScriptPath = Join-Path $repoRoot "scripts\verification-temp.ps1"
$testLaneScriptPath = Join-Path $repoRoot "scripts\verification-test-lanes.ps1"
$testPlanScriptPath = Join-Path $repoRoot "scripts\verification-test-plan.ps1"
$prepareTestPlanScriptPath = Join-Path $repoRoot "scripts\prepare-verification-test-plan.ps1"
$frontendScriptPath = Join-Path $repoRoot "scripts\verify-frontend.ps1"
$verifyScript = Get-Content -LiteralPath $verifyScriptPath -Raw
$scheduleScript = Get-Content -LiteralPath $scheduleScriptPath -Raw
$testPlanScript = Get-Content -LiteralPath $testPlanScriptPath -Raw
$prepareTestPlanScript = Get-Content -LiteralPath $prepareTestPlanScriptPath -Raw
$frontendScript = Get-Content -LiteralPath $frontendScriptPath -Raw
$powerShellExecutable = (Get-Process -Id $PID).Path
$nodeExecutable = (Get-Command node -CommandType Application -ErrorAction Stop).Source
$parallelProbePath = Join-Path $PSScriptRoot "verification-parallel-probe.mjs"
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:assertionCount++
}

function Assert-Contains {
    param([string]$Actual, [string]$Expected, [string]$Message)
    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'."
}

function Assert-NotContains {
    param([string]$Actual, [string]$Expected, [string]$Message)
    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) -Message "$Message Unexpected '$Expected'."
}

function Assert-OccurrenceCount {
    param([string]$Actual, [string]$Expected, [int]$ExpectedCount, [string]$Message)

    $actualCount = [regex]::Matches($Actual, [regex]::Escape($Expected)).Count
    Assert-True -Condition ($actualCount -eq $ExpectedCount) -Message "$Message Expected $ExpectedCount occurrence(s) of '$Expected'; found $actualCount."
}

function Normalize-ConsoleDiagnostic {
    param([AllowEmptyString()] [string]$Value)

    $withoutAnsi = [regex]::Replace($Value, "`e\[[0-?]*[ -/]*[@-~]", "")
    return [regex]::Replace($withoutAnsi, "\s+", " ").Trim()
}

. $phaseScriptPath
. $parallelScriptPath
. $scheduleScriptPath
. $artifactScriptPath
. $tempScriptPath
. $testLaneScriptPath
. $testPlanScriptPath

Assert-Contains -Actual $verifyScript -Expected '$normalPullRequestVerification = $VerificationTier -eq "PullRequest" -and -not $BrowserE2EOnly' -Message "Only the complete pull-request verifier may use the split preflight."
Assert-Contains -Actual $verifyScript -Expected '$preflightProcessHeavyWeight = [Math]::Min(3, [Math]::Max(1, [int][Math]::Ceiling($preflightResourceCapacity / 2.0)))' -Message "The preflight build must retain bounded weight while leaving one nested-process slot on a four-core host."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "build-pullrequest" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "build-pullrequest.log") -EstimatedDurationSeconds 90 -Weight $preflightProcessHeavyWeight -ResourceClass "ProcessHeavy"' -Message "The canonical build must be a bounded, logged, process-heavy preflight phase."
Assert-OccurrenceCount -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "build-pullrequest"' -ExpectedCount 1 -Message "The canonical Release build must be declared exactly once."
Assert-Contains -Actual $verifyScript -Expected 'kind=pull-request-preflight-dag phases=$($script:VerificationParallelPhases.Count)' -Message "Build, frontend, contracts, and the build-dependent coverage contract must publish one bounded DAG schedule."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-VerificationParallelPhases -MaximumWorkers $preflightMaximumWorkers -MaximumResourceCapacity $preflightResourceCapacity -MaximumProcessHeavyWorkers $preflightMaximumProcessHeavyWorkers -MaximumCpuBoundWorkers 1 | Out-Null' -Message "The preflight DAG must apply all process, resource, and class bounds."
Assert-NotContains -Actual $verifyScript -Expected '$script:LastCompletedVerificationPhase = "pull-request-build-overlap"' -Message "The verifier cannot claim a partial preflight boundary before every DAG phase passes."
Assert-Contains -Actual $verifyScript -Expected '$preflightMaximumWorkers = [Math]::Min(4, $hardwareBoundedResourceCapacity)' -Message "Preflight must cap actual tool processes at four even on larger hosts."
Assert-Contains -Actual $verifyScript -Expected '$preflightResourceCapacity = [Math]::Min(8, [Math]::Max(1, $preflightMaximumWorkers * 2))' -Message "Preflight must expose enough logical capacity for build, frontend, and one nested contract without exceeding four processes."
Assert-Contains -Actual $verifyScript -Expected '$preflightCoverageContractWeight = Get-VerificationPreflightCoverageContractWeight -ResourceCapacity $preflightResourceCapacity' -Message "The coverage contract must use the behavior-tested adaptive resource-weight derivation."
Assert-Contains -Actual $verifyScript -Expected '$preflightFrontendWeight = Get-VerificationPreflightFrontendWeight -ResourceCapacity $preflightResourceCapacity' -Message "The composed frontend phase must use a behavior-tested adaptive physical-capacity weight."
Assert-Contains -Actual $verifyScript -Expected '$preflightNestedProcessContractWeight = Get-VerificationPreflightNestedProcessContractWeight -ResourceCapacity $preflightResourceCapacity' -Message "Nested-process script contracts must use the behavior-tested process-heavy preflight weight."
Assert-Contains -Actual $verifyScript -Expected '$preflightTestPlanWeight = Get-VerificationPreflightTestPlanWeight -ResourceCapacity $preflightResourceCapacity' -Message "Immutable output preparation, discovery, and partitioning must use one behavior-tested preflight weight."
Assert-Contains -Actual $verifyScript -Expected '$preflightMaximumProcessHeavyWorkers = [Math]::Min(2, $preflightMaximumWorkers)' -Message "The DAG must admit no more than two process-heavy preflight workers."
Assert-Contains -Actual $scheduleScript -Expected 'return [Math]::Min(3, $ResourceCapacity)' -Message "The shared preflight coverage derivation must clamp its weight to the available capacity."
Assert-Contains -Actual $scheduleScript -Expected 'return [Math]::Min(2, $ResourceCapacity)' -Message "The shared frontend derivation must use two physical units where available and remain admissible on smaller hosts."
Assert-Contains -Actual $scheduleScript -Expected 'function Get-VerificationPreflightNestedProcessContractWeight {' -Message "Nested-process script contract admission must have one shared scheduling policy."
Assert-Contains -Actual $scheduleScript -Expected 'function Get-VerificationPreflightTestPlanWeight {' -Message "Build-dependent test-plan preparation must have one shared scheduling policy."
Assert-True -Condition ([regex]::Matches($scheduleScript, [regex]::Escape('return [Math]::Min(3, $ResourceCapacity)')).Count -eq 3) -Message "Coverage, nested-process contracts, and test-plan preparation must each reserve three logical units where available."
Assert-Contains -Actual $scheduleScript -Expected 'function Assert-VerificationPreflightContractClassification {' -Message "Preflight script contract classification must have one shared fail-closed validator."
Assert-Contains -Actual $verifyScript -Expected 'Assert-VerificationPreflightContractClassification -ContractScripts $contractScripts -CoverageContractScript "verify-coverage.tests.ps1" -NestedProcessContractScripts $preflightNestedProcessContractScripts -OrdinaryContractScripts $preflightOrdinaryContractScripts' -Message "The complete platform-applicable contract manifest must be classified before scheduling begins."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "contract-verify-coverage.tests" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "verify-coverage.tests.ps1.log") -DependsOn @("build-pullrequest") -EstimatedDurationSeconds 75 -Weight $preflightCoverageContractWeight -ResourceClass "ProcessHeavy"' -Message "The coverage contract must retain its profile and an explicit successful-build dependency."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "prepare-test-plan" -FileName $powerShellExecutable -Arguments $testPlanArguments -TimeoutSeconds 240 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "prepare-test-plan.log") -DependsOn @("build-pullrequest") -EstimatedDurationSeconds 60 -Weight $preflightTestPlanWeight -ResourceClass "ProcessHeavy"' -Message "Immutable output copying, discovery, and partition reconciliation must be one bounded build-dependent preflight phase."
Assert-OccurrenceCount -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "prepare-test-plan"' -ExpectedCount 1 -Message "Test-plan preparation must have exactly one scheduler owner."
Assert-Contains -Actual $prepareTestPlanScript -Expected 'Write-VerificationTestPreparationPlan -PlanPath $planPath' -Message "The child preparation phase must publish one authenticated schema-1 handoff only after partition reconciliation succeeds."
Assert-Contains -Actual $testPlanScript -Expected 'Read-VerificationTestPreparationPlan' -Message "The parent verifier must hydrate the child plan through the exact shared schema reader."
Assert-Contains -Actual $verifyScript -Expected '$isolations = @(Read-VerificationTestPreparationPlan -PlanPath $verificationTestPreparationPlanPath' -Message "Required gates must consume the validated build-dependent preparation plan instead of repeating output copying and discovery."
Assert-Contains -Actual $verifyScript -Expected 'elseif ($preflightNestedProcessContractScripts -ccontains $contractScript) {' -Message "Known descendant-heavy script contracts must be classified explicitly before fail-closed rejection."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 60 -Weight $preflightNestedProcessContractWeight -ResourceClass "ProcessHeavy"' -Message "Descendant-heavy script contracts must receive bounded process-heavy admission."
Assert-Contains -Actual $verifyScript -Expected 'if ($preflightOrdinaryContractScripts -ccontains $contractScript) {' -Message "Only explicitly classified descendant-free contracts may receive the Ordinary profile."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 90 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"' -Message "Every other independent script contract must retain the original bounded Ordinary profile."
Assert-OccurrenceCount -Actual $verifyScript -Expected '            "verify-coverage.tests.ps1",' -ExpectedCount 1 -Message "The coverage contract must appear exactly once in the execution manifest."
foreach ($nestedProcessContract in @("verify-preflight-overlap.tests.ps1", "verify-parallel.tests.ps1")) {
    Assert-OccurrenceCount -Actual $verifyScript -Expected "`"$nestedProcessContract`"" -ExpectedCount 2 -Message "Each descendant-heavy contract must have exactly one execution-manifest entry and one isolation-classification entry."
}
foreach ($ordinaryContract in @("verify-bounded-phases.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1")) {
    Assert-OccurrenceCount -Actual $verifyScript -Expected "`"$ordinaryContract`"" -ExpectedCount 2 -Message "Each build-safe contract must have exactly one execution-manifest entry and one explicit overlap-classification entry."
}
Assert-OccurrenceCount -Actual $verifyScript -Expected '"verify-sdk-diagnostics.tests.ps1"' -ExpectedCount 2 -Message "The Windows process-tree diagnostic contract must be present once and classified once as nested-process work."
Assert-Contains -Actual $verifyScript -Expected 'throw "Preflight script contract ''$contractScript'' reached execution without a resource classification."' -Message "The execution loop must fail closed if validated classification state is ever bypassed."
Assert-NotContains -Actual $verifyScript -Expected '-TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"' -Message "Coverage timeout headroom cannot become a blanket extension for Ordinary contracts."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "frontend-preflight" -FileName $powerShellExecutable -Arguments $frontendArguments -TimeoutSeconds 590 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "frontend-preflight.log") -EstimatedDurationSeconds 70 -Weight $preflightFrontendWeight -ResourceClass "CpuBound"' -Message "The install-and-test frontend dependency chain must own one bounded CPU profile during preflight."
Assert-OccurrenceCount -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "frontend-preflight"' -ExpectedCount 1 -Message "The composed frontend phase must be declared exactly once."
Assert-Contains -Actual $verifyScript -Expected 'Add-ProfiledRequiredGatePhase -Name "format-csharp" -FileName "dotnet" -Arguments @("format", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal") -TimeoutSeconds 240 -OutputPath (Join-Path $verificationLogsPath "format-csharp.log")' -Message "One authoritative formatter process must enforce whitespace and IDE1006 after immutable test outputs exist."
Assert-OccurrenceCount -Actual $verifyScript -Expected 'Add-ProfiledRequiredGatePhase -Name "format-csharp"' -ExpectedCount 1 -Message "Combined C# format validation must have exactly one scheduler owner."
Assert-NotContains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "format-csharp"' -Message "Combined C# formatting must not be duplicated in preflight."
Assert-NotContains -Actual $verifyScript -Expected '@("format", "whitespace", "EmbodySense.sln"' -Message "Whitespace validation must not reload the solution separately."
Assert-NotContains -Actual $verifyScript -Expected '@("format", "style", "EmbodySense.sln"' -Message "IDE1006 validation must not reload the solution separately."
Assert-Contains -Actual $verifyScript -Expected 'coverage_dependency=build-pullrequest test_plan_dependency=build-pullrequest' -Message "The preflight plan must publish both exact build-dependent edges."
Assert-Contains -Actual $verifyScript -Expected 'nested_process_contracts=$($preflightNestedProcessContractScripts.Count) ordinary_contracts=$($preflightOrdinaryContractScripts.Count)' -Message "The preflight plan must publish its exact classified contract inventory."
Assert-NotContains -Actual $verifyScript -Expected 'kind=discovery phases=$($script:VerificationParallelPhases.Count)' -Message "Canonical discovery must not be repeated after the build-dependent preparation plan passes."
Assert-OccurrenceCount -Actual $verifyScript -Expected 'Invoke-VerificationParallelPhases -MaximumWorkers $preflightMaximumWorkers -MaximumResourceCapacity $preflightResourceCapacity -MaximumProcessHeavyWorkers $preflightMaximumProcessHeavyWorkers -MaximumCpuBoundWorkers 1 | Out-Null' -ExpectedCount 1 -Message "The complete preflight DAG must have exactly one bounded scheduler invocation."
Assert-Contains -Actual $verifyScript -Expected '$script:LastCompletedVerificationPhase = "pull-request-preflight"' -Message "Later failures must identify the successful preflight dependency boundary."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "build-$($VerificationTier.ToLowerInvariant())" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900' -Message "Stress and browser-only verification must retain the sequential canonical build path."
Assert-NotContains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "npm-ci"' -Message "npm restore cannot be repeated after the post-build preflight."
Assert-NotContains -Actual $verifyScript -Expected 'Add-ProfiledRequiredGatePhase -Name "frontend-tests"' -Message "Frontend tests must not execute a second time in the required-gate plan."
Assert-Contains -Actual $frontendScript -Expected 'Invoke-NpmVerificationPhase -Name "npm-ci" -NpmArguments @("ci", "--include=dev")' -Message "The composed phase must install the exact development dependency set."
Assert-Contains -Actual $frontendScript -Expected 'Invoke-NpmVerificationPhase -Name "frontend-tests" -NpmArguments @("test")' -Message "The composed phase must execute the unchanged frontend test command after install."
Assert-Contains -Actual $frontendScript -Expected '[int]$InstallTimeoutSeconds = 300' -Message "The production frontend install must retain its exact five-minute default timeout."
Assert-Contains -Actual $frontendScript -Expected '[int]$TestTimeoutSeconds = 300' -Message "The production frontend test command must retain its exact five-minute default timeout."
Assert-Contains -Actual $frontendScript -Expected '@("/d", "/s", "/c", "npm.cmd $($NpmArguments -join '' '')")' -Message "Windows frontend commands must retain explicit cmd.exe argument handling."
Assert-Contains -Actual $frontendScript -Expected 'Add-VerificationParallelPhase -Name $Name' -Message "Each frontend dependency must retain bounded scheduler ownership and a distinct failure name."
Assert-Contains -Actual $frontendScript -Expected '-OutputPath (Join-Path $logsPathRoot "$Name.log")' -Message "Install and test output must remain in distinct diagnostic logs."

$classifiedContracts = @("coverage.ps1", "nested-one.ps1", "nested-two.ps1", "ordinary.ps1")
Assert-VerificationPreflightContractClassification -ContractScripts $classifiedContracts -CoverageContractScript "coverage.ps1" -NestedProcessContractScripts @("nested-one.ps1", "nested-two.ps1") -OrdinaryContractScripts @("ordinary.ps1")
foreach ($invalidClassificationCase in @(
    [pscustomobject]@{ Name = "missing"; Contracts = $classifiedContracts; Coverage = "coverage.ps1"; Nested = @("nested-one.ps1"); Ordinary = @("ordinary.ps1"); Expected = "missing_classifications=[nested-two.ps1]" }
    [pscustomobject]@{ Name = "duplicate"; Contracts = $classifiedContracts; Coverage = "coverage.ps1"; Nested = @("nested-one.ps1", "nested-two.ps1"); Ordinary = @("nested-one.ps1", "ordinary.ps1"); Expected = "duplicate_classifications=[nested-one.ps1]" }
    [pscustomobject]@{ Name = "unexpected"; Contracts = $classifiedContracts; Coverage = "coverage.ps1"; Nested = @("nested-one.ps1", "nested-two.ps1"); Ordinary = @("ordinary.ps1", "stale.ps1"); Expected = "unexpected_classifications=[stale.ps1]" }
)) {
    try {
        Assert-VerificationPreflightContractClassification -ContractScripts $invalidClassificationCase.Contracts -CoverageContractScript $invalidClassificationCase.Coverage -NestedProcessContractScripts $invalidClassificationCase.Nested -OrdinaryContractScripts $invalidClassificationCase.Ordinary
        throw "Expected $($invalidClassificationCase.Name) preflight classification failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected $invalidClassificationCase.Expected -Message "Preflight contract classification must fail closed for a $($invalidClassificationCase.Name) classification."
    }
}

$admissionRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-preflight-coverage-admission-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $admissionRoot | Out-Null
try {
    foreach ($admissionCase in @(
        [pscustomobject]@{ Capacity = 1; ExpectedCoverageWeight = 1; ExpectedFrontendWeight = 1 }
        [pscustomobject]@{ Capacity = 2; ExpectedCoverageWeight = 2; ExpectedFrontendWeight = 2 }
        [pscustomobject]@{ Capacity = 4; ExpectedCoverageWeight = 3; ExpectedFrontendWeight = 2 }
        [pscustomobject]@{ Capacity = 8; ExpectedCoverageWeight = 3; ExpectedFrontendWeight = 2 }
    )) {
        Reset-VerificationParallelPhaseState
        $coverageWeight = Get-VerificationPreflightCoverageContractWeight -ResourceCapacity $admissionCase.Capacity
        $frontendWeight = Get-VerificationPreflightFrontendWeight -ResourceCapacity $admissionCase.Capacity
        $nestedProcessContractWeight = Get-VerificationPreflightNestedProcessContractWeight -ResourceCapacity $admissionCase.Capacity
        $testPlanWeight = Get-VerificationPreflightTestPlanWeight -ResourceCapacity $admissionCase.Capacity
        $outputPath = Join-Path $admissionRoot "capacity-$($admissionCase.Capacity).log"
        Add-VerificationParallelPhase -Name "coverage-capacity-$($admissionCase.Capacity)" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-Command", "exit 0") -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $outputPath -EstimatedDurationSeconds 75 -Weight $coverageWeight -ResourceClass "ProcessHeavy"
        $results = @(Invoke-VerificationParallelPhases -MaximumWorkers $admissionCase.Capacity -MaximumResourceCapacity $admissionCase.Capacity)
        Assert-True -Condition ($results.Count -eq 1 -and $results[0].Weight -eq $admissionCase.ExpectedCoverageWeight -and $results[0].ResourceClass -ceq "ProcessHeavy") -Message "Coverage contract capacity $($admissionCase.Capacity) must be admitted as ProcessHeavy with effective weight $($admissionCase.ExpectedCoverageWeight)."
        Assert-True -Condition ($frontendWeight -eq $admissionCase.ExpectedFrontendWeight) -Message "Frontend capacity $($admissionCase.Capacity) must derive effective weight $($admissionCase.ExpectedFrontendWeight)."
        Assert-True -Condition ($nestedProcessContractWeight -eq [Math]::Min(3, $admissionCase.Capacity)) -Message "Nested-process contract capacity $($admissionCase.Capacity) must reserve at most three preflight units."
        Assert-True -Condition ($testPlanWeight -eq [Math]::Min(3, $admissionCase.Capacity)) -Message "Test-plan preparation capacity $($admissionCase.Capacity) must reserve at most three preflight units."
    }
}
finally {
    Reset-VerificationParallelPhaseState
    Remove-Item -LiteralPath $admissionRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$planFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-preflight-test-plan-" + [Guid]::NewGuid().ToString("N"))
$planFixturePhysicalTempRoot = Join-Path $(if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { "/private/tmp" } else { [IO.Path]::GetTempPath() }) ("esp-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $planFixtureRoot, $planFixturePhysicalTempRoot | Out-Null
try {
    $fixtureProjectName = "EmbodySense.Fixture.Tests"
    $fixtureProjectDirectory = Join-Path (Join-Path $planFixtureRoot "tests") $fixtureProjectName
    $fixtureProjectPath = Join-Path $fixtureProjectDirectory "$fixtureProjectName.csproj"
    $fixtureResultsRoot = Join-Path $planFixtureRoot "results"
    $fixtureIsolationRoot = Join-Path $fixtureResultsRoot "CoverageIsolation"
    $fixtureStandardResultsRoot = Join-Path $fixtureResultsRoot "StandardTests"
    $fixturePhysicalTempRoot = $planFixturePhysicalTempRoot
    $fixtureRunIdentity = [Guid]::NewGuid().ToString("N")
    New-Item -ItemType Directory -Path $fixtureProjectDirectory, $fixtureResultsRoot | Out-Null
    [IO.File]::WriteAllText($fixtureProjectPath, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>', [Text.UTF8Encoding]::new($false))
    $fixtureSourceDirectory = Join-Path $fixtureProjectDirectory "bin/Release/net10.0"
    New-Item -ItemType Directory -Path $fixtureSourceDirectory | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixtureSourceDirectory "$fixtureProjectName.dll"), "fixture", [Text.UTF8Encoding]::new($false))
    $fixtureProjectRoot = Join-Path $fixtureIsolationRoot $fixtureProjectName
    $fixturePristineDirectory = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $fixtureProjectRoot "canonical") -Configuration "Release" -TargetFramework "net10.0"
    New-Item -ItemType Directory -Path $fixturePristineDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $fixtureSourceDirectory "$fixtureProjectName.dll") -Destination $fixturePristineDirectory
    $fixtureCollectorDirectory = Join-Path $fixtureProjectRoot "Collector"
    New-Item -ItemType Directory -Path $fixtureCollectorDirectory | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixtureCollectorDirectory "collector.dll"), "collector", [Text.UTF8Encoding]::new($false))
    $fixtureRunSettingsPath = Join-Path $fixtureProjectRoot "verification-pull-request.runsettings"
    $fixtureChildRoot = Split-Path -Parent $fixturePristineDirectory
    $fixtureChildRunSettingsPath = Join-Path $fixtureChildRoot "verification-pull-request.runsettings"
    $fixtureLaneDirectory = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $fixtureProjectRoot "all") -Configuration "Release" -TargetFramework "net10.0"
    New-Item -ItemType Directory -Path $fixtureLaneDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $fixtureSourceDirectory "$fixtureProjectName.dll") -Destination $fixtureLaneDirectory
    $fixtureLaneIdentity = "$fixtureProjectName-all"
    $fixtureLaneRoot = Get-VerificationLaneFixturePath -PhysicalTempRoot $fixturePhysicalTempRoot -RunIdentity $fixtureRunIdentity -LaneIdentity $fixtureLaneIdentity
    New-Item -ItemType Directory -Path $fixtureLaneRoot | Out-Null
    $fixtureExceptions = [Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
    $fixtureLaneSelections = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $canonicalRunSettingsPath = Join-Path $repoRoot "tests/verification-pull-request.runsettings"
    $fixtureOwnership = [pscustomobject]@{
        CollectorVersion = "10.0.1"
        RunSettingsPath = $canonicalRunSettingsPath
        RunSettingsSha256 = (Get-FileHash -LiteralPath $canonicalRunSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        OwnershipSha256 = "b" * 64
        Owners = @([pscustomobject]@{ Package = "EmbodySense.Fixture"; SourceRoot = "src/EmbodySense.Fixture"; TestProject = $fixtureProjectName })
        ProductionFiles = @("src/EmbodySense.Fixture/Fixture.cs")
        ExceptionsByTestProject = $fixtureExceptions
        LaneSelectionsByTestProject = $fixtureLaneSelections
        TestProjectNames = @($fixtureProjectName)
    }
    $fixtureProject = [IO.FileInfo]::new($fixtureProjectPath)
    $fixtureSelection = Get-VerificationCoverageSelection -Ownership $fixtureOwnership -TestProject $fixtureProject
    $fixtureLaneDefinition = @(Get-VerificationTestProjectLanes -TestProject $fixtureProject)[0]
    $fixtureLaneFilter = Get-VerificationTestLaneFilter -Lane $fixtureLaneDefinition
    $fixtureLaneSelection = Get-VerificationCoverageLaneSelection -Ownership $fixtureOwnership -TestProject $fixtureProject -LaneName $fixtureLaneDefinition.Name
    Write-VerificationCoverageRunSettings -SourcePath $canonicalRunSettingsPath -DestinationPath $fixtureRunSettingsPath -Selection $fixtureSelection
    Copy-Item -LiteralPath $fixtureRunSettingsPath -Destination $fixtureChildRunSettingsPath
    $fixtureLaneSettingsRoot = Join-Path $fixtureProjectRoot "LaneSettings"
    New-Item -ItemType Directory -Path $fixtureLaneSettingsRoot | Out-Null
    $fixtureLaneRunSettingsPath = Join-Path $fixtureLaneSettingsRoot "all.runsettings"
    Write-VerificationCoverageRunSettings -SourcePath $canonicalRunSettingsPath -DestinationPath $fixtureLaneRunSettingsPath -Selection $fixtureLaneSelection
    $fixtureRunSettingsSha256 = (Get-FileHash -LiteralPath $fixtureRunSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $fixtureLaneRunSettingsSha256 = (Get-FileHash -LiteralPath $fixtureLaneRunSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $fixtureSourceManifest = @(Get-VerificationDirectoryManifest -Directory $fixtureSourceDirectory)
    $fixturePristineManifest = @(Get-VerificationDirectoryManifest -Directory $fixturePristineDirectory)
    $fixtureIsolation = [pscustomobject]@{
        Project = $fixtureProject
        SourceDirectory = $fixtureSourceDirectory
        SourceManifest = $fixtureSourceManifest
        PristineDirectory = $fixturePristineDirectory
        PristineManifest = $fixturePristineManifest
        CollectorDirectory = $fixtureCollectorDirectory
        RunSettingsPath = $fixtureRunSettingsPath
        RunSettingsSha256 = $fixtureRunSettingsSha256
        ChildRunSettingsPath = $fixtureChildRunSettingsPath
        ChildInvocationsRoot = Join-Path $fixtureChildRoot "Invocations"
        CoverageSelection = $fixtureSelection
        ChildResultsPath = Join-Path $fixtureChildRoot "Results"
        CanonicalAssemblyPath = Join-Path $fixturePristineDirectory "$fixtureProjectName.dll"
        Lanes = @([pscustomobject]@{
            Name = $fixtureLaneIdentity
            ProjectName = $fixtureProjectName
            ShardName = "all"
            Filter = $fixtureLaneFilter
            AssemblyPath = Join-Path $fixtureLaneDirectory "$fixtureProjectName.dll"
            Directory = $fixtureLaneDirectory
            Manifest = $fixturePristineManifest
            ResultsPath = Join-Path $fixtureStandardResultsRoot $fixtureLaneIdentity
            FixtureRoot = $fixtureLaneRoot
            RunSettingsPath = $fixtureLaneRunSettingsPath
            RunSettingsSha256 = $fixtureLaneRunSettingsSha256
            CoverageSelection = $fixtureLaneSelection
            Environment = @{
                EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $fixtureLaneRoot "catalog-trust"
                TEMP = $fixtureLaneRoot
                TMP = $fixtureLaneRoot
                TMPDIR = $fixtureLaneRoot
            }
        })
    }
    $fixturePlanPath = Join-Path $fixtureResultsRoot "required-test-preparation.json"
    Write-VerificationTestPreparationPlan -PlanPath $fixturePlanPath -RepositoryRoot $planFixtureRoot -VerificationResultsPath $fixtureResultsRoot -Configuration "Release" -SkipCoverage $false -CoverageOwnershipMode "Standard" -FixtureRunIdentity $fixtureRunIdentity -CoverageOwnership $fixtureOwnership -Isolations @($fixtureIsolation)
    $hydrated = @(Read-VerificationTestPreparationPlan -PlanPath $fixturePlanPath -RepositoryRoot $planFixtureRoot -VerificationResultsPath $fixtureResultsRoot -CoverageIsolationRoot $fixtureIsolationRoot -StandardTestResultsRoot $fixtureStandardResultsRoot -VerificationPhysicalTempRoot $fixturePhysicalTempRoot -FixtureRunIdentity $fixtureRunIdentity -Configuration "Release" -SkipCoverage $false -CoverageOwnershipMode "Standard" -CoverageOwnership $fixtureOwnership -TestProjects @($fixtureProject))
    Assert-True -Condition ($hydrated.Count -eq 1 -and $hydrated[0].Lanes.Count -eq 1 -and $hydrated[0].Lanes[0].Environment.Count -eq 4) -Message "The schema-1 handoff must hydrate one exact project/lane/environment topology."
    [IO.File]::AppendAllText($fixtureLaneRunSettingsPath, "<!--tampered-->", [Text.UTF8Encoding]::new($false))
    try {
        Read-VerificationTestPreparationPlan -PlanPath $fixturePlanPath -RepositoryRoot $planFixtureRoot -VerificationResultsPath $fixtureResultsRoot -CoverageIsolationRoot $fixtureIsolationRoot -StandardTestResultsRoot $fixtureStandardResultsRoot -VerificationPhysicalTempRoot $fixturePhysicalTempRoot -FixtureRunIdentity $fixtureRunIdentity -Configuration "Release" -SkipCoverage $false -CoverageOwnershipMode "Standard" -CoverageOwnership $fixtureOwnership -TestProjects @($fixtureProject) | Out-Null
        throw "Expected tampered lane runsettings to fail."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "lane runsettings do not match the exact current ownership selection" -Message "The parent handoff must authenticate each lane-specific filter before execution."
    }
    Write-VerificationCoverageRunSettings -SourcePath $canonicalRunSettingsPath -DestinationPath $fixtureLaneRunSettingsPath -Selection $fixtureLaneSelection
    $fixtureResiduePath = Join-Path $fixtureLaneRoot "unexpected-residue"
    [IO.File]::WriteAllText($fixtureResiduePath, "residue", [Text.UTF8Encoding]::new($false))
    try {
        Read-VerificationTestPreparationPlan -PlanPath $fixturePlanPath -RepositoryRoot $planFixtureRoot -VerificationResultsPath $fixtureResultsRoot -CoverageIsolationRoot $fixtureIsolationRoot -StandardTestResultsRoot $fixtureStandardResultsRoot -VerificationPhysicalTempRoot $fixturePhysicalTempRoot -FixtureRunIdentity $fixtureRunIdentity -Configuration "Release" -SkipCoverage $false -CoverageOwnershipMode "Standard" -CoverageOwnership $fixtureOwnership -TestProjects @($fixtureProject) | Out-Null
        throw "Expected a non-empty lane fixture root to fail."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "lane fixture root is not empty before execution" -Message "The parent handoff must fail closed if anything enters a reserved lane fixture root after child preparation."
    }
    Remove-Item -LiteralPath $fixtureResiduePath -Force
    $invalidPlanPath = Join-Path $fixtureResultsRoot "invalid-test-preparation.json"
    $invalidPlan = (Get-Content -LiteralPath $fixturePlanPath -Raw).Replace('"configuration": "Release"', '"configuration": "Debug"', [StringComparison]::Ordinal)
    [IO.File]::WriteAllText($invalidPlanPath, $invalidPlan, [Text.UTF8Encoding]::new($false))
    try {
        Read-VerificationTestPreparationPlan -PlanPath $invalidPlanPath -RepositoryRoot $planFixtureRoot -VerificationResultsPath $fixtureResultsRoot -CoverageIsolationRoot $fixtureIsolationRoot -StandardTestResultsRoot $fixtureStandardResultsRoot -VerificationPhysicalTempRoot $fixturePhysicalTempRoot -FixtureRunIdentity $fixtureRunIdentity -Configuration "Release" -SkipCoverage $false -CoverageOwnershipMode "Standard" -CoverageOwnership $fixtureOwnership -TestProjects @($fixtureProject) | Out-Null
        throw "Expected mismatched preparation configuration to fail."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "does not match its exact schema, configuration, coverage mode, or run identity" -Message "The schema-1 handoff must fail closed when its configuration is substituted."
    }
}
finally {
    Remove-Item -LiteralPath $planFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $planFixturePhysicalTempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

function Get-PreflightTimingArguments {
    param(
        [string]$ScriptPath,
        [string]$Role,
        [string]$SynchronizationRoot
    )

    return [string[]]@($ScriptPath, "timed", $Role, $SynchronizationRoot)
}

function Read-PreflightTiming {
    param([string]$Path)

    $values = @{}
    $timingLines = @()
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^(start|end)=(\d+)$') {
            $timingLines += $line
            if ($values.ContainsKey($Matches[1])) {
                throw "Preflight timing evidence contains duplicate '$($Matches[1])' entries: $Path"
            }
            $values[$Matches[1]] = [long]$Matches[2]
        }
    }
    if ($timingLines.Count -ne 2 -or -not $values.ContainsKey("start") -or -not $values.ContainsKey("end")) {
        throw "Preflight timing evidence is incomplete: $Path"
    }

    return [pscustomobject]@{ Start = $values.start; End = $values.end }
}

$behaviorRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-preflight-dependency-boundary-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $behaviorRoot | Out-Null
try {
    Assert-True -Condition (Test-Path -LiteralPath $parallelProbePath -PathType Leaf) -Message "The preflight DAG must use the checked-in lightweight cross-platform child-process probe."
    $timingScriptPath = $parallelProbePath

    Reset-VerificationParallelPhaseState
    $buildOutputPath = Join-Path $behaviorRoot "build.log"
    $coverageOutputPath = Join-Path $behaviorRoot "coverage.log"
    $prepareOutputPath = Join-Path $behaviorRoot "prepare.log"
    $frontendOutputPath = Join-Path $behaviorRoot "frontend.log"
    $ordinaryOutputPath = Join-Path $behaviorRoot "ordinary.log"
    $nestedFirstOutputPath = Join-Path $behaviorRoot "nested-first.log"
    $nestedSecondOutputPath = Join-Path $behaviorRoot "nested-second.log"
    $timingPhaseTimeoutSeconds = 30
    Add-VerificationParallelPhase -Name "build" -FileName $nodeExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "build" -SynchronizationRoot $behaviorRoot) -TimeoutSeconds $timingPhaseTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $buildOutputPath -EstimatedDurationSeconds 90 -Weight 3 -ResourceClass "ProcessHeavy"
    Add-VerificationParallelPhase -Name "frontend" -FileName $nodeExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "frontend" -SynchronizationRoot $behaviorRoot) -TimeoutSeconds $timingPhaseTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $frontendOutputPath -EstimatedDurationSeconds 70 -Weight 2 -ResourceClass "CpuBound"
    Add-VerificationParallelPhase -Name "ordinary" -FileName $nodeExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "ordinary" -SynchronizationRoot $behaviorRoot) -TimeoutSeconds $timingPhaseTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $ordinaryOutputPath -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"
    Add-VerificationParallelPhase -Name "coverage" -FileName $nodeExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "coverage" -SynchronizationRoot $behaviorRoot) -TimeoutSeconds $timingPhaseTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $coverageOutputPath -DependsOn @("build") -EstimatedDurationSeconds 75 -Weight 3 -ResourceClass "ProcessHeavy"
    Add-VerificationParallelPhase -Name "prepare" -FileName $nodeExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "prepare" -SynchronizationRoot $behaviorRoot) -TimeoutSeconds $timingPhaseTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $prepareOutputPath -DependsOn @("build") -EstimatedDurationSeconds 60 -Weight 3 -ResourceClass "ProcessHeavy"
    Add-VerificationParallelPhase -Name "nested-first" -FileName $nodeExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "nested-first" -SynchronizationRoot $behaviorRoot) -TimeoutSeconds $timingPhaseTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $nestedFirstOutputPath -EstimatedDurationSeconds 60 -Weight 3 -ResourceClass "ProcessHeavy"
    Add-VerificationParallelPhase -Name "nested-second" -FileName $nodeExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "nested-second" -SynchronizationRoot $behaviorRoot) -TimeoutSeconds $timingPhaseTimeoutSeconds -WorkingDirectory $repoRoot -OutputPath $nestedSecondOutputPath -EstimatedDurationSeconds 60 -Weight 3 -ResourceClass "ProcessHeavy"
    $overlapResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 4 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
    Assert-True -Condition ($overlapResults.Count -eq 7 -and @($overlapResults | Where-Object { $_.ExitCode -ne 0 }).Count -eq 0) -Message "The bounded preflight DAG must complete its exact phase set successfully."
    Assert-True -Condition (@($overlapResults | Select-Object -ExpandProperty Name | Sort-Object) -join "," -ceq "build,coverage,frontend,nested-first,nested-second,ordinary,prepare") -Message "The preflight DAG must contain only build-dependent coverage/preparation, frontend, nested contracts, and explicitly safe ordinary work."

    $buildTiming = Read-PreflightTiming -Path $buildOutputPath
    $coverageTiming = Read-PreflightTiming -Path $coverageOutputPath
    $prepareTiming = Read-PreflightTiming -Path $prepareOutputPath
    $frontendTiming = Read-PreflightTiming -Path $frontendOutputPath
    $ordinaryTiming = Read-PreflightTiming -Path $ordinaryOutputPath
    $nestedFirstTiming = Read-PreflightTiming -Path $nestedFirstOutputPath
    $nestedSecondTiming = Read-PreflightTiming -Path $nestedSecondOutputPath
    Assert-True -Condition ($frontendTiming.Start -lt $buildTiming.End -and $buildTiming.Start -lt $frontendTiming.End) -Message "The independent frontend chain must overlap the canonical build."
    Assert-True -Condition ($ordinaryTiming.Start -lt $buildTiming.End) -Message "A build-safe contract must backfill capacity after the faster frontend chain completes."
    Assert-True -Condition ($coverageTiming.Start -ge $buildTiming.End) -Message "The coverage contract must start only after the canonical build completes."
    Assert-True -Condition ($prepareTiming.Start -ge $buildTiming.End) -Message "Test-plan preparation must start only after the canonical build completes."
    Assert-True -Condition ($prepareTiming.Start -lt $coverageTiming.End -and $coverageTiming.Start -lt $prepareTiming.End) -Message "Coverage contracts and immutable test-plan preparation must overlap in the two admitted post-build process-heavy slots."
    Assert-True -Condition ($coverageTiming.Start -lt $ordinaryTiming.End -and $ordinaryTiming.Start -lt $coverageTiming.End) -Message "Coverage must backfill beside remaining build-safe work instead of extending the serialized post-build tail."
    foreach ($nestedTiming in @($nestedFirstTiming, $nestedSecondTiming)) {
        Assert-True -Condition ($nestedTiming.Start -lt $buildTiming.End -and $buildTiming.Start -lt $nestedTiming.End) -Message "Each descendant-heavy contract must consume the second process-heavy slot while the build is active."
    }
    Assert-True -Condition ($nestedFirstTiming.End -le $nestedSecondTiming.Start -or $nestedSecondTiming.End -le $nestedFirstTiming.Start) -Message "Nested-process contracts must remain one-at-a-time beside the build."
}
finally {
    Reset-VerificationParallelPhaseState
    Remove-Item -LiteralPath $behaviorRoot -Recurse -Force -ErrorAction SilentlyContinue
}

function Invoke-FrontendFixture {
    param(
        [string]$Name,
        [string]$FixtureRoot,
        [string]$FakeBinPath,
        [int]$InstallExitCode = 0,
        [int]$TestExitCode = 0,
        [int]$InstallDelayMilliseconds = 0,
        [int]$InstallTimeoutSeconds = 5,
        [int]$TestTimeoutSeconds = 5,
        [switch]$SeedStaleFrontendLog
    )

    $scenarioRoot = Join-Path $FixtureRoot $Name
    $logsPath = Join-Path $scenarioRoot "logs"
    $orderPath = Join-Path $scenarioRoot "order.txt"
    $pidPath = Join-Path $scenarioRoot "pid.txt"
    New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
    if ($SeedStaleFrontendLog) {
        New-Item -ItemType Directory -Path $logsPath | Out-Null
        [IO.File]::WriteAllText((Join-Path $logsPath "frontend-tests.log"), "stale-success", [Text.UTF8Encoding]::new($false))
    }
    $arguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $arguments += @("-ExecutionPolicy", "Bypass")
    }
    $arguments += @("-File", $frontendScriptPath, "-RepositoryRoot", $repoRoot, "-LogsPath", $logsPath, "-InstallTimeoutSeconds", [string]$InstallTimeoutSeconds, "-TestTimeoutSeconds", [string]$TestTimeoutSeconds)
    $environment = @{
        PATH = $FakeBinPath + [IO.Path]::PathSeparator + $env:PATH
        EMBODYSENSE_FAKE_NPM_ORDER_PATH = $orderPath
        EMBODYSENSE_FAKE_NPM_PID_PATH = $pidPath
        EMBODYSENSE_FAKE_NPM_INSTALL_EXIT_CODE = [string]$InstallExitCode
        EMBODYSENSE_FAKE_NPM_TEST_EXIT_CODE = [string]$TestExitCode
        EMBODYSENSE_FAKE_NPM_INSTALL_DELAY_MILLISECONDS = [string]$InstallDelayMilliseconds
    }
    $startInfo = New-VerificationProcessStartInfo -FileName $powerShellExecutable -Arguments $arguments -WorkingDirectory $repoRoot -Environment $environment
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) { throw "Frontend fixture '$Name' did not start." }
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            Stop-VerificationProcessTree $process
            $process.WaitForExit()
            throw "Frontend fixture '$Name' exceeded its test bound."
        }
        $stopwatch.Stop()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $standardOutput.GetAwaiter().GetResult() + [Environment]::NewLine + $standardError.GetAwaiter().GetResult()
            ElapsedSeconds = $stopwatch.Elapsed.TotalSeconds
            LogsPath = $logsPath
            OrderPath = $orderPath
            PidPath = $pidPath
        }
    }
    finally {
        if (-not $process.HasExited) { Stop-VerificationProcessTree $process }
        $process.Dispose()
    }
}

$frontendFixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-frontend-preflight-" + [Guid]::NewGuid().ToString("N"))
$fakeBinPath = Join-Path $frontendFixtureRoot "bin"
New-Item -ItemType Directory -Path $fakeBinPath | Out-Null
try {
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $npmShimPath = Join-Path $fakeBinPath "npm.cmd"
        $shim = "@echo off`r`n`"$nodeExecutable`" `"$parallelProbePath`" npm %*`r`nexit /b %ERRORLEVEL%`r`n"
        [IO.File]::WriteAllText($npmShimPath, $shim, [Text.Encoding]::ASCII)
    }
    else {
        $npmShimPath = Join-Path $fakeBinPath "npm"
        $shim = "#!/bin/sh`nexec `"$nodeExecutable`" `"$parallelProbePath`" npm `"`$@`"`n"
        [IO.File]::WriteAllText($npmShimPath, $shim, [Text.UTF8Encoding]::new($false))
        & chmod 700 $npmShimPath
        if ($LASTEXITCODE -ne 0) { throw "Could not make the fake npm shim executable." }
    }

    $success = Invoke-FrontendFixture -Name "success" -FixtureRoot $frontendFixtureRoot -FakeBinPath $fakeBinPath
    Assert-True -Condition ($success.ExitCode -eq 0) -Message "A successful install and frontend test must complete the composed phase. Actual: $($success.Output)"
    Assert-True -Condition ((@(Get-Content -LiteralPath $success.OrderPath) -join ",") -ceq "ci,test") -Message "Frontend tests must execute exactly once and only after npm ci succeeds."
    Assert-Contains -Actual (Get-Content -LiteralPath (Join-Path $success.LogsPath "npm-ci.log") -Raw) -Expected "fake-ci-output" -Message "The install phase must retain its distinct output log."
    Assert-Contains -Actual (Get-Content -LiteralPath (Join-Path $success.LogsPath "frontend-tests.log") -Raw) -Expected "fake-test-output" -Message "The test phase must retain its distinct output log."
    Assert-Contains -Actual $success.Output -Expected "VERIFY_FRONTEND_COMPLETE schema_version=1 status=passed" -Message "The composed frontend phase must emit exact terminal evidence."

    $installFailure = Invoke-FrontendFixture -Name "install-failure" -FixtureRoot $frontendFixtureRoot -FakeBinPath $fakeBinPath -InstallExitCode 11 -SeedStaleFrontendLog
    Assert-True -Condition ($installFailure.ExitCode -ne 0) -Message "A failed npm install must fail the composed phase."
    Assert-True -Condition ((@(Get-Content -LiteralPath $installFailure.OrderPath) -join ",") -ceq "ci") -Message "Frontend tests must not run after a failed npm install."
    $normalizedInstallFailure = Normalize-ConsoleDiagnostic $installFailure.Output
    Assert-Contains -Actual $normalizedInstallFailure -Expected "VERIFY_PARALLEL_PHASE_COMPLETE name=npm-ci status=failed exit_code=11" -Message "Install failure identity, terminal status, and exit code must remain explicit. Actual: $normalizedInstallFailure"
    Assert-True -Condition (-not (Test-Path -LiteralPath (Join-Path $installFailure.LogsPath "frontend-tests.log"))) -Message "A skipped frontend test must not leave stale success evidence."

    $testFailure = Invoke-FrontendFixture -Name "test-failure" -FixtureRoot $frontendFixtureRoot -FakeBinPath $fakeBinPath -TestExitCode 13
    Assert-True -Condition ($testFailure.ExitCode -ne 0) -Message "A failed frontend test must fail the composed phase."
    Assert-True -Condition ((@(Get-Content -LiteralPath $testFailure.OrderPath) -join ",") -ceq "ci,test") -Message "A frontend failure must retain its exact dependency order."
    $normalizedTestFailure = Normalize-ConsoleDiagnostic $testFailure.Output
    Assert-Contains -Actual $normalizedTestFailure -Expected "VERIFY_PARALLEL_PHASE_COMPLETE name=frontend-tests status=failed exit_code=13" -Message "Frontend failure identity, terminal status, and exit code must remain explicit. Actual: $normalizedTestFailure"

    $installTimeout = Invoke-FrontendFixture -Name "install-timeout" -FixtureRoot $frontendFixtureRoot -FakeBinPath $fakeBinPath -InstallDelayMilliseconds 30000 -InstallTimeoutSeconds 5
    Assert-True -Condition ($installTimeout.ExitCode -ne 0 -and $installTimeout.ElapsedSeconds -lt 15) -Message "A stalled lightweight npm fixture must fail inside its private bound without changing the production timeout."
    Assert-Contains -Actual $installTimeout.Output -Expected "VERIFY_CHILD_TIMEOUT name=npm-ci" -Message "Install timeout evidence must retain its exact phase identity."
    Assert-True -Condition ((@(Get-Content -LiteralPath $installTimeout.OrderPath) -join ",") -ceq "ci") -Message "A timed-out install must prevent frontend test admission."
    $timedOutPid = [int](Get-Content -LiteralPath $installTimeout.PidPath -Raw)
    Start-Sleep -Milliseconds 100
    Assert-True -Condition ($null -eq (Get-Process -Id $timedOutPid -ErrorAction SilentlyContinue)) -Message "Timeout must terminate the fake npm process tree."
}
finally {
    Remove-Item -LiteralPath $frontendFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$preflightCompletionIndex = $verifyScript.IndexOf('$script:LastCompletedVerificationPhase = "pull-request-preflight"', [StringComparison]::Ordinal)
$buildInvocationIndex = $verifyScript.IndexOf('Invoke-VerificationParallelPhases -MaximumWorkers $preflightMaximumWorkers -MaximumResourceCapacity $preflightResourceCapacity -MaximumProcessHeavyWorkers $preflightMaximumProcessHeavyWorkers -MaximumCpuBoundWorkers 1', [StringComparison]::Ordinal)
$contractManifestIndex = $verifyScript.IndexOf('$contractScripts = @(', [StringComparison]::Ordinal)
$browserIndex = $verifyScript.IndexOf('if ($RunBrowserE2E) {', [StringComparison]::Ordinal)
$isolationIndex = $verifyScript.IndexOf('Write-Output "VERIFY_REQUIRED_TEST_CONTRACT', [StringComparison]::Ordinal)
$buildIndex = $verifyScript.IndexOf('Add-VerificationParallelPhase -Name "build-pullrequest"', [StringComparison]::Ordinal)
$testPlanIndex = $verifyScript.IndexOf('Add-VerificationParallelPhase -Name "prepare-test-plan"', [StringComparison]::Ordinal)
$frontendIndex = $verifyScript.IndexOf('Add-VerificationParallelPhase -Name "frontend-preflight"', [StringComparison]::Ordinal)
$coverageIndex = $verifyScript.IndexOf('Add-VerificationParallelPhase -Name "contract-verify-coverage.tests"', [StringComparison]::Ordinal)
$nestedAdmissionIndex = $verifyScript.IndexOf('foreach ($contractScript in $contractScripts) {', [StringComparison]::Ordinal)
$planReadIndex = $verifyScript.IndexOf('$isolations = @(Read-VerificationTestPreparationPlan', [StringComparison]::Ordinal)
$partitionIndex = $prepareTestPlanScript.IndexOf('& $powerShellExecutable @partitionArguments', [StringComparison]::Ordinal)
$planWriteIndex = $prepareTestPlanScript.IndexOf('Write-VerificationTestPreparationPlan -PlanPath $planPath', [StringComparison]::Ordinal)
$formatCSharpIndex = $verifyScript.IndexOf('Add-ProfiledRequiredGatePhase -Name "format-csharp"', [StringComparison]::Ordinal)
$requiredGateInvocationIndex = $verifyScript.IndexOf('Assert-VerificationRequiredGateSchedule -Phases @($script:VerificationParallelPhases)', [StringComparison]::Ordinal)
Assert-True -Condition ($contractManifestIndex -ge 0 -and $contractManifestIndex -lt $buildIndex) -Message "Every script contract must be classified before any overlap phase is admitted."
Assert-True -Condition ($buildIndex -lt $buildInvocationIndex -and $testPlanIndex -lt $buildInvocationIndex -and $frontendIndex -lt $buildInvocationIndex -and $coverageIndex -lt $buildInvocationIndex -and $nestedAdmissionIndex -lt $buildInvocationIndex) -Message "Build, build-dependent preparation/coverage, frontend, and every classified contract must enter the same bounded DAG."
Assert-True -Condition ($buildInvocationIndex -lt $preflightCompletionIndex) -Message "The complete preflight DAG must pass before its dependency boundary is recorded."
Assert-True -Condition ($preflightCompletionIndex -ge 0 -and $preflightCompletionIndex -lt $browserIndex) -Message "Browser execution must wait for the complete preflight DAG."
Assert-True -Condition ($preflightCompletionIndex -lt $isolationIndex -and $preflightCompletionIndex -lt $planReadIndex) -Message "Required-gate admission must wait for the complete preflight DAG and validated plan hydration."
Assert-True -Condition ($frontendIndex -ge 0 -and $frontendIndex -lt $preflightCompletionIndex) -Message "Frontend install and tests must complete inside the preflight DAG."
Assert-True -Condition ($partitionIndex -ge 0 -and $planWriteIndex -gt $partitionIndex) -Message "The child phase must publish its plan only after exact partition reconciliation."
Assert-True -Condition ($planReadIndex -ge 0 -and $planReadIndex -lt $formatCSharpIndex) -Message "The combined read-only format gate must be admitted only after the immutable plan is validated."
Assert-True -Condition ($formatCSharpIndex -lt $requiredGateInvocationIndex) -Message "The authoritative combined format gate must execute inside the bounded required-gate schedule."

Write-Output "Verifier preflight overlap and dependency-boundary contract tests passed ($assertionCount assertions)."
