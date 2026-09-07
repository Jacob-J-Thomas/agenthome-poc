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

function Copy-TestMacOSPlatformSelection {
    param([object[]]$Selection)

    return @($Selection | ForEach-Object {
        $copy = [ordered]@{}
        foreach ($property in $_.PSObject.Properties) { $copy[$property.Name] = $property.Value }
        [pscustomobject]$copy
    })
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

function Set-TestCoveragePackageLines {
    param([string]$Root, [string]$PackageName, [int[]]$Hits)

    $sourceFile = Get-TestSourceFile -PackageName $PackageName
    $relativeFile = [IO.Path]::GetRelativePath($repoRoot, $sourceFile.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/")
    $lines = @($Hits | ForEach-Object -Begin { $number = 1 } -Process { $line = "<line number=`"$number`" hits=`"$_`" />"; $number++; $line }) -join ''
    $replacement = "<package name=`"$PackageName`"><classes><class filename=`"$relativeFile`"><lines>$lines</lines></class></classes></package>"
    $pattern = '<package name="' + [regex]::Escape($PackageName) + '"><classes><class filename="[^"]+"><lines>.*?</lines></class></classes></package>'
    foreach ($coverageFile in @(Get-ChildItem -LiteralPath (Join-Path $Root "VerificationResults") -Recurse -Filter "*.cobertura.xml" -File)) {
        $xml = Get-Content -LiteralPath $coverageFile.FullName -Raw
        $updated = [regex]::Replace($xml, $pattern, $replacement, [Text.RegularExpressions.RegexOptions]::Singleline)
        if ($updated -ceq $xml) { throw "Test fixture did not replace coverage for $PackageName." }
        [IO.File]::WriteAllText($coverageFile.FullName, $updated, [Text.UTF8Encoding]::new($false))
    }
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
    param([string]$SolutionRoot, [string]$NestedRoot, [string]$StaticRoot, [string]$MacOSRoot = $macOSRoot, [string]$ExpectedHead = "head", [string]$ExpectedRunId = "run", [string]$ExpectedRunAttempt = "attempt", [string]$NestedResult = "success", [string]$MacOSResult = "success")
    Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $SolutionRoot -NestedArtifactRoot $NestedRoot -StaticArtifactRoot $StaticRoot -MacOSArtifactRoot $MacOSRoot -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt -SolutionResult "success" -NestedResult $NestedResult -StaticResult "success" -MacOSResult $MacOSResult
}

function New-TestMacOSPlatformComponent {
    param([string]$Root)

    if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
    $receiptRoot = Join-Path $Root "VerificationResults"
    $platformRoot = Join-Path $receiptRoot "MacOSPlatformContract"
    New-Item -ItemType Directory -Path $platformRoot -Force | Out-Null
    $selection = @(Get-FanInMacOSPlatformContractSelection)
    . (Join-Path $repoRoot "scripts\verify-macos-platform-contract.ps1") -NoRun
    $assemblies = @(Get-MacOSPlatformContractAssemblies -Selection $selection)
    $facts = [Collections.Generic.List[object]]::new()
    $discoveries = [Collections.Generic.List[object]]::new()
    $phaseNames = @("build-macos-platform-contract")
    $index = 1
    foreach ($assembly in $assemblies) {
        $assemblyRoot = Join-Path $platformRoot $assembly.Assembly
        New-Item -ItemType Directory -Path $assemblyRoot -Force | Out-Null
        $results = [Collections.Generic.List[string]]::new()
        $definitions = [Collections.Generic.List[string]]::new()
        $entries = [Collections.Generic.List[string]]::new()
        foreach ($entry in $assembly.Group) {
            $testId = "00000000-0000-0000-0000-$($index.ToString('000000000000'))"
            $executionId = "10000000-0000-0000-0000-$($index.ToString('000000000000'))"
            $parts = $entry.Fact -split '\.'
            $class = $parts[0..($parts.Count - 2)] -join "."
            $results.Add("<UnitTestResult testName=`"$($entry.Fact)`" testId=`"$testId`" executionId=`"$executionId`" outcome=`"Passed`" />")
            $definitions.Add("<UnitTest name=`"$($entry.Fact)`" storage=`"$($assembly.TestAssemblyPath)`" id=`"$testId`"><Execution id=`"$executionId`" /><TestMethod className=`"$class`" name=`"$($parts[-1])`" codeBase=`"$($assembly.TestAssemblyPath)`" /></UnitTest>")
            $entries.Add("<TestEntry testId=`"$testId`" executionId=`"$executionId`" />")
            $discoveries.Add([pscustomobject][ordered]@{ project = $assembly.Project; assembly = $assembly.Assembly; fact = $entry.Fact; testId = $testId; xunitTestCaseUniqueId = "fixture-$testId"; testAssembly = [IO.Path]::GetRelativePath($repoRoot, $assembly.TestAssemblyPath).Replace([IO.Path]::DirectorySeparatorChar, "/") })
            $fact = [ordered]@{ project = $entry.Project; source = $entry.Source; assembly = $assembly.Assembly; fact = $entry.Fact; outcome = "Passed"; testId = $testId; executionId = $executionId; xunitTestCaseUniqueId = "fixture-$testId"; trx = "MacOSPlatformContract/$($assembly.Assembly)/macos-platform-contract.trx" }
            if ($null -ne $entry.PSObject.Properties["HelperSource"]) { $fact.helperSource = $entry.HelperSource }
            $facts.Add([pscustomobject]$fact)
            $index++
        }
        $count = $assembly.Group.Count
        $counters = "total=`"$count`" executed=`"$count`" passed=`"$count`" completed=`"0`" failed=`"0`" error=`"0`" timeout=`"0`" aborted=`"0`" inconclusive=`"0`" passedButRunAborted=`"0`" notRunnable=`"0`" notExecuted=`"0`" disconnected=`"0`" warning=`"0`" inProgress=`"0`" pending=`"0`""
        [IO.File]::WriteAllText((Join-Path $assemblyRoot "macos-platform-contract.trx"), "<TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results>$($results -join '')</Results><TestDefinitions>$($definitions -join '')</TestDefinitions><TestEntries>$($entries -join '')</TestEntries><ResultSummary outcome=`"Completed`"><Counters $counters /></ResultSummary></TestRun>", [Text.UTF8Encoding]::new($false))
        $phaseNames += @("discover-macos-$($assembly.Assembly)", "macos-$($assembly.Assembly)")
    }
    Write-TestJson -Path (Join-Path $platformRoot "macos-platform-contract-results.json") -Value ([ordered]@{ schemaVersion = 1; discoveries = @($discoveries | Sort-Object fact); facts = @($facts | Sort-Object fact) })
    $phaseMarkers = @($phaseNames | ForEach-Object { "VERIFY_PHASE_COMPLETE name=$_ elapsed_seconds=1 completed_at_utc=2026-01-01T00:00:00.0000000+00:00`n" })
    [IO.File]::WriteAllText((Join-Path $receiptRoot "watchdog.log"), "$($phaseMarkers -join '')VERIFY_COMPLETE schema_version=1 component=macos-platform-contract status=passed elapsed_seconds=1`n", [Text.UTF8Encoding]::new($false))
    $evidencePath = Join-Path $receiptRoot "verification-component-evidence.json"
    $manifestPath = Join-Path $receiptRoot "verification-component-manifest.json"
    $watchdogEvidencePath = Join-Path $receiptRoot "verification-watchdog-evidence.json"
    Write-TestJson -Path $evidencePath -Value ([ordered]@{ schemaVersion = 1; component = "macos-platform-contract"; repositoryHead = "head"; githubRunId = "run"; githubRunAttempt = "attempt"; laneCount = 6; inventoryComplete = $true; coverageComplete = $false; staticContractCount = 0; frontendComplete = $false; formatComplete = $false; diffComplete = $false; manifestSha256 = "" })
    Write-TestJson -Path $watchdogEvidencePath -Value ([ordered]@{ schemaVersion = 1; component = "macos-platform-contract"; mode = "promotion"; repositoryHead = "head"; githubRunId = "run"; githubRunAttempt = "attempt"; deadlineSeconds = 600; elapsedSeconds = 1; exitCode = 0; completionMarkerCount = 1; status = "passed"; watchdogLogSha256 = (Get-FileHash -LiteralPath (Join-Path $receiptRoot "watchdog.log") -Algorithm SHA256).Hash.ToLowerInvariant(); componentEvidenceSha256 = ""; componentManifestSha256 = "" })
    Update-TestComponentAuth -Root $Root
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-promotion-fan-in-" + [Guid]::NewGuid().ToString("N"))
$solutionRoot = Join-Path $fixtureRoot "solution"
$nestedRoot = Join-Path $fixtureRoot "nested"
$staticRoot = Join-Path $fixtureRoot "static"
$macOSRoot = Join-Path $fixtureRoot "macos"
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
try {
    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    New-TestComponent -Root $staticRoot -Component "static-contracts"
    New-TestMacOSPlatformComponent -Root $macOSRoot
    $macOSSelection = @(Get-FanInMacOSPlatformContractSelection)
    foreach ($selectionCase in @(
        [pscustomobject]@{ Name = "wrong project"; Mutate = { param($items) $items[0].Project = "tests/Wrong/Wrong.csproj" } },
        [pscustomobject]@{ Name = "missing project fact"; Mutate = { param($items) $items[14].Project = $items[0].Project } },
        [pscustomobject]@{ Name = "duplicate fact"; Mutate = { param($items) $items[1].Fact = $items[0].Fact } },
        [pscustomobject]@{ Name = "substituted fact"; Mutate = { param($items) $items[0].Fact = "EmbodySense.Cli.Command.Tests.ConsoleAgentRuntimeHostTests.NotSelected" } },
        [pscustomobject]@{ Name = "one-backslash project"; Mutate = { param($items) $items[0].Project = $items[0].Project.Replace('/', '\') } }
    )) {
        $candidate = @(Copy-TestMacOSPlatformSelection -Selection $macOSSelection)
        & $selectionCase.Mutate $candidate
        Assert-Throws -Message "macOS selection $($selectionCase.Name)" -ExpectedMessage "MacOSPlatformContract" -Action { Assert-MacOSPlatformContractSelection -Selection $candidate }
    }
    . (Join-Path $repoRoot "scripts\verify-macos-platform-contract.ps1") -NoRun
    $controlledDiscoveryAssembly = @(Get-MacOSPlatformContractAssemblies -Selection $macOSSelection)[0]
    $controlledDiscoveryRoot = Join-Path $fixtureRoot "controlled-discovery"
    New-Item -ItemType Directory -Path $controlledDiscoveryRoot -Force | Out-Null
    function Invoke-MacOSPlatformContractPhase {
        param([string]$Name, [string]$FileName, [string[]]$Arguments, [Diagnostics.Stopwatch]$Stopwatch)
        $outputPath = $Arguments[([Array]::IndexOf($Arguments, "-OutputPath") + 1)]
        $filter = $Arguments[([Array]::IndexOf($Arguments, "-Filter") + 1)]
        $tests = @($controlledDiscoveryAssembly.Group | ForEach-Object { [ordered]@{ fullyQualifiedName = $_.Fact; id = "20000000-0000-0000-0000-$([Array]::IndexOf($controlledDiscoveryAssembly.Group, $_).ToString('000000000000'))"; xunitTestCaseUniqueId = "controlled-$($_.Fact)" } })
        Write-TestJson -Path $outputPath -Value ([ordered]@{ schemaVersion = 1; source = [IO.Path]::GetFullPath($controlledDiscoveryAssembly.TestAssemblyPath); filter = $filter; totalTests = $tests.Count; tests = $tests })
        Write-Output "VERIFY_PHASE_START name=$Name"
        Write-Output "VERIFY_PHASE_COMPLETE name=$Name"
    }
    $controlledDiscoveryInformationPath = Join-Path $controlledDiscoveryRoot "phase-information.log"
    $controlledDiscoveryStopwatch = [Diagnostics.Stopwatch]::StartNew()
    $controlledDiscoveryRecords = @(Invoke-MacOSPlatformContractDiscovery -Assembly $controlledDiscoveryAssembly -Stopwatch $controlledDiscoveryStopwatch -DiscoveryRoot $controlledDiscoveryRoot 6> $controlledDiscoveryInformationPath)
    $controlledDiscoveryInformation = Get-Content -LiteralPath $controlledDiscoveryInformationPath -Raw
    Assert-True -Condition ($controlledDiscoveryRecords.Count -eq $controlledDiscoveryAssembly.Group.Count -and @($controlledDiscoveryRecords | Where-Object { $_ -is [pscustomobject] }).Count -eq $controlledDiscoveryAssembly.Group.Count) -Message "Discovery must return only shaped provenance records."
    Assert-True -Condition ($controlledDiscoveryInformation.Contains("VERIFY_PHASE_START name=discover-macos-$($controlledDiscoveryAssembly.Assembly)") -and $controlledDiscoveryInformation.Contains("VERIFY_PHASE_COMPLETE name=discover-macos-$($controlledDiscoveryAssembly.Assembly)")) -Message "Discovery phase markers must remain visible outside the record return stream."
    $productionNestedLane = Get-Content -LiteralPath (Join-Path $nestedRoot "VerificationResults/required-test-lanes.json") -Raw | ConvertFrom-Json
    $productionNestedCoverage = Get-Content -LiteralPath (Join-Path $nestedRoot "VerificationResults/coverage-manifest.json") -Raw | ConvertFrom-Json
    Assert-True -Condition ($productionNestedLane.lanes[0].name -ceq "EmbodySense.Core.Startup.Tests-nested-process") -Message "Inventory lane identity must match the canonical verifier producer."
    Assert-True -Condition ($productionNestedCoverage.reports[0].laneName -ceq "tests-EmbodySense.Core.Startup.Tests-nested-process") -Message "Coverage phase identity must retain the canonical tests prefix."
    $productionSolutionLanes = Get-Content -LiteralPath (Join-Path $solutionRoot "VerificationResults/required-test-lanes.json") -Raw | ConvertFrom-Json
    $ordinaryLane = @($productionSolutionLanes.lanes | Where-Object { $_.projectName -ceq "EmbodySense.Cli.Command.Tests" })
    $browserLane = @($productionSolutionLanes.lanes | Where-Object { $_.projectName -ceq "EmbodySense.E2ETests" })
    Assert-True -Condition ($ordinaryLane.Count -eq 1 -and $ordinaryLane[0].filter -ceq "(VerificationTier!=Stress)") -Message "Empty additional exclusions must preserve the canonical ordinary-project filter."
    Assert-True -Condition ($browserLane.Count -eq 1 -and $browserLane[0].filter -ceq "(FullyQualifiedName!~BrowserFlowTests)&(VerificationTier!=Stress)") -Message "The required inventory must retain the canonical BrowserFlowTests exclusion."
    $output = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot
    $outputText = [string]::Join("`n", @($output | ForEach-Object { [string]$_ }))
    Assert-True -Condition $outputText.Contains("lanes=10 projects=9") -Message "The successful fan-in did not prove the ten-lane nine-project aggregate."
    Assert-True -Condition $outputText.Contains("macos=macos-platform-contract") -Message "The successful fan-in did not authenticate the required macOS component."
    Assert-Throws -Message "skipped macOS child" -ExpectedMessage "All four hosted verification children must succeed" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot -MacOSResult "skipped" }
    $macOSResultPath = Join-Path $macOSRoot "VerificationResults/MacOSPlatformContract/macos-platform-contract-results.json"
    $macOSResultMap = Get-Content -LiteralPath $macOSResultPath -Raw | ConvertFrom-Json
    $macOSResultMap.facts[0].outcome = "NotExecuted"
    Write-TestJson -Path $macOSResultPath -Value $macOSResultMap
    Update-TestComponentAuth -Root $macOSRoot
    Assert-Throws -Message "non-passing macOS result" -ExpectedMessage "authoritative raw TRX reconciliation" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }
    New-TestMacOSPlatformComponent -Root $macOSRoot

    foreach ($discoveryCase in @(
        [pscustomobject]@{ Name = "empty fact"; Mutate = { param($map) $map.discoveries[0].fact = "" } },
        [pscustomobject]@{ Name = "duplicate fact"; Mutate = { param($map) $map.discoveries[1].fact = $map.discoveries[0].fact } },
        [pscustomobject]@{ Name = "empty test ID"; Mutate = { param($map) $map.discoveries[0].testId = "" } },
        [pscustomobject]@{ Name = "duplicate test ID"; Mutate = { param($map) $map.discoveries[1].testId = $map.discoveries[0].testId } },
        [pscustomobject]@{ Name = "empty xUnit ID"; Mutate = { param($map) $map.discoveries[0].xunitTestCaseUniqueId = "" } },
        [pscustomobject]@{ Name = "duplicate xUnit ID"; Mutate = { param($map) $map.discoveries[1].xunitTestCaseUniqueId = $map.discoveries[0].xunitTestCaseUniqueId } },
        [pscustomobject]@{ Name = "wrong project"; Mutate = { param($map) $map.discoveries[0].project = "tests/Wrong/Wrong.csproj" } },
        [pscustomobject]@{ Name = "wrong assembly"; Mutate = { param($map) $map.discoveries[0].assembly = "WrongAssembly" } },
        [pscustomobject]@{ Name = "rooted test assembly"; Mutate = { param($map) $map.discoveries[0].testAssembly = "/foreign/tests/Wrong.dll" } },
        [pscustomobject]@{ Name = "dotdot test assembly"; Mutate = { param($map) $map.discoveries[0].testAssembly = "tests/../Wrong.dll" } }
    )) {
        New-TestMacOSPlatformComponent -Root $macOSRoot
        $discoveryMap = Get-Content -LiteralPath $macOSResultPath -Raw | ConvertFrom-Json
        & $discoveryCase.Mutate $discoveryMap
        Write-TestJson -Path $macOSResultPath -Value $discoveryMap
        Update-TestComponentAuth -Root $macOSRoot
        Assert-Throws -Message "macOS discovery $($discoveryCase.Name)" -ExpectedMessage "discovery" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }
    }
    New-TestMacOSPlatformComponent -Root $macOSRoot

    $assemblyPathPattern = '(storage|codeBase)="[^"]*(tests/[^"/]+(?:/[^"/]+)*/bin/Release/net10\.0/[^"/]+\.dll)"'
    $foreignChangeCount = 0
    foreach ($foreignTrx in @(Get-ChildItem -LiteralPath (Join-Path $macOSRoot "VerificationResults/MacOSPlatformContract") -Recurse -Filter "*.trx" -File)) {
        $foreignOriginal = Get-Content -LiteralPath $foreignTrx.FullName -Raw
        $foreignText = [regex]::Replace($foreignOriginal, $assemblyPathPattern, '$1="/foreign/producer/$2"')
        Assert-True -Condition ($foreignText -cne $foreignOriginal) -Message "Foreign producer path substitution must change storage or codeBase evidence."
        $foreignChangeCount++
        [IO.File]::WriteAllText($foreignTrx.FullName, $foreignText, [Text.UTF8Encoding]::new($false))
    }
    Assert-True -Condition ($foreignChangeCount -gt 0) -Message "Foreign producer fixture must contain at least one storage or codeBase assembly path."
    Update-TestComponentAuth -Root $macOSRoot
    $foreignBindingOutput = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot
    Assert-True -Condition ([string]::Join([Environment]::NewLine, @($foreignBindingOutput)).Contains("macos=macos-platform-contract")) -Message "Foreign macOS producer paths must bind lexically to the selected assembly suffix."
    New-TestMacOSPlatformComponent -Root $macOSRoot

    foreach ($rawCase in @(
        [pscustomobject]@{ Name = "failed raw result"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('outcome="Passed"', 'outcome="Failed"'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "not-executed raw result"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('outcome="Passed"', 'outcome="NotExecuted"'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "wrong namespace"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('http://microsoft.com/schemas/VisualStudio/TeamTest/2010', 'urn:wrong'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "DTD"; Mutate = { param($path) [IO.File]::WriteAllText($path, '<!DOCTYPE TestRun [<!ENTITY xxe "blocked">]>' + (Get-Content -LiteralPath $path -Raw), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "malformed XML"; Mutate = { param($path) [IO.File]::WriteAllText($path, '<TestRun', [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "oversized XML"; Mutate = { param($path) [IO.File]::AppendAllText($path, ('x' * 1048577), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "wrong storage and codeBase"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('.dll', '-wrong.dll'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "dot foreign producer path"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $updated = [regex]::Replace($original, $assemblyPathPattern, '$1="/foreign/./producer/$2"'); Assert-True -Condition ($updated -cne $original) -Message "Dot-path substitution must change storage or codeBase evidence."; [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "dotdot foreign producer path"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $updated = [regex]::Replace($original, $assemblyPathPattern, '$1="/foreign/../producer/$2"'); Assert-True -Condition ($updated -cne $original) -Message "Dotdot-path substitution must change storage or codeBase evidence."; [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "empty result identifier"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $pattern = [regex]::new('(<UnitTestResult [^>]*executionId=")[^"]+'); [IO.File]::WriteAllText($path, $pattern.Replace($original, '$1', 1), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "malformed result identifier"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $pattern = [regex]::new('(<UnitTestResult [^>]*testId=")[^"]+'); [IO.File]::WriteAllText($path, $pattern.Replace($original, '${1}not-a-guid', 1), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "empty GUID"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $pattern = [regex]::new('(<UnitTestResult [^>]*testId=")[^"]+'); [IO.File]::WriteAllText($path, $pattern.Replace($original, '${1}00000000-0000-0000-0000-000000000000', 1), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "empty results"; Mutate = { param($path) [IO.File]::WriteAllText($path, [regex]::Replace((Get-Content -LiteralPath $path -Raw), '<Results>.*?</Results>', '<Results></Results>', [Text.RegularExpressions.RegexOptions]::Singleline), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "missing result"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $pattern = [regex]::new('<UnitTestResult [^>]*/>'); [IO.File]::WriteAllText($path, $pattern.Replace($original, '', 1), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "duplicate result"; Mutate = { param($path) $text = Get-Content -LiteralPath $path -Raw; $match = [regex]::Match($text, '<UnitTestResult [^>]*/>'); [IO.File]::WriteAllText($path, $text.Replace($match.Value, $match.Value + $match.Value), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "unexpected result child"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('<Results>', '<Results><UnexpectedResult />'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "unexpected definition child"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('<TestDefinitions>', '<TestDefinitions><UnexpectedDefinition />'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "unexpected entry child"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('<TestEntries>', '<TestEntries><UnexpectedEntry />'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "wrong class provenance"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $pattern = [regex]::new('(className=")[^"]+'); [IO.File]::WriteAllText($path, $pattern.Replace($original, '${1}Wrong.Class', 1), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "wrong method provenance"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $pattern = [regex]::new('(<TestMethod\b[^>]*\bname=")[^"]+'); $updated = $pattern.Replace($original, '${1}WrongMethod', 1); Assert-True -Condition ($updated -cne $original -and @([regex]::Matches($updated, '<TestMethod\b[^>]*\bname="WrongMethod"')).Count -eq 1) -Message "Method provenance mutation must replace exactly one TestMethod name."; [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "missing definition"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $pattern = [regex]::new('<UnitTest .*?</UnitTest>'); [IO.File]::WriteAllText($path, $pattern.Replace($original, '', 1), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "counter mismatch"; Mutate = { param($path) $original = Get-Content -LiteralPath $path -Raw; $pattern = [regex]::new('(<Counters\b[^>]*\bpassed=")(\d+)'); $matches = @($pattern.Matches($original)); Assert-True -Condition ($matches.Count -eq 1) -Message "Counter mismatch mutation requires one Counters passed attribute."; $evaluator = [Text.RegularExpressions.MatchEvaluator]{ param($match) $match.Groups[1].Value + ([int]$match.Groups[2].Value + 1).ToString([Globalization.CultureInfo]::InvariantCulture) }; $updated = $pattern.Replace($original, $evaluator, 1); Assert-True -Condition ($updated -cne $original) -Message "Counter mismatch mutation must replace exactly one Counters passed value."; [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "nonzero completed counter"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('completed="0"', 'completed="1"'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "aborted summary"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('ResultSummary outcome="Completed"', 'ResultSummary outcome="Aborted"'), [Text.UTF8Encoding]::new($false)) } },
        [pscustomobject]@{ Name = "nonzero failure counter"; Mutate = { param($path) [IO.File]::WriteAllText($path, (Get-Content -LiteralPath $path -Raw).Replace('failed="0"', 'failed="1"'), [Text.UTF8Encoding]::new($false)) } }
    )) {
        New-TestMacOSPlatformComponent -Root $macOSRoot
        $rawTrx = Get-ChildItem -LiteralPath (Join-Path $macOSRoot "VerificationResults/MacOSPlatformContract") -Recurse -Filter "*.trx" -File | Select-Object -First 1
        & $rawCase.Mutate $rawTrx.FullName
        Update-TestComponentAuth -Root $macOSRoot
        Assert-Throws -Message "macOS raw TRX $($rawCase.Name)" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }
    }
    New-TestMacOSPlatformComponent -Root $macOSRoot
    $duplicateDefinitionTrx = Get-ChildItem -LiteralPath (Join-Path $macOSRoot "VerificationResults/MacOSPlatformContract") -Recurse -Filter "*.trx" -File | Select-Object -First 1
    $duplicateDefinitionText = Get-Content -LiteralPath $duplicateDefinitionTrx.FullName -Raw
    $definitionMatch = [regex]::Match($duplicateDefinitionText, '<UnitTest .*?</UnitTest>')
    [IO.File]::WriteAllText($duplicateDefinitionTrx.FullName, $duplicateDefinitionText.Replace($definitionMatch.Value, $definitionMatch.Value + $definitionMatch.Value), [Text.UTF8Encoding]::new($false))
    Update-TestComponentAuth -Root $macOSRoot
    Assert-Throws -Message "macOS duplicate definition" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }

    New-TestMacOSPlatformComponent -Root $macOSRoot
    $crossedIdentifierTrx = Get-ChildItem -LiteralPath (Join-Path $macOSRoot "VerificationResults/MacOSPlatformContract") -Recurse -Filter "*.trx" -File | Select-Object -First 1
    $crossedIdentifierPattern = [regex]::new('(<UnitTestResult [^>]*executionId=")[^"]+')
    $crossedIdentifierText = $crossedIdentifierPattern.Replace((Get-Content -LiteralPath $crossedIdentifierTrx.FullName -Raw), '${1}ffffffff-ffff-ffff-ffff-ffffffffffff', 1)
    [IO.File]::WriteAllText($crossedIdentifierTrx.FullName, $crossedIdentifierText, [Text.UTF8Encoding]::new($false))
    Update-TestComponentAuth -Root $macOSRoot
    Assert-Throws -Message "macOS crossed execution identifier" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }

    New-TestMacOSPlatformComponent -Root $macOSRoot
    $duplicateExecutionTrxs = @(Get-ChildItem -LiteralPath (Join-Path $macOSRoot "VerificationResults/MacOSPlatformContract") -Recurse -Filter "*.trx" -File | Sort-Object FullName)
    $firstExecution = [regex]::Match((Get-Content -LiteralPath $duplicateExecutionTrxs[0].FullName -Raw), 'executionId="([^"]+)"').Groups[1].Value
    $secondExecution = [regex]::Match((Get-Content -LiteralPath $duplicateExecutionTrxs[1].FullName -Raw), 'executionId="([^"]+)"').Groups[1].Value
    Assert-True -Condition ($firstExecution -cne $secondExecution) -Message "Cross-assembly duplicate execution fixture requires distinct source execution IDs."
    $secondExecutionOriginal = Get-Content -LiteralPath $duplicateExecutionTrxs[1].FullName -Raw
    $secondExecutionText = $secondExecutionOriginal.Replace($secondExecution, $firstExecution)
    Assert-True -Condition ($secondExecutionText -cne $secondExecutionOriginal) -Message "Cross-assembly duplicate execution fixture must alter the second TRX."
    [IO.File]::WriteAllText($duplicateExecutionTrxs[1].FullName, $secondExecutionText, [Text.UTF8Encoding]::new($false))
    Update-TestComponentAuth -Root $macOSRoot
    Assert-Throws -Message "macOS duplicate execution across assemblies" -ExpectedMessage "duplicate test execution identifiers" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }

    New-TestMacOSPlatformComponent -Root $macOSRoot
    $macOSResultMap = Get-Content -LiteralPath $macOSResultPath -Raw | ConvertFrom-Json
    $macOSResultMap.facts[0].testId = "ffffffff-ffff-ffff-ffff-ffffffffffff"
    Write-TestJson -Path $macOSResultPath -Value $macOSResultMap
    Update-TestComponentAuth -Root $macOSRoot
    Assert-Throws -Message "macOS JSON provenance tamper" -ExpectedMessage "authoritative raw TRX reconciliation" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }
    foreach ($phaseCase in @("missing", "duplicate", "failed")) {
        New-TestMacOSPlatformComponent -Root $macOSRoot
        $watchdogPath = Join-Path $macOSRoot "VerificationResults/watchdog.log"
        $watchdogText = Get-Content -LiteralPath $watchdogPath -Raw
        if ($phaseCase -eq "missing") { $watchdogText = $watchdogText.Replace('VERIFY_PHASE_COMPLETE name=build-macos-platform-contract elapsed_seconds=1 completed_at_utc=2026-01-01T00:00:00.0000000+00:00' + "`n", "") }
        elseif ($phaseCase -eq "duplicate") { $watchdogText = 'VERIFY_PHASE_COMPLETE name=build-macos-platform-contract elapsed_seconds=1 completed_at_utc=2026-01-01T00:00:00.0000000+00:00' + "`n" + $watchdogText }
        else { $watchdogText = 'VERIFY_PHASE_FAILED name=build-macos-platform-contract' + "`n" + $watchdogText }
        [IO.File]::WriteAllText($watchdogPath, $watchdogText, [Text.UTF8Encoding]::new($false))
        Update-TestComponentAuth -Root $macOSRoot
        Assert-Throws -Message "macOS $phaseCase phase evidence" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }
    }
    New-TestMacOSPlatformComponent -Root $macOSRoot

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
    $windowsOutput = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot
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
    Assert-Throws -Message "uncovered generated source retains coverage denominator" -ExpectedMessage "must be greater than the unchanged 90% floor" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    $coverageBoundaryPackage = "EmbodySense.Core.Common"
    Set-TestCoveragePackageLines -Root $solutionRoot -PackageName $coverageBoundaryPackage -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 1, 0)
    Set-TestCoveragePackageLines -Root $nestedRoot -PackageName $coverageBoundaryPackage -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 1, 0)
    foreach ($coverageRoot in @($solutionRoot, $nestedRoot)) { Update-TestCoverageAuth -Root $coverageRoot; Update-TestComponentAuth -Root $coverageRoot }
    Assert-Throws -Message "exact-90 combined coverage" -ExpectedMessage "must be greater than the unchanged 90% floor" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    Set-TestCoveragePackageLines -Root $solutionRoot -PackageName $coverageBoundaryPackage -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 1, 1)
    Set-TestCoveragePackageLines -Root $nestedRoot -PackageName $coverageBoundaryPackage -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 1, 1)
    foreach ($coverageRoot in @($solutionRoot, $nestedRoot)) { Update-TestCoverageAuth -Root $coverageRoot; Update-TestComponentAuth -Root $coverageRoot }
    $fullCoverageOutput = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot
    Assert-True -Condition ([string]::Join("`n", @($fullCoverageOutput)).Contains("lanes=10 projects=9")) -Message "A combined 10/10 package must pass the strict fan-in coverage floor."

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    Set-TestCoveragePackageLines -Root $solutionRoot -PackageName $coverageBoundaryPackage -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 0, 0)
    Set-TestCoveragePackageLines -Root $nestedRoot -PackageName $coverageBoundaryPackage -Hits @(0, 0, 0, 0, 0, 0, 0, 0, 1, 1)
    foreach ($coverageRoot in @($solutionRoot, $nestedRoot)) { Update-TestCoverageAuth -Root $coverageRoot; Update-TestComponentAuth -Root $coverageRoot }
    $complementaryCoverageOutput = Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot
    Assert-True -Condition ([string]::Join("`n", @($complementaryCoverageOutput)).Contains("lanes=10 projects=9")) -Message "Complementary 8/10 child coverage must union to a passing 10/10 package."

    New-TestComponent -Root $solutionRoot -Component "solution"
    New-TestComponent -Root $nestedRoot -Component "nested-process"
    foreach ($coverageRoot in @($solutionRoot, $nestedRoot)) { Set-TestCoveragePackageLines -Root $coverageRoot -PackageName $coverageBoundaryPackage -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 0, 0); Update-TestCoverageAuth -Root $coverageRoot; Update-TestComponentAuth -Root $coverageRoot }
    Assert-Throws -Message "common-gap partial coverage" -ExpectedMessage "must be greater than the unchanged 90% floor" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot -MacOSRoot $macOSRoot }

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
    Assert-Throws -Message "combined coverage below floor" -ExpectedMessage "must be greater than the unchanged 90% floor" -Action { Invoke-TestFanIn -SolutionRoot $solutionRoot -NestedRoot $nestedRoot -StaticRoot $staticRoot }

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
