Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fanInScriptPath = Join-Path $repoRoot "scripts\verify-promotion-fan-in.ps1"
. $fanInScriptPath -NoRun
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:assertionCount++
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Message)
    try {
        & $Action
        throw "Expected failure: $Message"
    }
    catch {
        if ($_.Exception.Message -like "Expected failure: $Message") { throw }
        $script:assertionCount++
    }
}

function Write-TestJson {
    param([string]$Path, [object]$Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

function New-TestComponent {
    param([string]$Root, [string]$Component)

    $resultsRoot = Join-Path $Root "VerificationResults"
    $logsRoot = Join-Path $resultsRoot "Logs"
    New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
    $phaseNames = if ($Component -ceq "static-contracts") { @("contract-verify-sdk-diagnostics.tests", "contract-verify-preflight-overlap.tests", "contract-verify-coverage.tests", "contract-verify-bounded-phases.tests", "contract-verify-parallel.tests", "contract-verify-test-inventory.tests", "contract-verify-watchdog.tests", "contract-verify-promotion-fan-in.tests", "frontend-preflight", "restore-static", "format-whitespace", "format-naming-style", "git-diff-check") } else { @() }
    $marker = "$(($phaseNames | ForEach-Object { "VERIFY_PHASE_COMPLETE name=$_ elapsed_seconds=1 completed_at_utc=2026-01-01T00:00:00.0000000+00:00`n" }) -join '')VERIFY_COMPLETE schema_version=1 component=$Component status=passed elapsed_seconds=1`n"
    [IO.File]::WriteAllText((Join-Path $resultsRoot "watchdog.log"), $marker, [Text.UTF8Encoding]::new($false))
    if ($Component -ceq "solution") {
        $lanes = @(1..9 | ForEach-Object { [ordered]@{ name = "lane-$_"; projectName = "Project$_"; filter = "(VerificationTier!=Stress)" } })
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-lanes.json") -Value ([ordered]@{ schemaVersion = 1; lanes = $lanes })
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-partition.json") -Value ([ordered]@{ schemaVersion = 1; canonicalInventoryCount = 9; laneDefinitionCount = 9; canonicalTestCount = 1; laneTestCount = 1; emptyLanes = @(); missing = @(); unexpected = @(); overlap = @(); duplicateCanonical = @(); duplicateExecutionIds = @() })
        Write-TestJson -Path (Join-Path $resultsRoot "required-execution-tests.json") -Value ([ordered]@{ schemaVersion = 1; totalTests = 1; tests = @([ordered]@{ id = "00000000-0000-0000-0000-000000000001" }) })
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-report.json") -Value ([ordered]@{ schemaVersion = 1; expectedCount = 1; executedCount = 1; uniqueExecutedCount = 1; missing = @(); unexpected = @(); crossReportOverlap = @(); duplicateExecutionId = @(); nonPassing = @() })
        Write-TestJson -Path (Join-Path $resultsRoot "coverage-manifest.json") -Value ([ordered]@{ schemaVersion = 1; laneReportCount = 9; childReportCount = 0; aliasReportCount = 0; reports = @(1..9 | ForEach-Object { [ordered]@{ id = $_ } }); aliases = @() })
        Write-TestJson -Path (Join-Path $resultsRoot "coverage-summary.json") -Value ([ordered]@{ schemaVersion = 1; threshold = 0.9; reports = @([ordered]@{ path = "coverage" }); packages = @([ordered]@{ package = "Core" }); failures = @() })
        foreach ($index in 1..9) {
            [IO.File]::WriteAllText((Join-Path $resultsRoot "lane-$index.trx"), "trx", [Text.UTF8Encoding]::new($false))
        }
    }
    else {
        foreach ($name in @("verify-sdk-diagnostics.tests.ps1", "verify-preflight-overlap.tests.ps1", "verify-coverage.tests.ps1", "verify-bounded-phases.tests.ps1", "verify-parallel.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1", "verify-promotion-fan-in.tests.ps1")) {
            [IO.File]::WriteAllText((Join-Path $logsRoot "$name.log"), "passed", [Text.UTF8Encoding]::new($false))
        }
        foreach ($name in @("frontend-preflight.log", "restore-static.log", "format-whitespace.log", "format-naming-style.log", "git-diff-check.log")) {
            $content = if ($name -in @("format-whitespace.log", "format-naming-style.log", "git-diff-check.log")) { "" } else { "passed" }
            [IO.File]::WriteAllText((Join-Path $logsRoot $name), $content, [Text.UTF8Encoding]::new($false))
        }
    }

    $evidence = [ordered]@{ schemaVersion = 1; component = $Component; repositoryHead = "head"; githubRunId = "run"; githubRunAttempt = "attempt"; laneCount = if ($Component -ceq "solution") { 9 } else { 0 }; inventoryComplete = ($Component -ceq "solution"); coverageComplete = ($Component -ceq "solution"); staticContractCount = if ($Component -ceq "static-contracts") { 8 } else { 0 }; frontendComplete = ($Component -ceq "static-contracts"); formatComplete = ($Component -ceq "static-contracts"); diffComplete = ($Component -ceq "static-contracts"); manifestSha256 = "" }
    $evidencePath = Join-Path $resultsRoot "verification-component-evidence.json"
    Write-TestJson -Path $evidencePath -Value $evidence
    $manifestPath = Join-Path $resultsRoot "verification-component-manifest.json"
    $watchdogEvidencePath = Join-Path $resultsRoot "verification-watchdog-evidence.json"
    $manifest = [ordered]@{ schemaVersion = 1; files = @(Get-ChildItem -LiteralPath $resultsRoot -Recurse -File | Where-Object { $_.FullName -ne $evidencePath -and $_.FullName -ne $manifestPath -and $_.FullName -ne $watchdogEvidencePath -and $_.Name -ne "watchdog.log" } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($resultsRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/"); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }) }
    Write-TestJson -Path $manifestPath -Value $manifest
    $evidence.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TestJson -Path $evidencePath -Value $evidence
    $watchdogEvidence = [ordered]@{ schemaVersion = 1; component = $Component; mode = "promotion"; repositoryHead = "head"; githubRunId = "run"; githubRunAttempt = "attempt"; deadlineSeconds = if ($Component -ceq "solution") { 900 } else { 600 }; elapsedSeconds = 1; exitCode = 0; completionMarkerCount = 1; status = "passed"; watchdogLogSha256 = (Get-FileHash -LiteralPath (Join-Path $resultsRoot "watchdog.log") -Algorithm SHA256).Hash.ToLowerInvariant(); componentEvidenceSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant(); componentManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    Write-TestJson -Path $watchdogEvidencePath -Value $watchdogEvidence
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-promotion-fan-in-" + [Guid]::NewGuid().ToString("N"))
$solutionRoot = Join-Path $fixtureRoot "solution"
$staticRoot = Join-Path $fixtureRoot "static"
New-Item -ItemType Directory -Path $solutionRoot, $staticRoot -Force | Out-Null
try {
    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $staticRoot -Component "static-contracts"
    Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success"
    $assertionCount++

    $watchdogEvidencePath = Join-Path $solutionRoot "VerificationResults\verification-watchdog-evidence.json"
    $watchdogEvidence = Get-Content -LiteralPath $watchdogEvidencePath -Raw | ConvertFrom-Json
    $watchdogEvidence.status = "tampered"
    Write-TestJson -Path $watchdogEvidencePath -Value $watchdogEvidence
    Assert-Throws -Message "watchdog evidence tamper" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    $extraReceiptPath = Join-Path $solutionRoot "VerificationResults\unexpected-receipt.txt"
    [IO.File]::WriteAllText($extraReceiptPath, "unexpected", [Text.UTF8Encoding]::new($false))
    Assert-Throws -Message "closed-world extra file" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    Remove-Item -LiteralPath $extraReceiptPath

    $duplicateEvidenceDirectory = Join-Path $solutionRoot "duplicate-evidence"
    New-Item -ItemType Directory -Path $duplicateEvidenceDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $solutionRoot "VerificationResults\verification-component-evidence.json") -Destination (Join-Path $duplicateEvidenceDirectory "verification-component-evidence.json")
    Assert-Throws -Message "duplicate evidence" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    Remove-Item -LiteralPath $duplicateEvidenceDirectory -Recurse -Force
    $duplicateLogDirectory = Join-Path $staticRoot "VerificationResults\Logs\duplicate-log"
    New-Item -ItemType Directory -Path $duplicateLogDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $staticRoot "VerificationResults\Logs\verify-watchdog.tests.ps1.log") -Destination (Join-Path $duplicateLogDirectory "verify-watchdog.tests.ps1.log")
    Assert-Throws -Message "duplicate log" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    Remove-Item -LiteralPath $duplicateLogDirectory -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $solutionRoot "VerificationResults\watchdog.log")
    Assert-Throws -Message "missing marker" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    Assert-Throws -Message "failed child" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "failure" -StaticResult "success" }
    Remove-Item -LiteralPath (Join-Path $staticRoot "VerificationResults\verification-component-evidence.json")
    Assert-Throws -Message "missing evidence" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $staticRoot -Component "static-contracts"
    [IO.File]::AppendAllText((Join-Path $solutionRoot "VerificationResults\watchdog.log"), "VERIFY_COMPLETE schema_version=1 component=solution status=passed elapsed_seconds=2`n")
    Assert-Throws -Message "duplicate marker" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    $componentEvidencePath = Join-Path $solutionRoot "VerificationResults\verification-component-evidence.json"
    $componentEvidence = Get-Content -LiteralPath $componentEvidencePath -Raw | ConvertFrom-Json
    $componentEvidence.component = "static-contracts"
    Write-TestJson -Path $componentEvidencePath -Value $componentEvidence
    Assert-Throws -Message "component mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    $solutionResultsRoot = Join-Path $solutionRoot "VerificationResults"
    $solutionManifestPath = Join-Path $solutionResultsRoot "verification-component-manifest.json"
    $solutionManifest = Get-Content -LiteralPath $solutionManifestPath -Raw | ConvertFrom-Json
    $solutionManifest.files = @($solutionManifest.files | Where-Object { $_.path -ne "required-test-report.json" })
    Write-TestJson -Path $solutionManifestPath -Value $solutionManifest
    $solutionEvidencePath = Join-Path $solutionResultsRoot "verification-component-evidence.json"
    $solutionEvidence = Get-Content -LiteralPath $solutionEvidencePath -Raw | ConvertFrom-Json
    $solutionEvidence.manifestSha256 = (Get-FileHash -LiteralPath $solutionManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TestJson -Path $solutionEvidencePath -Value $solutionEvidence
    Assert-Throws -Message "manifest omission" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    Add-Content -LiteralPath (Join-Path $solutionRoot "VerificationResults\lane-1.trx") -Value "tampered"
    Assert-Throws -Message "manifest tamper" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    Assert-Throws -Message "SHA mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "wrong-head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    Assert-Throws -Message "run mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "wrong-run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    Assert-Throws -Message "attempt mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "wrong-attempt" -SolutionResult "success" -StaticResult "success" }
    Remove-Item -LiteralPath (Join-Path $solutionRoot "VerificationResults\required-test-partition.json")
    Assert-Throws -Message "incomplete solution reports" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    Remove-Item -LiteralPath (Join-Path $staticRoot "VerificationResults\Logs\verify-watchdog.tests.ps1.log")
    Assert-Throws -Message "missing static contract" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "attempt" -SolutionResult "success" -StaticResult "success" }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "Promotion fan-in contract tests passed ($assertionCount assertions)."
