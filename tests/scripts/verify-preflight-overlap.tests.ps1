Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$scheduleScriptPath = Join-Path $repoRoot "scripts\verification-schedule.ps1"
$frontendScriptPath = Join-Path $repoRoot "scripts\verify-frontend.ps1"
$verifyScript = Get-Content -LiteralPath $verifyScriptPath -Raw
$scheduleScript = Get-Content -LiteralPath $scheduleScriptPath -Raw
$frontendScript = Get-Content -LiteralPath $frontendScriptPath -Raw
$powerShellExecutable = (Get-Process -Id $PID).Path
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

function Normalize-ConsoleDiagnostic {
    param([AllowEmptyString()] [string]$Value)

    $withoutAnsi = [regex]::Replace($Value, "`e\[[0-?]*[ -/]*[@-~]", "")
    return [regex]::Replace($withoutAnsi, "\s+", " ").Trim()
}

. $phaseScriptPath
. $parallelScriptPath
. $scheduleScriptPath

Assert-Contains -Actual $verifyScript -Expected '$normalPullRequestVerification = $VerificationTier -eq "PullRequest" -and -not $BrowserE2EOnly' -Message "Only the complete pull-request verifier may use the overlapping preflight."
Assert-Contains -Actual $verifyScript -Expected '$preflightProcessHeavyWeight = [Math]::Max(1, [int][Math]::Ceiling($hardwareBoundedResourceCapacity / 2.0))' -Message "The preflight build weight must adapt safely to the explicit hardware-bounded capacity."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "build-pullrequest" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "build-pullrequest.log") -EstimatedDurationSeconds 90 -Weight $preflightProcessHeavyWeight -ResourceClass "ProcessHeavy"' -Message "The canonical build must be a bounded, logged, process-heavy preflight phase."
Assert-Contains -Actual $verifyScript -Expected '$preflightCoverageContractWeight = Get-VerificationPreflightCoverageContractWeight -ResourceCapacity $hardwareBoundedResourceCapacity' -Message "The coverage contract must use the behavior-tested adaptive resource-weight derivation."
Assert-Contains -Actual $verifyScript -Expected '$preflightFrontendWeight = Get-VerificationPreflightFrontendWeight -ResourceCapacity $hardwareBoundedResourceCapacity' -Message "The composed frontend phase must use a behavior-tested adaptive physical-capacity weight."
Assert-Contains -Actual $verifyScript -Expected '$preflightMaximumProcessHeavyWorkers = 1' -Message "The build and coverage contract must share one process-heavy preflight slot."
Assert-Contains -Actual $scheduleScript -Expected 'return [Math]::Min(3, $ResourceCapacity)' -Message "The shared preflight coverage derivation must clamp its weight to the available capacity."
Assert-Contains -Actual $scheduleScript -Expected 'return [Math]::Min(2, $ResourceCapacity)' -Message "The shared frontend derivation must use two physical units where available and remain admissible on smaller hosts."
Assert-Contains -Actual $verifyScript -Expected 'if ($contractScript -ceq "verify-coverage.tests.ps1") {' -Message "Only the intrinsically process-heavy coverage contract may receive the expanded preflight profile."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 75 -Weight $preflightCoverageContractWeight -ResourceClass "ProcessHeavy"' -Message "The coverage contract must retain its measured process-heavy profile and bounded headroom."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 90 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"' -Message "Every other independent script contract must retain the original bounded Ordinary profile."
Assert-NotContains -Actual $verifyScript -Expected '-TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"' -Message "Coverage timeout headroom cannot become a blanket extension for Ordinary contracts."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "frontend-preflight" -FileName $powerShellExecutable -Arguments $frontendArguments -TimeoutSeconds 590 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "frontend-preflight.log") -EstimatedDurationSeconds 70 -Weight $preflightFrontendWeight -ResourceClass "CpuBound"' -Message "The install-and-test frontend dependency chain must own one bounded CPU profile during preflight."
Assert-Contains -Actual $verifyScript -Expected 'kind=pull-request-preflight phases=$($script:VerificationParallelPhases.Count) requested_workers=$MaximumTestWorkers maximum_workers=$hardwareBoundedResourceCapacity maximum_resource_capacity=$hardwareBoundedResourceCapacity maximum_process_heavy=$preflightMaximumProcessHeavyWorkers build_weight=$preflightProcessHeavyWeight frontend_weight=$preflightFrontendWeight coverage_contract_weight=$preflightCoverageContractWeight ordinary_contract_weight=1 configuration=$Configuration' -Message "The overlapping preflight must distinguish the logical request from its hardware-bounded worker and resource limits."
Assert-Contains -Actual $verifyScript -Expected 'kind=discovery phases=$($script:VerificationParallelPhases.Count) requested_workers=$MaximumTestWorkers maximum_workers=$hardwareBoundedResourceCapacity maximum_resource_capacity=$hardwareBoundedResourceCapacity' -Message "Canonical discovery must publish the same hardware-bounded worker and resource limits."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-VerificationParallelPhases -MaximumWorkers $hardwareBoundedResourceCapacity -MaximumResourceCapacity $hardwareBoundedResourceCapacity -MaximumProcessHeavyWorkers $preflightMaximumProcessHeavyWorkers | Out-Null' -Message "The preflight must serialize its process-heavy build and coverage phases while aggregating every child through the fail-closed scheduler."
Assert-Contains -Actual $verifyScript -Expected '$script:LastCompletedVerificationPhase = "pull-request-preflight"' -Message "Later failures must identify the successful preflight dependency boundary."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "build-$($VerificationTier.ToLowerInvariant())" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900' -Message "Stress and browser-only verification must retain the sequential canonical build path."
Assert-NotContains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "npm-ci"' -Message "npm restore cannot be repeated after the overlapping preflight."
Assert-NotContains -Actual $verifyScript -Expected 'Add-ProfiledRequiredGatePhase -Name "frontend-tests"' -Message "Frontend tests must not execute a second time in the required-gate plan."
Assert-Contains -Actual $frontendScript -Expected 'Invoke-NpmVerificationPhase -Name "npm-ci" -NpmArguments @("ci", "--include=dev")' -Message "The composed phase must install the exact development dependency set."
Assert-Contains -Actual $frontendScript -Expected 'Invoke-NpmVerificationPhase -Name "frontend-tests" -NpmArguments @("test")' -Message "The composed phase must execute the unchanged frontend test command after install."
Assert-Contains -Actual $frontendScript -Expected '@("/d", "/s", "/c", "npm.cmd $($NpmArguments -join '' '')")' -Message "Windows frontend commands must retain explicit cmd.exe argument handling."
Assert-Contains -Actual $frontendScript -Expected 'Add-VerificationParallelPhase -Name $Name' -Message "Each frontend dependency must retain bounded scheduler ownership and a distinct failure name."
Assert-Contains -Actual $frontendScript -Expected '-OutputPath (Join-Path $logsPathRoot "$Name.log")' -Message "Install and test output must remain in distinct diagnostic logs."

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
        $outputPath = Join-Path $admissionRoot "capacity-$($admissionCase.Capacity).log"
        Add-VerificationParallelPhase -Name "coverage-capacity-$($admissionCase.Capacity)" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-Command", "exit 0") -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $outputPath -EstimatedDurationSeconds 75 -Weight $coverageWeight -ResourceClass "ProcessHeavy"
        $results = @(Invoke-VerificationParallelPhases -MaximumWorkers $admissionCase.Capacity -MaximumResourceCapacity $admissionCase.Capacity)
        Assert-True -Condition ($results.Count -eq 1 -and $results[0].Weight -eq $admissionCase.ExpectedCoverageWeight -and $results[0].ResourceClass -ceq "ProcessHeavy") -Message "Coverage contract capacity $($admissionCase.Capacity) must be admitted as ProcessHeavy with effective weight $($admissionCase.ExpectedCoverageWeight)."
        Assert-True -Condition ($frontendWeight -eq $admissionCase.ExpectedFrontendWeight) -Message "Frontend capacity $($admissionCase.Capacity) must derive effective weight $($admissionCase.ExpectedFrontendWeight)."
    }
}
finally {
    Reset-VerificationParallelPhaseState
    Remove-Item -LiteralPath $admissionRoot -Recurse -Force -ErrorAction SilentlyContinue
}

