Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$artifactScriptPath = Join-Path $repoRoot "scripts\verification-artifacts.ps1"
$powerShellExecutable = (Get-Process -Id $PID).Path
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }

    $script:assertionCount++
}

function Assert-Contains {
    param([string]$Actual, [string]$Expected, [string]$Message)

    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'. Actual: $Actual"
}

. $phaseScriptPath
. $parallelScriptPath
. $artifactScriptPath

$scenarioRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-parallel-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
try {
    $probePath = Join-Path $scenarioRoot "probe.ps1"
    @'
param([string]$Name, [int]$DelayMilliseconds, [int]$ExitCode, [string]$OrderPath, [string]$SynchronizationRoot, [int]$ExpectedConcurrent)
if (-not [string]::IsNullOrWhiteSpace($OrderPath) -and $OrderPath -cne "-") { Add-Content -LiteralPath $OrderPath -Value $Name }
if (-not [string]::IsNullOrWhiteSpace($SynchronizationRoot) -and $SynchronizationRoot -cne "-" -and $ExpectedConcurrent -gt 0) {
    New-Item -ItemType Directory -Path $SynchronizationRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $SynchronizationRoot "$Name.ready") -Value "ready" -Encoding UTF8
    $synchronizationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while (@(Get-ChildItem -LiteralPath $SynchronizationRoot -Filter "*.ready" -File).Count -lt $ExpectedConcurrent) {
        if ([DateTimeOffset]::UtcNow -ge $synchronizationDeadline) { exit 41 }
        Start-Sleep -Milliseconds 10
    }
}
Start-Sleep -Milliseconds $DelayMilliseconds
Write-Output "probe=$Name"
Write-Output "environment=$env:VERIFY_PARALLEL_PROBE"
Write-Output "physical_temp=$([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))"
exit $ExitCode
'@ | Set-Content -LiteralPath $probePath -Encoding UTF8

    $baseArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $baseArguments += @("-ExecutionPolicy", "Bypass")
    }
    $baseArguments += @("-File", $probePath)
    $synchronizationRoot = Join-Path $scenarioRoot "overlap"
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "first" -FileName $powerShellExecutable -Arguments ($baseArguments + @("first", "50", "0", "-", $synchronizationRoot, "4")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "first.log")
    Add-VerificationParallelPhase -Name "second" -FileName $powerShellExecutable -Arguments ($baseArguments + @("second", "50", "0", "-", $synchronizationRoot, "4")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "second.log")
    Add-VerificationParallelPhase -Name "third" -FileName $powerShellExecutable -Arguments ($baseArguments + @("third", "50", "0", "-", $synchronizationRoot, "4")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "third.log")
    Add-VerificationParallelPhase -Name "fourth" -FileName $powerShellExecutable -Arguments ($baseArguments + @("fourth", "50", "0", "-", $synchronizationRoot, "4")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "fourth.log")
    $results = @(Invoke-VerificationParallelPhases -MaximumWorkers 4)

    Assert-True -Condition ($results.Count -eq 4) -Message "Every successful parallel phase must be aggregated."
    Assert-True -Condition (@(Get-ChildItem -LiteralPath $synchronizationRoot -Filter "*.ready" -File).Count -eq 4) -Message "Four ordinary weight-one phases must pack the entire four-unit resource capacity."
    Assert-Contains -Actual (Get-Content -Raw (Join-Path $scenarioRoot "first.log")) -Expected "probe=first" -Message "Each phase must retain isolated output."

    $weightedProbePath = Join-Path $scenarioRoot "weighted-probe.ps1"
    @'
