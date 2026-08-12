Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifyScriptPath = Join-Path $repoRoot "scripts\verify.ps1"
$verifyScript = Get-Content -LiteralPath $verifyScriptPath -Raw
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

Assert-Contains -Actual $verifyScript -Expected '$normalPullRequestVerification = $VerificationTier -eq "PullRequest" -and -not $BrowserE2EOnly' -Message "Only the complete pull-request verifier may use the overlapping preflight."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "build-pullrequest" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "build-pullrequest.log") -Priority 3000 -Weight 2 -ResourceClass "ProcessHeavy"' -Message "The canonical build must be a bounded, logged, process-heavy preflight phase."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "npm-ci" -FileName "npm" -Arguments @("ci", "--include=dev") -TimeoutSeconds 300 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "npm-ci.log") -Priority 2900 -Weight 1 -ResourceClass "Ordinary"' -Message "The portable npm restore must be a bounded, logged preflight phase."
Assert-Contains -Actual $verifyScript -Expected 'Add-VerificationParallelPhase -Name "npm-ci" -FileName $env:ComSpec -Arguments @("/d", "/s", "/c", "npm.cmd ci --include=dev") -TimeoutSeconds 300 -WorkingDirectory $repoRoot -OutputPath (Join-Path $verificationLogsPath "npm-ci.log") -Priority 2900 -Weight 1 -ResourceClass "Ordinary"' -Message "The Windows npm restore must preserve cmd.exe quoting inside the preflight."
Assert-Contains -Actual $verifyScript -Expected '-Priority 2800 -Weight 1 -ResourceClass "Ordinary"' -Message "Every independent script contract must declare its bounded preflight resource cost."
Assert-Contains -Actual $verifyScript -Expected 'kind=pull-request-preflight phases=$($script:VerificationParallelPhases.Count) maximum_resource_capacity=$MaximumTestWorkers build_weight=2 npm_weight=1 contract_weight=1 configuration=$Configuration' -Message "The overlapping preflight must publish its exact resource plan."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-VerificationParallelPhases -MaximumWorkers $MaximumTestWorkers | Out-Null' -Message "The preflight must aggregate every child through the fail-closed scheduler."
Assert-Contains -Actual $verifyScript -Expected '$script:LastCompletedVerificationPhase = "pull-request-preflight"' -Message "Later failures must identify the successful preflight dependency boundary."
Assert-Contains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "build-$($VerificationTier.ToLowerInvariant())" -FileName "dotnet" -Arguments $buildArguments -TimeoutSeconds 900' -Message "Stress and browser-only verification must retain the sequential canonical build path."
Assert-NotContains -Actual $verifyScript -Expected 'Invoke-CheckedNativePhase -Name "npm-ci"' -Message "npm restore cannot be repeated after the overlapping preflight."

$preflightCompletionIndex = $verifyScript.IndexOf('$script:LastCompletedVerificationPhase = "pull-request-preflight"', [StringComparison]::Ordinal)
$browserIndex = $verifyScript.IndexOf('if ($RunBrowserE2E) {', [StringComparison]::Ordinal)
$isolationIndex = $verifyScript.IndexOf('Write-Output "VERIFY_REQUIRED_TEST_CONTRACT', [StringComparison]::Ordinal)
$frontendIndex = $verifyScript.IndexOf('Add-VerificationParallelPhase -Name "frontend-tests"', [StringComparison]::Ordinal)
Assert-True -Condition ($preflightCompletionIndex -ge 0 -and $preflightCompletionIndex -lt $browserIndex) -Message "Browser execution must wait for a successful build preflight."
Assert-True -Condition ($preflightCompletionIndex -lt $isolationIndex) -Message "Isolation and discovery must wait for a successful build preflight."
Assert-True -Condition ($preflightCompletionIndex -lt $frontendIndex) -Message "Frontend tests must wait for a successful npm restore preflight."

Write-Output "Verifier preflight overlap contract tests passed ($assertionCount assertions)."
