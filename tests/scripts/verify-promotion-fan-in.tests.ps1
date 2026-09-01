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
    param([string]$Root, [string]$Component, [int]$Attempt = 1)

    $resultsRoot = Join-Path $Root "VerificationResults"
    $logsRoot = Join-Path $resultsRoot "Logs"
    New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
    $phaseNames = if ($Component -ceq "static-contracts") { @("contract-verify-sdk-diagnostics.tests", "contract-verify-preflight-overlap.tests", "contract-verify-coverage.tests", "contract-verify-bounded-phases.tests", "contract-verify-parallel.tests", "contract-verify-test-inventory.tests", "contract-verify-watchdog.tests", "contract-verify-promotion-fan-in.tests", "frontend-preflight", "restore-static", "format-whitespace", "format-naming-style", "git-diff-check") } else { @() }
    $marker = "$(($phaseNames | ForEach-Object { "VERIFY_PHASE_COMPLETE name=$_ elapsed_seconds=1 completed_at_utc=2026-01-01T00:00:00.0000000+00:00`n" }) -join '')VERIFY_COMPLETE schema_version=1 component=$Component status=passed elapsed_seconds=1`n"
    [IO.File]::WriteAllText((Join-Path $resultsRoot "watchdog.log"), $marker, [Text.UTF8Encoding]::new($false))
    if ($Component -ceq "solution") {
        $lanes = @(1..10 | ForEach-Object { [ordered]@{ name = "lane-$_"; projectName = "Project$_"; filter = "(VerificationTier!=Stress)" } })
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-lanes.json") -Value ([ordered]@{ schemaVersion = 1; lanes = $lanes })
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-partition.json") -Value ([ordered]@{ schemaVersion = 1; canonicalInventoryCount = 9; laneDefinitionCount = 10; canonicalTestCount = 1; laneTestCount = 1; emptyLanes = @(); missing = @(); unexpected = @(); overlap = @(); duplicateCanonical = @(); duplicateExecutionIds = @() })
        Write-TestJson -Path (Join-Path $resultsRoot "required-execution-tests.json") -Value ([ordered]@{ schemaVersion = 1; totalTests = 1; tests = @([ordered]@{ id = "00000000-0000-0000-0000-000000000001" }) })
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-report.json") -Value ([ordered]@{ schemaVersion = 1; expectedCount = 1; executedCount = 2; uniqueExecutedCount = 1; missing = @(); unexpected = @(); crossReportOverlap = @(); duplicateExecutionId = @(); nonPassing = @() })
        Write-TestJson -Path (Join-Path $resultsRoot "coverage-manifest.json") -Value ([ordered]@{ schemaVersion = 1; laneReportCount = 10; childReportCount = 0; aliasReportCount = 0; reports = @(1..10 | ForEach-Object { [ordered]@{ id = $_ } }); aliases = @() })
        Write-TestJson -Path (Join-Path $resultsRoot "coverage-summary.json") -Value ([ordered]@{ schemaVersion = 1; threshold = 0.9; reports = @([ordered]@{ path = "coverage" }); packages = @([ordered]@{ package = "Core" }); failures = @() })
        foreach ($index in 1..10) {
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

    $evidence = [ordered]@{ schemaVersion = 1; component = $Component; repositoryHead = "head"; githubRunId = "run"; githubRunAttempt = $Attempt.ToString([Globalization.CultureInfo]::InvariantCulture); laneCount = if ($Component -ceq "solution") { 10 } else { 0 }; inventoryComplete = ($Component -ceq "solution"); coverageComplete = ($Component -ceq "solution"); staticContractCount = if ($Component -ceq "static-contracts") { 8 } else { 0 }; frontendComplete = ($Component -ceq "static-contracts"); formatComplete = ($Component -ceq "static-contracts"); diffComplete = ($Component -ceq "static-contracts"); manifestSha256 = "" }
    $evidencePath = Join-Path $resultsRoot "verification-component-evidence.json"
    Write-TestJson -Path $evidencePath -Value $evidence
    $manifestPath = Join-Path $resultsRoot "verification-component-manifest.json"
    $watchdogEvidencePath = Join-Path $resultsRoot "verification-watchdog-evidence.json"
    $manifest = [ordered]@{ schemaVersion = 1; files = @(Get-ChildItem -LiteralPath $resultsRoot -Recurse -File | Where-Object { $_.FullName -ne $evidencePath -and $_.FullName -ne $manifestPath -and $_.FullName -ne $watchdogEvidencePath -and $_.Name -ne "watchdog.log" } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($resultsRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/"); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }) }
    Write-TestJson -Path $manifestPath -Value $manifest
    $evidence.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TestJson -Path $evidencePath -Value $evidence
    $watchdogEvidence = [ordered]@{ schemaVersion = 1; component = $Component; mode = "promotion"; repositoryHead = "head"; githubRunId = "run"; githubRunAttempt = $Attempt.ToString([Globalization.CultureInfo]::InvariantCulture); deadlineSeconds = if ($Component -ceq "solution") { 1500 } else { 600 }; elapsedSeconds = 1; exitCode = 0; completionMarkerCount = 1; status = "passed"; watchdogLogSha256 = (Get-FileHash -LiteralPath (Join-Path $resultsRoot "watchdog.log") -Algorithm SHA256).Hash.ToLowerInvariant(); componentEvidenceSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant(); componentManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    Write-TestJson -Path $watchdogEvidencePath -Value $watchdogEvidence
}

function New-TestReceiptArtifact {
    param([string]$Root, [string]$Component, [int]$Attempt)

    $artifactPrefix = if ($Component -ceq "solution") { "verification-solution-receipt-" } else { "verification-contract-receipt-" }
    $artifactRoot = Join-Path $Root ($artifactPrefix + $Attempt.ToString([Globalization.CultureInfo]::InvariantCulture))
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    New-TestComponent -Root $artifactRoot -Component $Component -Attempt $Attempt
    return $artifactRoot
}

function Update-TestComponentEvidence {
    param([string]$Root)

    $resultsRoot = Join-Path $Root "VerificationResults"
    $evidencePath = Join-Path $resultsRoot "verification-component-evidence.json"
    $manifestPath = Join-Path $resultsRoot "verification-component-manifest.json"
    $watchdogEvidencePath = Join-Path $resultsRoot "verification-watchdog-evidence.json"
    $manifest = [ordered]@{ schemaVersion = 1; files = @(Get-ChildItem -LiteralPath $resultsRoot -Recurse -File | Where-Object { $_.FullName -ne $evidencePath -and $_.FullName -ne $manifestPath -and $_.FullName -ne $watchdogEvidencePath -and $_.Name -ne "watchdog.log" } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($resultsRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/"); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }) }
    Write-TestJson -Path $manifestPath -Value $manifest
    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    $evidence.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TestJson -Path $evidencePath -Value $evidence
    $watchdogEvidence = Get-Content -LiteralPath $watchdogEvidencePath -Raw | ConvertFrom-Json
    $watchdogEvidence.componentEvidenceSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $watchdogEvidence.componentManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TestJson -Path $watchdogEvidencePath -Value $watchdogEvidence
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-promotion-fan-in-" + [Guid]::NewGuid().ToString("N"))
$solutionRoot = Join-Path $fixtureRoot "solution"
$staticRoot = Join-Path $fixtureRoot "static"
New-Item -ItemType Directory -Path $solutionRoot, $staticRoot -Force | Out-Null
try {
    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $staticRoot -Component "static-contracts"
    Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success"
    $assertionCount++

    $mixedSolutionRoot = Join-Path $fixtureRoot "mixed-solution"
    $mixedStaticRoot = Join-Path $fixtureRoot "mixed-static"
    New-Item -ItemType Directory -Path $mixedSolutionRoot, $mixedStaticRoot -Force | Out-Null
    $null = New-TestReceiptArtifact -Root $mixedSolutionRoot -Component "solution" -Attempt 1
    $null = New-TestReceiptArtifact -Root $mixedSolutionRoot -Component "solution" -Attempt 2
    $null = New-TestReceiptArtifact -Root $mixedStaticRoot -Component "static-contracts" -Attempt 1
    $null = New-TestReceiptArtifact -Root $mixedStaticRoot -Component "static-contracts" -Attempt 2
    $fullRerunOutput = @(Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $mixedSolutionRoot -StaticArtifactRoot $mixedStaticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "2" -SolutionResult "success" -StaticResult "success") -join "`n"
    Assert-True -Condition ($fullRerunOutput -match "component=solution attempt=2" -and $fullRerunOutput -match "component=static-contracts attempt=2") -Message "A full rerun must select the highest authenticated receipt for each component."

    Remove-Item -LiteralPath (Join-Path $mixedStaticRoot "verification-contract-receipt-2") -Recurse -Force
    $mixedAttemptOutput = @(Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $mixedSolutionRoot -StaticArtifactRoot $mixedStaticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "2" -SolutionResult "success" -StaticResult "success") -join "`n"
    Assert-True -Condition ($mixedAttemptOutput -match "component=solution attempt=2" -and $mixedAttemptOutput -match "component=static-contracts attempt=1") -Message "A failed-only rerun must combine the latest authenticated receipt from each successful component."

    Assert-Throws -Message "cancelled solution child" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "cancelled" -StaticResult "success" }
    Assert-Throws -Message "cancelled static child" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "cancelled" }
    Assert-Throws -Message "skipped solution child" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "skipped" -StaticResult "success" }
    Assert-Throws -Message "skipped static child" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "skipped" }

    $futureAttemptRoot = Join-Path $fixtureRoot "future-attempt"
    New-Item -ItemType Directory -Path $futureAttemptRoot -Force | Out-Null
    $null = New-TestReceiptArtifact -Root $futureAttemptRoot -Component "solution" -Attempt 3
    Assert-Throws -Message "future directory receipt attempt" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $futureAttemptRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "2" -SolutionResult "success" -StaticResult "success" }

    $futureFlatRoot = Join-Path $fixtureRoot "future-flat-attempt"
    New-Item -ItemType Directory -Path $futureFlatRoot -Force | Out-Null
    New-TestComponent -Root $futureFlatRoot -Component "solution" -Attempt 3
    Assert-Throws -Message "future flat receipt attempt" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $futureFlatRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "2" -SolutionResult "success" -StaticResult "success" }

    $boundedCandidateRoot = Join-Path $fixtureRoot "bounded-candidates"
    foreach ($attempt in 1..33) {
        $null = New-TestReceiptArtifact -Root $boundedCandidateRoot -Component "solution" -Attempt $attempt
    }
    Assert-Throws -Message "bounded receipt candidates" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $boundedCandidateRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "33" -SolutionResult "success" -StaticResult "success" }

    $latestSolutionEvidencePath = Join-Path $mixedSolutionRoot "verification-solution-receipt-2\VerificationResults\verification-component-evidence.json"
    $latestSolutionEvidence = Get-Content -LiteralPath $latestSolutionEvidencePath -Raw | ConvertFrom-Json
    $latestSolutionEvidence.repositoryHead = "stale-head"
    Write-TestJson -Path $latestSolutionEvidencePath -Value $latestSolutionEvidence
    Assert-Throws -Message "stale latest receipt" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $mixedSolutionRoot -StaticArtifactRoot $mixedStaticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "2" -SolutionResult "success" -StaticResult "success" }

    $malformedAttemptRoot = Join-Path $fixtureRoot "malformed-attempt"
    New-Item -ItemType Directory -Path $malformedAttemptRoot -Force | Out-Null
    New-TestComponent -Root (Join-Path $malformedAttemptRoot "verification-solution-receipt-2") -Component "solution" -Attempt 1
    Assert-Throws -Message "receipt directory/content attempt mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $malformedAttemptRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "2" -SolutionResult "success" -StaticResult "success" }

    $invalidNameRoot = Join-Path $fixtureRoot "invalid-name"
    New-Item -ItemType Directory -Path $invalidNameRoot -Force | Out-Null
    New-TestComponent -Root (Join-Path $invalidNameRoot "verification-solution-receipt-latest") -Component "solution" -Attempt 1
    Assert-Throws -Message "noncanonical receipt directory name" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $invalidNameRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "2" -SolutionResult "success" -StaticResult "success" }

    $watchdogEvidencePath = Join-Path $solutionRoot "VerificationResults\verification-watchdog-evidence.json"
    $watchdogEvidence = Get-Content -LiteralPath $watchdogEvidencePath -Raw | ConvertFrom-Json
    $watchdogEvidence.status = "tampered"
    Write-TestJson -Path $watchdogEvidencePath -Value $watchdogEvidence
    Assert-Throws -Message "watchdog evidence tamper" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    $extraReceiptPath = Join-Path $solutionRoot "VerificationResults\unexpected-receipt.txt"
    [IO.File]::WriteAllText($extraReceiptPath, "unexpected", [Text.UTF8Encoding]::new($false))
    Assert-Throws -Message "closed-world extra file" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    Remove-Item -LiteralPath $extraReceiptPath

    $duplicateEvidenceDirectory = Join-Path $solutionRoot "duplicate-evidence"
    New-Item -ItemType Directory -Path $duplicateEvidenceDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $solutionRoot "VerificationResults\verification-component-evidence.json") -Destination (Join-Path $duplicateEvidenceDirectory "verification-component-evidence.json")
    Assert-Throws -Message "duplicate evidence" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    Remove-Item -LiteralPath $duplicateEvidenceDirectory -Recurse -Force
    $duplicateLogDirectory = Join-Path $staticRoot "VerificationResults\Logs\duplicate-log"
    New-Item -ItemType Directory -Path $duplicateLogDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $staticRoot "VerificationResults\Logs\verify-watchdog.tests.ps1.log") -Destination (Join-Path $duplicateLogDirectory "verify-watchdog.tests.ps1.log")
    Assert-Throws -Message "duplicate log" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    Remove-Item -LiteralPath $duplicateLogDirectory -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $solutionRoot "VerificationResults\watchdog.log")
    Assert-Throws -Message "missing marker" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    Assert-Throws -Message "failed child" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "failure" -StaticResult "success" }
    Remove-Item -LiteralPath (Join-Path $staticRoot "VerificationResults\verification-component-evidence.json")
    Assert-Throws -Message "missing evidence" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $staticRoot -Component "static-contracts"
    [IO.File]::AppendAllText((Join-Path $solutionRoot "VerificationResults\watchdog.log"), "VERIFY_COMPLETE schema_version=1 component=solution status=passed elapsed_seconds=2`n")
    Assert-Throws -Message "duplicate marker" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    $componentEvidencePath = Join-Path $solutionRoot "VerificationResults\verification-component-evidence.json"
    $componentEvidence = Get-Content -LiteralPath $componentEvidencePath -Raw | ConvertFrom-Json
    $componentEvidence.component = "static-contracts"
    Write-TestJson -Path $componentEvidencePath -Value $componentEvidence
    Assert-Throws -Message "component mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
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
    Assert-Throws -Message "manifest omission" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    $staleLaneEvidence = Get-Content -LiteralPath (Join-Path $solutionRoot "VerificationResults\verification-component-evidence.json") -Raw | ConvertFrom-Json
    $staleLaneEvidence.laneCount = 9
    Write-TestJson -Path (Join-Path $solutionRoot "VerificationResults\verification-component-evidence.json") -Value $staleLaneEvidence
    Update-TestComponentEvidence -Root $solutionRoot
    Assert-Throws -Message "stale nine-lane evidence" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    $malformedLaneDefinitions = Get-Content -LiteralPath (Join-Path $solutionRoot "VerificationResults\required-test-lanes.json") -Raw | ConvertFrom-Json
    $malformedLaneDefinitions.lanes = @($malformedLaneDefinitions.lanes) + [pscustomobject]@{ name = "lane-11"; projectName = "Project11"; filter = "(VerificationTier!=Stress)" }
    Write-TestJson -Path (Join-Path $solutionRoot "VerificationResults\required-test-lanes.json") -Value $malformedLaneDefinitions
    Update-TestComponentEvidence -Root $solutionRoot
    Assert-Throws -Message "malformed eleven-lane evidence" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    Add-Content -LiteralPath (Join-Path $solutionRoot "VerificationResults\lane-1.trx") -Value "tampered"
    Assert-Throws -Message "manifest tamper" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    Assert-Throws -Message "SHA mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "wrong-head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    Assert-Throws -Message "run mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "wrong-run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    Assert-Throws -Message "attempt mismatch" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "wrong-attempt" -SolutionResult "success" -StaticResult "success" }
    Remove-Item -LiteralPath (Join-Path $solutionRoot "VerificationResults\required-test-partition.json")
    Assert-Throws -Message "incomplete solution reports" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
    New-TestComponent -Root $solutionRoot -Component "solution"
    Remove-Item -LiteralPath (Join-Path $staticRoot "VerificationResults\Logs\verify-watchdog.tests.ps1.log")
    Assert-Throws -Message "missing static contract" -Action { Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $solutionRoot -StaticArtifactRoot $staticRoot -ExpectedHead "head" -ExpectedRunId "run" -ExpectedRunAttempt "1" -SolutionResult "success" -StaticResult "success" }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "Promotion fan-in contract tests passed ($assertionCount assertions)."
