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
    param([scriptblock]$Action, [string]$Message, [string]$ExpectedMessage)
    try {
        & $Action
        throw "Expected failure: $Message"
    }
    catch {
        if ($_.Exception.Message -like "Expected failure: $Message") { throw }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedMessage) -and $_.Exception.Message.IndexOf($ExpectedMessage, [StringComparison]::Ordinal) -lt 0) {
            throw "Failure for '$Message' did not identify the expected cause '$ExpectedMessage'. Actual: $($_.Exception.Message)"
        }
        $script:assertionCount++
    }
}

function Write-TestJson {
    param([string]$Path, [object]$Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
}

function Get-TestPackages {
    return @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src") -Directory -Recurse | Where-Object { Test-Path (Join-Path $_.FullName ($_.Name + ".csproj")) } | Sort-Object Name | ForEach-Object Name)
}

function Get-TestSourceFile {
    param([string]$PackageName)
    $packageRoot = Join-Path (Join-Path $repoRoot "src") $PackageName
    return (Get-ChildItem -LiteralPath $packageRoot -Filter "*.cs" -File -Recurse | Sort-Object FullName | Select-Object -First 1)
}

function Write-TestCoverageReport {
    param([string]$Path)

    $packageNodes = [Collections.Generic.List[string]]::new()
    foreach ($package in Get-TestPackages) {
        $sourceFile = Get-TestSourceFile -PackageName $package
        if ($null -eq $sourceFile) { throw "Test fixture cannot find a source file for package $package." }
        $relativeFile = [IO.Path]::GetRelativePath($repoRoot, $sourceFile.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/")
        $packageNodes.Add("<package name=`"$package`"><classes><class filename=`"$relativeFile`"><lines><line number=`"1`" hits=`"1`" /></lines></class></classes></package>")
    }
    $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><coverage><packages>$($packageNodes -join '')</packages></coverage>"
    [IO.File]::WriteAllText($Path, $xml, [Text.UTF8Encoding]::new($false))
}

function Write-TestTrx {
    param([string]$Path, [string[]]$TestId, [string[]]$ExecutionId)
    if ($TestId.Count -ne $ExecutionId.Count) { throw "TRX fixture test and execution identities must match." }
    $results = @(for ($index = 0; $index -lt $TestId.Count; $index++) { "<UnitTestResult testId=`"$($TestId[$index])`" executionId=`"$($ExecutionId[$index])`" outcome=`"Passed`" />" })
    $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results>$($results -join '')</Results></TestRun>"
    [IO.File]::WriteAllText($Path, $xml, [Text.UTF8Encoding]::new($false))
}

function Update-TestComponentAuth {
    param([string]$Root)

    $resultsRoot = Join-Path $Root "VerificationResults"
    $evidencePath = Join-Path $resultsRoot "verification-component-evidence.json"
    $manifestPath = Join-Path $resultsRoot "verification-component-manifest.json"
    $watchdogEvidencePath = Join-Path $resultsRoot "verification-watchdog-evidence.json"
    $manifestEntries = @(Get-ChildItem -LiteralPath $resultsRoot -Recurse -File | Where-Object { $_.FullName -ne $evidencePath -and $_.FullName -ne $manifestPath -and $_.FullName -ne $watchdogEvidencePath -and $_.Name -ne "watchdog.log" } | ForEach-Object { [ordered]@{ path = [IO.Path]::GetRelativePath($resultsRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/"); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    Write-TestJson -Path $manifestPath -Value ([ordered]@{ schemaVersion = 1; files = @($manifestEntries | Sort-Object path) })
    $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    $evidence.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TestJson -Path $evidencePath -Value $evidence
    $watchdogEvidence = Get-Content -LiteralPath $watchdogEvidencePath -Raw | ConvertFrom-Json
    $watchdogEvidence.componentEvidenceSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $watchdogEvidence.componentManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-TestJson -Path $watchdogEvidencePath -Value $watchdogEvidence
}

function Update-TestCoverageAuth {
    param([string]$Root)

    $resultsRoot = Join-Path $Root "VerificationResults"
    $manifestPath = Join-Path $resultsRoot "coverage-manifest.json"
    $coverageManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($report in @($coverageManifest.reports)) {
        $file = Get-Item -LiteralPath ([string]$report.path)
        $report.length = $file.Length
        $report.sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Write-TestJson -Path $manifestPath -Value $coverageManifest
}

function New-TestComponent {
    param([string]$Root, [ValidateSet("solution", "nested-process", "static-contracts")] [string]$Component, [int]$NestedTestCount = 5)

    if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
    $resultsRoot = Join-Path $Root "VerificationResults"
    $logsRoot = Join-Path $resultsRoot "Logs"
    New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
    $phaseNames = if ($Component -ceq "static-contracts") { @("contract-verify-sdk-diagnostics.tests", "contract-verify-preflight-overlap.tests", "contract-verify-coverage.tests", "contract-verify-bounded-phases.tests", "contract-verify-parallel.tests", "contract-verify-test-inventory.tests", "contract-verify-watchdog.tests", "contract-verify-promotion-fan-in.tests", "frontend-preflight", "restore-static", "format-whitespace", "format-naming-style", "git-diff-check") } else { @() }
    $markers = @($phaseNames | ForEach-Object { "VERIFY_PHASE_COMPLETE name=$_ elapsed_seconds=1 completed_at_utc=2026-01-01T00:00:00.0000000+00:00`n" })
    $marker = "$(($markers -join ''))VERIFY_COMPLETE schema_version=1 component=$Component status=passed elapsed_seconds=1`n"
    [IO.File]::WriteAllText((Join-Path $resultsRoot "watchdog.log"), $marker, [Text.UTF8Encoding]::new($false))

    if ($Component -ceq "static-contracts") {
        foreach ($name in @("verify-sdk-diagnostics.tests.ps1", "verify-preflight-overlap.tests.ps1", "verify-coverage.tests.ps1", "verify-bounded-phases.tests.ps1", "verify-parallel.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1", "verify-promotion-fan-in.tests.ps1")) {
            [IO.File]::WriteAllText((Join-Path $logsRoot "$name.log"), "passed", [Text.UTF8Encoding]::new($false))
        }
        foreach ($name in @("frontend-preflight.log", "restore-static.log", "format-whitespace.log", "format-naming-style.log", "git-diff-check.log")) {
            [IO.File]::WriteAllText((Join-Path $logsRoot $name), $(if ($name -in @("format-whitespace.log", "format-naming-style.log", "git-diff-check.log")) { "" } else { "passed" }), [Text.UTF8Encoding]::new($false))
        }
    }
    else {
        $laneEntries = @(Get-FanInSourceOwnedLaneDefinitions -Component $(if ($Component -ceq "nested-process") { "NestedProcess" } else { "Solution" }) | ForEach-Object { [ordered]@{ name = $_.name; projectName = $_.projectName; filter = $_.filter } })
        $tests = @($laneEntries | ForEach-Object -Begin { $index = if ($Component -ceq "nested-process") { 10 } else { 1 } } -Process {
            $testCount = if ($Component -ceq "nested-process") { $NestedTestCount } else { 1 }
            $testIds = @(for ($offset = 0; $offset -lt $testCount; $offset++) { "00000000-0000-0000-0000-$(($index + $offset).ToString('000000000000'))" })
            $executionIds = @(for ($offset = 0; $offset -lt $testCount; $offset++) { "10000000-0000-0000-0000-$(($index + $offset).ToString('000000000000'))" })
            $laneRoot = Join-Path $resultsRoot ("StandardTests/" + $_.name)
            New-Item -ItemType Directory -Path $laneRoot -Force | Out-Null
            $trxPath = Join-Path $laneRoot ($_.name + ".trx")
            $coveragePath = Join-Path $laneRoot "coverage.cobertura.xml"
            Write-TestTrx -Path $trxPath -TestId $testIds -ExecutionId $executionIds
            Write-TestCoverageReport -Path $coveragePath
            $current = [pscustomobject]@{ Lane = $_; TestIds = $testIds; ExecutionIds = $executionIds; TrxPath = $trxPath; CoveragePath = $coveragePath }
            $index++
            $current
        })
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-lanes.json") -Value ([ordered]@{ schemaVersion = 1; lanes = @($laneEntries) })
        $expectedTests = @($tests | ForEach-Object { $test = $_; foreach ($testId in $test.TestIds) { [ordered]@{ id = $testId; lane = $test.Lane.name; xunitTestCaseUniqueId = "fixture-$testId" } } })
        $expectedCount = $expectedTests.Count
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-partition.json") -Value ([ordered]@{ schemaVersion = 1; canonicalInventoryCount = if ($Component -ceq "nested-process") { 1 } else { 9 }; laneDefinitionCount = if ($Component -ceq "nested-process") { 1 } else { 9 }; canonicalTestCount = $expectedCount; laneTestCount = $expectedCount; emptyLanes = @(); missing = @(); unexpected = @(); overlap = @(); duplicateCanonical = @(); duplicateExecutionIds = @() })
        Write-TestJson -Path (Join-Path $resultsRoot "required-execution-tests.json") -Value ([ordered]@{ schemaVersion = 1; totalTests = $expectedCount; tests = @($expectedTests) })
        Write-TestJson -Path (Join-Path $resultsRoot "required-test-report.json") -Value ([ordered]@{ schemaVersion = 1; expectedCount = $expectedCount; executedCount = $expectedCount; uniqueExecutedCount = $expectedCount; missing = @(); unexpected = @(); crossReportOverlap = @(); duplicateExecutionId = @(); nonPassing = @() })
        $reportEntries = @($tests | ForEach-Object { $file = Get-Item -LiteralPath $_.CoveragePath; [ordered]@{ kind = "lane"; laneName = "tests-$($_.Lane.name)"; laneResultsRoot = [IO.Path]::GetDirectoryName($_.CoveragePath); trxPath = $_.TrxPath; deploymentRoot = "Deployment"; path = $_.CoveragePath; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
        Write-TestJson -Path (Join-Path $resultsRoot "coverage-manifest.json") -Value ([ordered]@{ schemaVersion = 1; resultsRoot = $resultsRoot; minimumWriteTimeUtc = "2026-01-01T00:00:00.0000000Z"; laneReportCount = $tests.Count; childReportCount = 0; aliasReportCount = 0; reports = @($reportEntries); aliases = @() })
        Write-TestJson -Path (Join-Path $resultsRoot "coverage-summary.json") -Value ([ordered]@{ schemaVersion = 1; threshold = 0.9; reports = @($reportEntries | ForEach-Object { [ordered]@{ path = $_.path } }); packages = @(Get-TestPackages | ForEach-Object { [ordered]@{ package = $_; lineRate = 1 } }); failures = @() })
        $laneCount = $tests.Count
        $inventoryComplete = $true
        $coverageComplete = $true
    }

    $evidence = [ordered]@{ schemaVersion = 1; component = $Component; repositoryHead = "head"; githubRunId = "run"; githubRunAttempt = "attempt"; laneCount = if ($Component -ceq "static-contracts") { 0 } elseif ($Component -ceq "nested-process") { 1 } else { 9 }; inventoryComplete = ($Component -ne "static-contracts"); coverageComplete = ($Component -ne "static-contracts"); staticContractCount = if ($Component -ceq "static-contracts") { 8 } else { 0 }; frontendComplete = ($Component -ceq "static-contracts"); formatComplete = ($Component -eq "static-contracts"); diffComplete = ($Component -eq "static-contracts"); manifestSha256 = "" }
    $evidencePath = Join-Path $resultsRoot "verification-component-evidence.json"
    Write-TestJson -Path $evidencePath -Value $evidence
    $manifestPath = Join-Path $resultsRoot "verification-component-manifest.json"
    $watchdogEvidencePath = Join-Path $resultsRoot "verification-watchdog-evidence.json"
    $watchdogEvidence = [ordered]@{ schemaVersion = 1; component = $Component; mode = "promotion"; repositoryHead = "head"; githubRunId = "run"; githubRunAttempt = "attempt"; deadlineSeconds = if ($Component -eq "solution") { 1500 } else { 600 }; elapsedSeconds = 1; exitCode = 0; completionMarkerCount = 1; status = "passed"; watchdogLogSha256 = (Get-FileHash -LiteralPath (Join-Path $resultsRoot "watchdog.log") -Algorithm SHA256).Hash.ToLowerInvariant(); componentEvidenceSha256 = ""; componentManifestSha256 = "" }
    Write-TestJson -Path $watchdogEvidencePath -Value $watchdogEvidence
    Update-TestComponentAuth -Root $Root
}

function Invoke-TestFanIn {
    param([string]$SolutionRoot, [string]$NestedRoot, [string]$StaticRoot, [string]$ExpectedHead = "head", [string]$ExpectedRunId = "run", [string]$ExpectedRunAttempt = "attempt", [string]$NestedResult = "success")
    Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $SolutionRoot -NestedArtifactRoot $NestedRoot -StaticArtifactRoot $StaticRoot -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt -SolutionResult "success" -NestedResult $NestedResult -StaticResult "success"
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-promotion-fan-in-" + [Guid]::NewGuid().ToString("N"))
$solutionRoot = Join-Path $fixtureRoot "solution"
$nestedRoot = Join-Path $fixtureRoot "nested"
$staticRoot = Join-Path $fixtureRoot "static"
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
try {
    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    New-TestComponent -Root $staticRoot -Component "static-contracts"
    $productionNestedLane = Get-Content -LiteralPath (Join-Path $nestedRoot "VerificationResults/required-test-lanes.json") -Raw | ConvertFrom-Json
    $productionNestedCoverage = Get-Content -LiteralPath (Join-Path $nestedRoot "VerificationResults/coverage-manifest.json") -Raw | ConvertFrom-Json
    Assert-True -Condition ($productionNestedLane.lanes[0].name -ceq "EmbodySense.Core.Startup.Tests-nested-process") -Message "Inventory lane identity must match the canonical verifier producer."
    Assert-True -Condition ($productionNestedCoverage.reports[0].laneName -ceq "tests-EmbodySense.Core.Startup.Tests-nested-process") -Message "Coverage phase identity must retain the canonical tests prefix."
    $productionSolutionLanes = Get-Content -LiteralPath (Join-Path $solutionRoot "VerificationResults/required-test-lanes.json") -Raw | ConvertFrom-Json
    $ordinaryLane = @($productionSolutionLanes.lanes | Where-Object { $_.projectName -ceq "EmbodySense.Cli.Command.Tests" })
    $browserLane = @($productionSolutionLanes.lanes | Where-Object { $_.projectName -ceq "EmbodySense.E2ETests" })
    Assert-True -Condition ($ordinaryLane.Count -eq 1 -and $ordinaryLane[0].filter -ceq "(VerificationTier!=Stress)") -Message "Empty additional exclusions must preserve the canonical ordinary-project filter."
    Assert-True -Condition ($browserLane.Count -eq 1 -and $browserLane[0].filter -ceq "(FullyQualifiedName!~BrowserFlowTests)&(VerificationTier!=Stress)") -Message "The required inventory must retain the canonical BrowserFlowTests exclusion."
    $output = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot
    $outputText = [string]::Join("`n", @($output | ForEach-Object { [string]$_ }))
    Assert-True -Condition $outputText.Contains("lanes=10 projects=9") -Message "The successful fan-in did not prove the ten-lane nine-project aggregate."

    $nestedCoverageManifestPath = Join-Path $nestedRoot "VerificationResults/coverage-manifest.json"
    $nestedCoverageManifest = Get-Content -LiteralPath $nestedCoverageManifestPath -Raw | ConvertFrom-Json
    $originalResultsRoot = [string]$nestedCoverageManifest.resultsRoot
    $windowsResultsRoot = 'D:\a\agenthome-poc\agenthome-poc\tests\VerificationResults'
    foreach ($report in $nestedCoverageManifest.reports) {
        foreach ($property in @("path", "trxPath", "laneResultsRoot")) {
            $report.$property = $windowsResultsRoot.ToLowerInvariant() + ([string]$report.$property).Substring($originalResultsRoot.Length).Replace('\', '/')
        }
    }
    $nestedCoverageManifest.resultsRoot = $windowsResultsRoot
    $nestedCoverageSummaryPath = Join-Path $nestedRoot "VerificationResults/coverage-summary.json"
    $nestedCoverageSummary = Get-Content -LiteralPath $nestedCoverageSummaryPath -Raw | ConvertFrom-Json
    $nestedCoverageSummary.reports[0].path = $nestedCoverageManifest.reports[0].path
    Write-TestJson -Path $nestedCoverageSummaryPath -Value $nestedCoverageSummary
    Write-TestJson -Path $nestedCoverageManifestPath -Value $nestedCoverageManifest
    Update-TestComponentAuth -Root $nestedRoot
    $windowsOutput = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot
    Assert-True -Condition ([string]::Join("`n", @($windowsOutput)).Contains("lanes=10 projects=9")) -Message "Windows-origin receipts must retain their declared root across separator and case normalization."

    foreach ($property in @("path", "trxPath")) {
        $originalPath = [string]$nestedCoverageManifest.reports[0].$property
        $nestedCoverageManifest.reports[0].$property = 'X:\remapped\VerificationResults' + $originalPath.Substring($windowsResultsRoot.Length)
        Write-TestJson -Path $nestedCoverageManifestPath -Value $nestedCoverageManifest
        Update-TestComponentAuth -Root $nestedRoot
        Assert-Throws -Message "remapped coverage $property" -ExpectedMessage "cannot be mapped to its declared VerificationResults root" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }
        $nestedCoverageManifest.reports[0].$property = $originalPath
    }

    $canonicalCoverage = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.cobertura.xml" -File | Select-Object -First 1
    $aliasPath = Join-Path $canonicalCoverage.DirectoryName "staging.cobertura.xml"
    Copy-Item -LiteralPath $canonicalCoverage.FullName -Destination $aliasPath
    $alias = [ordered]@{
        path = $windowsResultsRoot + $aliasPath.Substring($originalResultsRoot.Length).Replace('\', '/')
        canonicalPath = [string]$nestedCoverageManifest.reports[0].path
        length = $canonicalCoverage.Length
        sha256 = (Get-FileHash -LiteralPath $canonicalCoverage.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $nestedCoverageManifest.aliasReportCount = 1
    $nestedCoverageManifest.aliases = @($alias)
    Write-TestJson -Path $nestedCoverageManifestPath -Value $nestedCoverageManifest
    Update-TestComponentAuth -Root $nestedRoot
    $aliasOutput = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot
    Assert-True -Condition ([string]::Join("`n", @($aliasOutput)).Contains("lanes=10 projects=9")) -Message "A byte-identical staging alias within the declared Windows root must remain admissible."
    $alias.path = 'X:\remapped\VerificationResults' + ([string]$alias.path).Substring($windowsResultsRoot.Length)
    Write-TestJson -Path $nestedCoverageManifestPath -Value $nestedCoverageManifest
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "remapped coverage alias" -ExpectedMessage "cannot be mapped to its declared VerificationResults root" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }
    New-TestComponent -Root $nestedRoot -Component "nested-process"

    $nestedResultsRoot = Join-Path $nestedRoot "VerificationResults"
    $nestedCoverage = Get-ChildItem -LiteralPath $nestedResultsRoot -Recurse -Filter "*.cobertura.xml" -File | Select-Object -First 1
    $remappedCoveragePath = Join-Path (Join-Path $nestedResultsRoot "remapped") ([IO.Path]::GetRelativePath($nestedResultsRoot, $nestedCoverage.FullName))
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($remappedCoveragePath)) -Force | Out-Null
    Move-Item -LiteralPath $nestedCoverage.FullName -Destination $remappedCoveragePath
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "remapped artifact manifest entry" -ExpectedMessage "not represented exactly once in the component artifact manifest" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }
    New-TestComponent -Root $nestedRoot -Component "nested-process"

    foreach ($nestedCount in @(4, 6)) {
        New-TestComponent -Root $nestedRoot -Component "nested-process" -NestedTestCount $nestedCount
        Assert-Throws -Message "nested fixture count $nestedCount" -ExpectedMessage "Nested-process partition reconciliation is incomplete or non-clean" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }
    }
    New-TestComponent -Root $nestedRoot -Component "nested-process"

    $nestedCoverageManifestPath = Join-Path $nestedRoot "VerificationResults/coverage-manifest.json"
    $wrongCoveragePhase = Get-Content -LiteralPath $nestedCoverageManifestPath -Raw | ConvertFrom-Json
    $wrongCoveragePhase.reports[0].laneName = "EmbodySense.Core.Startup.Tests-nested-process"
    Write-TestJson -Path $nestedCoverageManifestPath -Value $wrongCoveragePhase
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "coverage phase identity lacks producer prefix" -ExpectedMessage "not bound to one source-owned lane" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }
    New-TestComponent -Root $nestedRoot -Component "nested-process"

    Assert-Throws -Message "failed nested child" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -NestedResult "failure" }
    Assert-Throws -Message "missing nested artifact root" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot (Join-Path $fixtureRoot "missing") -StaticRoot $staticRoot }
    Assert-Throws -Message "head mismatch" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -ExpectedHead "wrong-head" }
    Assert-Throws -Message "run mismatch" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -ExpectedRunId "wrong-run" }
    Assert-Throws -Message "attempt mismatch" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -ExpectedRunAttempt "wrong-attempt" }

    $nestedEvidencePath = Join-Path $nestedRoot "VerificationResults/verification-component-evidence.json"
    $nestedEvidence = Get-Content -LiteralPath $nestedEvidencePath -Raw | ConvertFrom-Json
    $nestedEvidence.laneCount = 2
    Write-TestJson -Path $nestedEvidencePath -Value $nestedEvidence
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "nested lane count tamper" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedLanesPath = Join-Path $nestedRoot "VerificationResults/required-test-lanes.json"
    $nestedLanes = Get-Content -LiteralPath $nestedLanesPath -Raw | ConvertFrom-Json
    $nestedLanes.lanes[0].projectName = "EmbodySense.Core.Application.Tests"
    Write-TestJson -Path $nestedLanesPath -Value $nestedLanes
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "nested source-owned lane tamper" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedTrx = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.trx" -File | Select-Object -First 1
    $nestedTrxText = Get-Content -LiteralPath $nestedTrx.FullName -Raw
    $nestedTrxText = $nestedTrxText.Replace("00000000-0000-0000-0000-000000000010", "00000000-0000-0000-0000-000000000009")
    [IO.File]::WriteAllText($nestedTrx.FullName, $nestedTrxText, [Text.UTF8Encoding]::new($false))
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "nested TRX source inventory mismatch" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedTrx = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.trx" -File | Select-Object -First 1
    $wrongLaneTrx = Join-Path $nestedTrx.DirectoryName "tests-not-source-owned.trx"
    Move-Item -LiteralPath $nestedTrx.FullName -Destination $wrongLaneTrx
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "nested TRX lane attribution mismatch" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedCoverage = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.cobertura.xml" -File | Select-Object -First 1
    Add-Content -LiteralPath $nestedCoverage.FullName -Value "tampered"
    Assert-Throws -Message "coverage artifact tamper" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedTrx = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.trx" -File | Select-Object -First 1
    $dtdTrx = "<?xml version=`"1.0`"?><!DOCTYPE TestRun [<!ENTITY xxe `"blocked`">]><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results><UnitTestResult testId=`"00000000-0000-0000-0000-000000000010`" executionId=`"10000000-0000-0000-0000-000000000010`" outcome=`"Passed`" /></Results></TestRun>"
    [IO.File]::WriteAllText($nestedTrx.FullName, $dtdTrx, [Text.UTF8Encoding]::new($false))
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "DTD-prohibited TRX" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedTrx = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.trx" -File | Select-Object -First 1
    Remove-Item -LiteralPath $nestedTrx.FullName
    Assert-Throws -Message "missing authenticated TRX" -ExpectedMessage "Authenticated component file is missing" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $solutionTrxFiles = @(Get-ChildItem -LiteralPath (Join-Path $solutionRoot "VerificationResults") -Recurse -Filter "*.trx" -File | Sort-Object FullName)
    $firstExecutionId = "10000000-0000-0000-0000-000000000001"
    $secondTrxText = Get-Content -LiteralPath $solutionTrxFiles[1].FullName -Raw
    $secondTrxText = $secondTrxText.Replace("10000000-0000-0000-0000-000000000002", $firstExecutionId)
    [IO.File]::WriteAllText($solutionTrxFiles[1].FullName, $secondTrxText, [Text.UTF8Encoding]::new($false))
    Update-TestComponentAuth -Root $solutionRoot
    Assert-Throws -Message "duplicate execution ID" -ExpectedMessage "duplicate execution IDs" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedTrx = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.trx" -File | Select-Object -First 1
    $failedTrxText = (Get-Content -LiteralPath $nestedTrx.FullName -Raw).Replace('outcome="Passed"', 'outcome="Failed"')
    [IO.File]::WriteAllText($nestedTrx.FullName, $failedTrxText, [Text.UTF8Encoding]::new($false))
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "non-passed nested outcome" -ExpectedMessage "non-passing test results" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedCoverage = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.cobertura.xml" -File | Select-Object -First 1
    $clientsSourceFile = Get-TestSourceFile -PackageName "EmbodySense.Core.Clients"
    $clientsRelativeFile = [IO.Path]::GetRelativePath($repoRoot, $clientsSourceFile.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/")
    $generatedRegexFile = "src/EmbodySense.Core.Clients/obj/Release/net10.0/System.Text.RegularExpressions.Generator/System.Text.RegularExpressions.Generator.RegexGenerator/RegexGenerator.g.cs"
    $generatedCoverageText = (Get-Content -LiteralPath $nestedCoverage.FullName -Raw).Replace($clientsRelativeFile, $generatedRegexFile)
    [IO.File]::WriteAllText($nestedCoverage.FullName, $generatedCoverageText, [Text.UTF8Encoding]::new($false))
    Update-TestCoverageAuth -Root $nestedRoot
    Update-TestComponentAuth -Root $nestedRoot
    $generatedOutput = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot
    Assert-True -Condition ([string]::Join("`n", @($generatedOutput)).Contains("lanes=10 projects=9")) -Message "Authenticated virtual regex-generator source must survive aggregation on a clean checkout."
    $generatedPattern = '(<package name="EmbodySense.Core.Clients".*?<line number="1" hits=")1"'
    $uncoveredGeneratedText = [regex]::Replace($generatedCoverageText, $generatedPattern, { param($match) $match.Groups[1].Value + '0"' }, [Text.RegularExpressions.RegexOptions]::Singleline)
    [IO.File]::WriteAllText($nestedCoverage.FullName, $uncoveredGeneratedText, [Text.UTF8Encoding]::new($false))
    Update-TestCoverageAuth -Root $nestedRoot
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "uncovered generated source retains coverage denominator" -ExpectedMessage "below the unchanged 90% floor" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $belowFloorPackage = "EmbodySense.Core.Common"
    foreach ($coverageRoot in @($solutionRoot, $nestedRoot)) {
        foreach ($coverageFile in @(Get-ChildItem -LiteralPath (Join-Path $coverageRoot "VerificationResults") -Recurse -Filter "*.cobertura.xml" -File)) {
            $coverageText = Get-Content -LiteralPath $coverageFile.FullName -Raw
            $coveragePattern = '(<package name="' + [regex]::Escape($belowFloorPackage) + '".*?<line number="1" hits=")1"'
            $coverageText = [regex]::Replace($coverageText, $coveragePattern, { param($match) $match.Groups[1].Value + '0"' }, [Text.RegularExpressions.RegexOptions]::Singleline)
            [IO.File]::WriteAllText($coverageFile.FullName, $coverageText, [Text.UTF8Encoding]::new($false))
        }
        Update-TestCoverageAuth -Root $coverageRoot
        Update-TestComponentAuth -Root $coverageRoot
    }
    Assert-Throws -Message "combined coverage below floor" -ExpectedMessage "below the unchanged 90% floor" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedCoverage = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.cobertura.xml" -File | Select-Object -First 1
    $outOfSourceText = (Get-Content -LiteralPath $nestedCoverage.FullName -Raw).Replace('filename="src/', 'filename="outside/')
    [IO.File]::WriteAllText($nestedCoverage.FullName, $outOfSourceText, [Text.UTF8Encoding]::new($false))
    Update-TestCoverageAuth -Root $nestedRoot
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "out-of-src coverage path" -ExpectedMessage "does not identify an existing source file beneath src/" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $nestedCoverage = Get-ChildItem -LiteralPath (Join-Path $nestedRoot "VerificationResults") -Recurse -Filter "*.cobertura.xml" -File | Select-Object -First 1
    $commonSourceFile = Get-TestSourceFile -PackageName "EmbodySense.Core.Common"
    $commonRelativeFile = [IO.Path]::GetRelativePath($repoRoot, $commonSourceFile.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/")
    $ambiguousRelativeFile = "src/EmbodySense.Core.Common/../EmbodySense.Core.Application/" + $commonSourceFile.Name
    $ambiguousText = (Get-Content -LiteralPath $nestedCoverage.FullName -Raw).Replace($commonRelativeFile, $ambiguousRelativeFile)
    [IO.File]::WriteAllText($nestedCoverage.FullName, $ambiguousText, [Text.UTF8Encoding]::new($false))
    Update-TestCoverageAuth -Root $nestedRoot
    Update-TestComponentAuth -Root $nestedRoot
    Assert-Throws -Message "ambiguous coverage path" -ExpectedMessage "does not identify an existing source file beneath src/" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $extraReceiptPath = Join-Path $nestedRoot "VerificationResults/unexpected-receipt.txt"
    [IO.File]::WriteAllText($extraReceiptPath, "unexpected", [Text.UTF8Encoding]::new($false))
    Assert-Throws -Message "closed-world extra file" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "Promotion fan-in contract tests passed ($assertionCount assertions)."
