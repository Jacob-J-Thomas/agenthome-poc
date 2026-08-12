Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$scheduleScriptPath = Join-Path $repoRoot "scripts\verification-schedule.ps1"
$verifyScript = Get-Content -LiteralPath $verifyScriptPath -Raw
$scheduleScript = Get-Content -LiteralPath $scheduleScriptPath -Raw
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

. $phaseScriptPath
. $parallelScriptPath
. $scheduleScriptPath

Assert-Contains -Actual $verifyScript -Expected '$normalPullRequestVerification = $VerificationTier -eq "PullRequest" -and -not $BrowserE2EOnly' -Message "Only the complete pull-request verifier may use the overlapping preflight."
Assert-Contains -Actual $verifyScript -Expected '$preflightProcessHeavyWeight = [Math]::Max(1, [int][Math]::Ceiling($hardwareBoundedResourceCapacity / 2.0))' -Message "The preflight build weight must adapt safely to the explicit hardware-bounded capacity."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "build-pullrequest" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "build-pullrequest.log") -EstimatedDurationSeconds 90 -Weight $preflightProcessHeavyWeight -ResourceClass "ProcessHeavy"' -Message "The canonical build must be a bounded, logged, process-heavy preflight phase."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "npm-ci" -FileName "npm" -Arguments @("ci", "--include=dev") -TimeoutSeconds 300 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "npm-ci.log") -EstimatedDurationSeconds 30 -Weight 1 -ResourceClass "Ordinary"' -Message "The portable npm restore must be a bounded, logged preflight phase."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "npm-ci" -FileName $env:ComSpec -Arguments @("/d", "/s", "/c", "npm.cmd ci --include=dev") -TimeoutSeconds 300 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "npm-ci.log") -EstimatedDurationSeconds 30 -Weight 1 -ResourceClass "Ordinary"' -Message "The Windows npm restore must preserve cmd.exe quoting inside the preflight."
Assert-Contains -Actual $verifyScript -Expected '$preflightCoverageContractWeight = Get-VerificationPreflightCoverageContractWeight -ResourceCapacity $hardwareBoundedResourceCapacity' -Message "The coverage contract must use the behavior-tested adaptive resource-weight derivation."
Assert-Contains -Actual $scheduleScript -Expected 'return [Math]::Min(3, $ResourceCapacity)' -Message "The shared preflight coverage derivation must clamp its weight to the available capacity."
Assert-Contains -Actual $verifyScript -Expected 'if ($contractScript -ceq "verify-coverage.tests.ps1") {' -Message "Only the intrinsically process-heavy coverage contract may receive the expanded preflight profile."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 75 -Weight $preflightCoverageContractWeight -ResourceClass "ProcessHeavy"' -Message "The coverage contract must retain its measured process-heavy profile and bounded headroom."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 90 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"' -Message "Every other independent script contract must retain the original bounded Ordinary profile."
Assert-NotContains -Actual $verifyScript -Expected '-TimeoutSeconds 120 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "$contractScript.log") -EstimatedDurationSeconds 35 -Weight 1 -ResourceClass "Ordinary"' -Message "Coverage timeout headroom cannot become a blanket extension for Ordinary contracts."
Assert-Contains -Actual $verifyScript -Expected 'kind=pull-request-preflight phases=$($script:VerificationParallelPhases.Count) maximum_workers=$MaximumTestWorkers maximum_resource_capacity=$hardwareBoundedResourceCapacity build_weight=$preflightProcessHeavyWeight npm_weight=1 coverage_contract_weight=$preflightCoverageContractWeight ordinary_contract_weight=1 configuration=$Configuration' -Message "The overlapping preflight must publish its effective adaptive coverage weight and distinct Ordinary profile."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-VerificationParallelPhases -MaximumWorkers $MaximumTestWorkers -MaximumResourceCapacity $hardwareBoundedResourceCapacity | Out-Null' -Message "The preflight must aggregate every child through the fail-closed scheduler."
Assert-Contains -Actual $verifyScript -Expected '$script:LastCompletedVerificationPhase = "pull-request-preflight"' -Message "Later failures must identify the successful preflight dependency boundary."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "build-$($VerificationTier.ToLowerInvariant())" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900' -Message "Stress and browser-only verification must retain the sequential canonical build path."
Assert-NotContains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "npm-ci"' -Message "npm restore cannot be repeated after the overlapping preflight."

$admissionRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-preflight-coverage-admission-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $admissionRoot | Out-Null
try {
    foreach ($admissionCase in @(
        [pscustomobject]@{ Capacity = 1; ExpectedWeight = 1 }
        [pscustomobject]@{ Capacity = 2; ExpectedWeight = 2 }
        [pscustomobject]@{ Capacity = 4; ExpectedWeight = 3 }
        [pscustomobject]@{ Capacity = 8; ExpectedWeight = 3 }
    )) {
        Reset-VerificationParallelPhaseState
        $coverageWeight = Get-VerificationPreflightCoverageContractWeight -ResourceCapacity $admissionCase.Capacity
        $outputPath = Join-Path $admissionRoot "capacity-$($admissionCase.Capacity).log"
        Add-VerificationParallelPhase -Name "coverage-capacity-$($admissionCase.Capacity)" -FileName $powerShellExecutable -Arguments @("-NoProfile", "-Command", "exit 0") -TimeoutSeconds 10 -WorkingDirectory $repoRoot -OutputPath $outputPath -EstimatedDurationSeconds 75 -Weight $coverageWeight -ResourceClass "ProcessHeavy"
        $results = @(Invoke-VerificationParallelPhases -MaximumWorkers $admissionCase.Capacity -MaximumResourceCapacity $admissionCase.Capacity)
        Assert-True -Condition ($results.Count -eq 1 -and $results[0].Weight -eq $admissionCase.ExpectedWeight -and $results[0].ResourceClass -ceq "ProcessHeavy") -Message "Coverage contract capacity $($admissionCase.Capacity) must be admitted as ProcessHeavy with effective weight $($admissionCase.ExpectedWeight)."
    }
}
finally {
    Reset-VerificationParallelPhaseState
    Remove-Item -LiteralPath $admissionRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$preflightCompletionIndex = $verifyScript.IndexOf('$script:LastCompletedVerificationPhase = "pull-request-preflight"', [StringComparison]::Ordinal)
$browserIndex = $verifyScript.IndexOf('if ($RunBrowserE2E) {', [StringComparison]::Ordinal)
$isolationIndex = $verifyScript.IndexOf('Write-Output "VERIFY_REQUIRED_TEST_CONTRACT', [StringComparison]::Ordinal)
$frontendIndex = $verifyScript.IndexOf('Add-ProfiledRequiredGatePhase -Name "frontend-tests"', [StringComparison]::Ordinal)
Assert-True -Condition ($preflightCompletionIndex -ge 0 -and $preflightCompletionIndex -lt $browserIndex) -Message "Browser execution must wait for a successful build preflight."
Assert-True -Condition ($preflightCompletionIndex -lt $isolationIndex) -Message "Isolation and discovery must wait for a successful build preflight."
Assert-True -Condition ($preflightCompletionIndex -lt $frontendIndex) -Message "Frontend tests must wait for a successful npm restore preflight."

Write-Output "Verifier preflight overlap contract tests passed ($assertionCount assertions)."