function Get-PreflightTimingArguments {
    param(
        [string]$ScriptPath,
        [string]$Role,
        [string]$SynchronizationRoot
    )

    $arguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $arguments += @("-ExecutionPolicy", "Bypass")
    }
    $arguments += @("-File", $ScriptPath, "-Role", $Role, "-SynchronizationRoot", $SynchronizationRoot)
    return [string[]]$arguments
}

function Read-PreflightTiming {
    param([string]$Path)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^(start|end)=(\d+)$') {
            $values[$Matches[1]] = [long]$Matches[2]
        }
    }
    if (-not $values.ContainsKey("start") -or -not $values.ContainsKey("end")) {
        throw "Preflight timing evidence is incomplete: $Path"
    }

    return [pscustomobject]@{ Start = $values.start; End = $values.end }
}

$behaviorRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-preflight-backfill-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $behaviorRoot | Out-Null
try {
    $timingScriptPath = Join-Path $behaviorRoot "timed-phase.ps1"
    $synchronizationRoot = Join-Path $behaviorRoot "synchronization"
    New-Item -ItemType Directory -Path $synchronizationRoot | Out-Null
    [IO.File]::WriteAllText($timingScriptPath, @'
param([string]$Role, [string]$SynchronizationRoot)
Write-Output "start=$([DateTime]::UtcNow.Ticks)"
if ($Role -ceq "build") {
    $releasePath = Join-Path $SynchronizationRoot "release-build"
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $releasePath)) {
        if ($stopwatch.Elapsed -ge [TimeSpan]::FromSeconds(5)) { exit 41 }
        Start-Sleep -Milliseconds 10
    }
}
elseif ($Role -ceq "frontend") {
    Start-Sleep -Milliseconds 100
}
elseif ($Role -ceq "ordinary") {
    New-Item -ItemType File -Path (Join-Path $SynchronizationRoot "release-build") | Out-Null
}
Write-Output "end=$([DateTime]::UtcNow.Ticks)"
'@, [Text.UTF8Encoding]::new($false))

    Reset-VerificationParallelPhaseState
    $buildOutputPath = Join-Path $behaviorRoot "build.log"
    $coverageOutputPath = Join-Path $behaviorRoot "coverage.log"
    $frontendOutputPath = Join-Path $behaviorRoot "frontend.log"
    $ordinaryOutputPath = Join-Path $behaviorRoot "ordinary.log"
    Add-VerificationParallelPhase -Name "build" -FileName $powerShellExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "build" -SynchronizationRoot $synchronizationRoot) -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $buildOutputPath -EstimatedDurationSeconds 90 -Weight 2 -ResourceClass "ProcessHeavy"
    Add-VerificationParallelPhase -Name "coverage" -FileName $powerShellExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "coverage" -SynchronizationRoot $synchronizationRoot) -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $coverageOutputPath -EstimatedDurationSeconds 75 -Weight 3 -ResourceClass "ProcessHeavy"
    Add-VerificationParallelPhase -Name "frontend" -FileName $powerShellExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "frontend" -SynchronizationRoot $synchronizationRoot) -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $frontendOutputPath -EstimatedDurationSeconds 70 -Weight 2 -ResourceClass "CpuBound"
    Add-VerificationParallelPhase -Name "ordinary" -FileName $powerShellExecutable -Arguments (Get-PreflightTimingArguments -ScriptPath $timingScriptPath -Role "ordinary" -SynchronizationRoot $synchronizationRoot) -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $ordinaryOutputPath -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"

    $behaviorResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 4 -MaximumResourceCapacity 4 -MaximumProcessHeavyWorkers 1 -MaximumCpuBoundWorkers 1)
    Assert-True -Condition ($behaviorResults.Count -eq 4 -and @($behaviorResults | Where-Object { $_.ExitCode -ne 0 }).Count -eq 0) -Message "The representative preflight schedule must complete every phase successfully."

    $buildTiming = Read-PreflightTiming -Path $buildOutputPath
    $coverageTiming = Read-PreflightTiming -Path $coverageOutputPath
    $frontendTiming = Read-PreflightTiming -Path $frontendOutputPath
    $ordinaryTiming = Read-PreflightTiming -Path $ordinaryOutputPath
    Assert-True -Condition ($frontendTiming.Start -lt $buildTiming.End) -Message "The composed frontend phase must share bounded physical capacity with the build."
    Assert-True -Condition ($coverageTiming.Start -ge $buildTiming.End) -Message "The coverage contract must not overlap the process-heavy build."
    Assert-True -Condition ($ordinaryTiming.Start -lt $buildTiming.End) -Message "Ordinary preflight work must backfill capacity after the composed frontend phase."
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
        if (-not $process.WaitForExit(15000)) {
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
    $fakeNpmScriptPath = Join-Path $frontendFixtureRoot "fake-npm.ps1"
    [IO.File]::WriteAllText($fakeNpmScriptPath, @'
param(
    [Parameter(Position = 0)] [string]$Operation,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]]$RemainingArguments
)
Add-Content -LiteralPath $env:EMBODYSENSE_FAKE_NPM_ORDER_PATH -Value $Operation
[IO.File]::WriteAllText($env:EMBODYSENSE_FAKE_NPM_PID_PATH, [string]$PID, [Text.UTF8Encoding]::new($false))
Write-Output "fake-$Operation-output"
if ($Operation -ceq "ci") {
    $delay = [int]$env:EMBODYSENSE_FAKE_NPM_INSTALL_DELAY_MILLISECONDS
    if ($delay -gt 0) { Start-Sleep -Milliseconds $delay }
    exit ([int]$env:EMBODYSENSE_FAKE_NPM_INSTALL_EXIT_CODE)
}
if ($Operation -ceq "test") { exit ([int]$env:EMBODYSENSE_FAKE_NPM_TEST_EXIT_CODE) }
exit 97
'@, [Text.UTF8Encoding]::new($false))

    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $npmShimPath = Join-Path $fakeBinPath "npm.cmd"
        $shim = "@echo off`r`n`"$powerShellExecutable`" -NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$fakeNpmScriptPath`" %*`r`nexit /b %ERRORLEVEL%`r`n"
        [IO.File]::WriteAllText($npmShimPath, $shim, [Text.Encoding]::ASCII)
    }
    else {
        $npmShimPath = Join-Path $fakeBinPath "npm"
        $shim = "#!/bin/sh`nexec `"$powerShellExecutable`" -NoLogo -NoProfile -File `"$fakeNpmScriptPath`" `"`$@`"`n"
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
    Assert-Contains -Actual $normalizedInstallFailure -Expected "'npm-ci' exited with code 11" -Message "Install failure identity and exit code must remain explicit. Actual: $normalizedInstallFailure"
    Assert-True -Condition (-not (Test-Path -LiteralPath (Join-Path $installFailure.LogsPath "frontend-tests.log"))) -Message "A skipped frontend test must not leave stale success evidence."

    $testFailure = Invoke-FrontendFixture -Name "test-failure" -FixtureRoot $frontendFixtureRoot -FakeBinPath $fakeBinPath -TestExitCode 13
    Assert-True -Condition ($testFailure.ExitCode -ne 0) -Message "A failed frontend test must fail the composed phase."
    Assert-True -Condition ((@(Get-Content -LiteralPath $testFailure.OrderPath) -join ",") -ceq "ci,test") -Message "A frontend failure must retain its exact dependency order."
    $normalizedTestFailure = Normalize-ConsoleDiagnostic $testFailure.Output
    Assert-Contains -Actual $normalizedTestFailure -Expected "'frontend-tests' exited with code 13" -Message "Frontend failure identity and exit code must remain explicit. Actual: $normalizedTestFailure"

    $installTimeout = Invoke-FrontendFixture -Name "install-timeout" -FixtureRoot $frontendFixtureRoot -FakeBinPath $fakeBinPath -InstallDelayMilliseconds 2500 -InstallTimeoutSeconds 1
    Assert-True -Condition ($installTimeout.ExitCode -ne 0 -and $installTimeout.ElapsedSeconds -lt 5) -Message "A stalled npm install must fail inside its bounded timeout."
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
$browserIndex = $verifyScript.IndexOf('if ($RunBrowserE2E) {', [StringComparison]::Ordinal)
$isolationIndex = $verifyScript.IndexOf('Write-Output "VERIFY_REQUIRED_TEST_CONTRACT', [StringComparison]::Ordinal)
$frontendIndex = $verifyScript.IndexOf('Add-VerificationParallelPhase -Name "frontend-preflight"', [StringComparison]::Ordinal)
Assert-True -Condition ($preflightCompletionIndex -ge 0 -and $preflightCompletionIndex -lt $browserIndex) -Message "Browser execution must wait for a successful build preflight."
Assert-True -Condition ($preflightCompletionIndex -lt $isolationIndex) -Message "Isolation and discovery must wait for a successful build preflight."
Assert-True -Condition ($frontendIndex -ge 0 -and $frontendIndex -lt $preflightCompletionIndex) -Message "Frontend install and tests must complete inside the pull-request preflight."

Write-Output "Verifier preflight overlap contract tests passed ($assertionCount assertions)."