param([string]$Name, [string]$ActiveRoot, [int]$MaximumExpectedConcurrent)
$activePath = Join-Path $ActiveRoot "$Name.active"
try {
    Set-Content -LiteralPath $activePath -Value "active" -Encoding UTF8
    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds(350)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $activeCount = @(Get-ChildItem -LiteralPath $ActiveRoot -Filter "*.active" -File).Count
        if ($activeCount -gt $MaximumExpectedConcurrent) {
            Write-Output "overcommitted=$activeCount"
            exit 43
        }
        Start-Sleep -Milliseconds 10
    }
    Write-Output "weighted_probe=$Name"
}
finally {
    Remove-Item -LiteralPath $activePath -Force -ErrorAction SilentlyContinue
}
'@ | Set-Content -LiteralPath $weightedProbePath -Encoding UTF8
    $weightedArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $weightedArguments += @("-ExecutionPolicy", "Bypass")
    }
    $weightedArguments += @("-File", $weightedProbePath)
    $activeRoot = Join-Path $scenarioRoot "weighted-active"
    New-Item -ItemType Directory -Path $activeRoot | Out-Null
    Reset-VerificationParallelPhaseState
    foreach ($name in @("heavy-first", "heavy-second", "heavy-third")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $activeRoot, "2")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log") -Weight 2 -ResourceClass ProcessHeavy
    }
    $weightedResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 4)
    Assert-True -Condition ($weightedResults.Count -eq 3) -Message "Every weighted phase must be scheduled and aggregated."
    Assert-True -Condition (@($weightedResults | Where-Object { $_.Weight -eq 2 -and $_.ResourceClass -ceq "ProcessHeavy" }).Count -eq 3) -Message "Weighted result evidence must preserve each phase's declared resource posture."

    $fairPending = [Collections.Generic.List[object]]::new()
    $fairPending.Add([pscustomobject]@{ Name = "heavy"; Weight = 2; SchedulingDeferrals = 0 })
    $fairPending.Add([pscustomobject]@{ Name = "ordinary-one"; Weight = 1; SchedulingDeferrals = 0 })
    $fairPending.Add([pscustomobject]@{ Name = "ordinary-two"; Weight = 1; SchedulingDeferrals = 0 })
    $backfill = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($backfill.Name -ceq "ordinary-one") -Message "A fitting ordinary phase must backfill capacity behind a temporarily blocked heavy phase."
    $reserved = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($null -eq $reserved) -Message "A bypassed heavy phase must reserve the next fitting opportunity instead of starving behind ordinary work."
    $heavy = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 2
    Assert-True -Condition ($heavy.Name -ceq "heavy") -Message "A previously bypassed heavy phase must run as soon as its required capacity is available."
    $lastOrdinary = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($lastOrdinary.Name -ceq "ordinary-two" -and $fairPending.Count -eq 0) -Message "Fair reservation cannot lose or strand the remaining ordinary phase."

    $unschedulableOutput = Join-Path $scenarioRoot "unschedulable.log"
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "unschedulable" -FileName $powerShellExecutable -Arguments ($baseArguments + @("unschedulable", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $unschedulableOutput -Weight 5 -ResourceClass ProcessHeavy
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers 4 | Out-Null
        throw "Expected unschedulable capacity failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "cannot schedule phases beyond the hardware-bounded resource capacity" -Message "A phase that exceeds the hard resource capacity must fail closed before execution."
    }
    Assert-True -Condition (-not (Test-Path -LiteralPath $unschedulableOutput)) -Message "An unschedulable phase must never start or create output."

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "failure" -FileName $powerShellExecutable -Arguments ($baseArguments + @("failure", "50", "17")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "failure.log") -Weight 2 -ResourceClass ProcessHeavy
    Add-VerificationParallelPhase -Name "success" -FileName $powerShellExecutable -Arguments ($baseArguments + @("success", "300", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "success.log") -Weight 2 -ResourceClass ProcessHeavy
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers 4 | Out-Null
        throw "Expected aggregate failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "'failure' exited with code 17" -Message "Nonzero child exits must fail the aggregate with the exact phase."
    }

    Assert-Contains -Actual (Get-Content -Raw (Join-Path $scenarioRoot "success.log")) -Expected "probe=success" -Message "The harness must drain already-running peers before reporting failure."

    $laneTempRoot = Join-Path $scenarioRoot "lane-temp"
    New-Item -ItemType Directory -Path $laneTempRoot | Out-Null
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "environment" -FileName $powerShellExecutable -Arguments ($baseArguments + @("environment", "10", "0", "-")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "environment.log") -Environment @{ VERIFY_PARALLEL_PROBE = "scoped-child"; TEMP = $laneTempRoot; TMP = $laneTempRoot; TMPDIR = $laneTempRoot }
    Invoke-VerificationParallelPhases -MaximumWorkers 1 | Out-Null
    $environmentLog = Get-Content -Raw (Join-Path $scenarioRoot "environment.log")
    Assert-Contains -Actual $environmentLog -Expected "environment=scoped-child" -Message "Per-phase environment overrides must reach only the child ProcessStartInfo."
    Assert-Contains -Actual $environmentLog -Expected "physical_temp=$([IO.Path]::GetFullPath($laneTempRoot))" -Message "A lane's .NET process and descendants must resolve the isolated fixture root as physical temporary storage."
    Assert-True -Condition ([string]::IsNullOrEmpty($env:VERIFY_PARALLEL_PROBE)) -Message "Per-phase environment overrides cannot mutate the verifier process environment."

    $orderPath = Join-Path $scenarioRoot "priority-order.txt"
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "low" -FileName $powerShellExecutable -Arguments ($baseArguments + @("low", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "low.log") -Priority 1
    Add-VerificationParallelPhase -Name "high" -FileName $powerShellExecutable -Arguments ($baseArguments + @("high", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "high.log") -Priority 100
    Invoke-VerificationParallelPhases -MaximumWorkers 1 | Out-Null
    $order = @(Get-Content -LiteralPath $orderPath)
    Assert-True -Condition ($order.Count -eq 2 -and $order[0] -ceq "high" -and $order[1] -ceq "low") -Message "Higher-priority phases must start first with deterministic single-worker ordering."

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "timeout" -FileName $powerShellExecutable -Arguments ($baseArguments + @("timeout", "5000", "0")) -TimeoutSeconds 1 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "timeout.log")
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers 1 | Out-Null
        throw "Expected aggregate timeout."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "'timeout' timed out" -Message "Timeouts must kill the child tree and fail the aggregate."
    }

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "duplicate" -FileName $powerShellExecutable -Arguments ($baseArguments + @("one", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "one.log")
    try {
        Add-VerificationParallelPhase -Name "duplicate" -FileName $powerShellExecutable -Arguments ($baseArguments + @("two", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "two.log")
        throw "Expected duplicate declaration failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "declared more than once" -Message "Duplicate phase identities must fail before execution."
    }

    $artifactSource = Join-Path $scenarioRoot "artifact-source"
    $artifactCopy = Join-Path $scenarioRoot "artifact-copy"
    New-Item -ItemType Directory -Path (Join-Path $artifactSource "nested") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $artifactSource "nested\assembly.dll") -Value "immutable" -Encoding UTF8
    $artifactManifest = @(Copy-VerifiedDirectory -SourceDirectory $artifactSource -DestinationDirectory $artifactCopy -Description "contract artifact")
    Assert-VerificationDirectoryManifest -Expected $artifactManifest -Directory $artifactCopy -Description "unchanged contract artifact"
    Set-Content -LiteralPath (Join-Path $artifactCopy "nested\assembly.dll") -Value "substituted" -Encoding UTF8
    try {
        Assert-VerificationDirectoryManifest -Expected $artifactManifest -Directory $artifactCopy -Description "mutated contract artifact"
        throw "Expected immutable artifact verification to fail."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "failed immutable artifact verification" -Message "Artifact mutation or substitution must fail closed."
    }

    try {
        Copy-VerifiedDirectory -SourceDirectory $artifactSource -DestinationDirectory $artifactCopy -Description "stale destination" | Out-Null
        throw "Expected stale artifact destination rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "could substitute stale artifacts" -Message "Pre-existing lane output cannot be reused as a stale substitute."
    }

    $isolatedOutput = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $scenarioRoot "project\lane") -Configuration "Release" -TargetFramework "net10.0"
    $isolatedSegments = $isolatedOutput.Replace('\', '/').Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    $expectedSuffix = @("project", "lane", "bin", "Release", "net10.0")
    Assert-True -Condition (@(Compare-Object -ReferenceObject $expectedSuffix -DifferenceObject $isolatedSegments[($isolatedSegments.Count - $expectedSuffix.Count)..($isolatedSegments.Count - 1)] -CaseSensitive).Count -eq 0) -Message "Isolated lanes must preserve the bin/<Configuration>/<TargetFramework> AppContext suffix used by helper-host tests."

    try {
        Get-VerificationIsolatedOutputPath -IsolationRoot $scenarioRoot -Configuration "Release/escape" -TargetFramework "net10.0" | Out-Null
        throw "Expected unsafe topology segment rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "not a single safe path segment" -Message "Isolated topology segments must fail closed instead of escaping their lane."
    }
}
finally {
    if (Test-Path -LiteralPath $scenarioRoot) {
        Remove-Item -LiteralPath $scenarioRoot -Recurse -Force
    }
}

Write-Output "Parallel verifier contract tests passed ($assertionCount assertions)."
