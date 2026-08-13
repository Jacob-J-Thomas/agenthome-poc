Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$partitionScriptPath = Join-Path $repoRoot "scripts\verify-test-partition.ps1"
$inventoryScriptPath = Join-Path $repoRoot "scripts\verify-test-inventory.ps1"
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:assertionCount++
}

function Assert-Contains {
    param([string]$Actual, [string]$Expected, [string]$Message)
    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'. Actual: $Actual"
}

$ordinalSetConstruction = '[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)'
$partitionScript = Get-Content -LiteralPath $partitionScriptPath -Raw
$inventoryScript = Get-Content -LiteralPath $inventoryScriptPath -Raw
Assert-True -Condition (([regex]::Matches($partitionScript, [regex]::Escape($ordinalSetConstruction))).Count -eq 2) -Message "Partition reconciliation must use two ordinal identity sets."
Assert-True -Condition (([regex]::Matches($inventoryScript, [regex]::Escape($ordinalSetConstruction))).Count -eq 2) -Message "Execution reconciliation must use two ordinal identity sets."
Assert-True -Condition ($partitionScript.IndexOf("-cnotin", [StringComparison]::Ordinal) -lt 0) -Message "Partition reconciliation must not regress to quadratic array membership."
Assert-True -Condition ($inventoryScript.IndexOf("-cnotin", [StringComparison]::Ordinal) -lt 0) -Message "Execution reconciliation must not regress to quadratic array membership."

. $phaseScriptPath

function Invoke-Script {
    param([string]$ScriptPath, [string[]]$Arguments)

    $childArguments = @("-NoProfile")
    if ($runningOnWindows) { $childArguments += @("-ExecutionPolicy", "Bypass") }
    $childArguments += @("-File", $ScriptPath) + $Arguments
    $startInfo = New-VerificationProcessStartInfo -FileName $powerShellExecutable -Arguments $childArguments
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Contract child process did not start." }
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            Stop-VerificationProcessTree $process
            throw "Contract child process exceeded its 30-second bound."
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $outputTask.GetAwaiter().GetResult() + $errorTask.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Write-DiscoveryInventory {
    param([string]$Path, [object[]]$Tests, [string]$Source = "fixture.dll")
    $value = [ordered]@{ schemaVersion = 1; source = $Source; filter = "VerificationTier!=Stress"; totalTests = $Tests.Count; tests = @($Tests) }
    [IO.File]::WriteAllText($Path, ($value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Write-LaneDefinitions {
    param([string]$Path, [object[]]$Lanes)
    [IO.File]::WriteAllText($Path, ([ordered]@{ schemaVersion = 1; lanes = @($Lanes) } | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function New-LaneDefinition {
    param([string]$Name, [string]$Filter)
    return [ordered]@{ name = $Name; projectName = "project"; filter = $Filter }
}

function New-DiscoveryTest {
    param([string]$Id, [string]$XunitId, [string]$Name)
    return [ordered]@{ id = $Id; xunitTestCaseUniqueId = $XunitId; fullyQualifiedName = "Suite.$Name"; displayName = "duplicate display"; executorUri = "executor://xunit/VsTestRunner3/netcore/"; source = "fixture.dll" }
}

function Write-ExecutionInventory {
    param([string]$Path, [object[]]$Tests)
    $value = [ordered]@{ schemaVersion = 1; totalTests = $Tests.Count; tests = @($Tests | ForEach-Object { [ordered]@{ id = $_.id; xunitTestCaseUniqueId = $_.xunitTestCaseUniqueId; fullyQualifiedName = $_.fullyQualifiedName; displayName = $_.displayName; source = $_.source; lane = $_.lane } }) }
    [IO.File]::WriteAllText($Path, ($value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Write-Trx {
    param([string]$Path, [object[]]$Results)

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try {
        $writer.WriteStartElement("TestRun", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        $writer.WriteStartElement("Results")
        foreach ($result in $Results) {
            $writer.WriteStartElement("UnitTestResult")
            $writer.WriteAttributeString("testId", [string]$result.TestId)
            $writer.WriteAttributeString("executionId", [string]$result.ExecutionId)
            $writer.WriteAttributeString("testName", [string]$result.Name)
            $writer.WriteAttributeString("outcome", [string]$result.Outcome)
            $writer.WriteAttributeString("duration", [TimeSpan]::FromMilliseconds([double]$result.DurationMilliseconds).ToString("c"))
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    finally { $writer.Dispose() }
}

$scenarioRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-inventory-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
try {
    $idA = "11111111-1111-1111-1111-111111111111"
    $idB = "22222222-2222-2222-2222-222222222222"
    $testA = New-DiscoveryTest -Id $idA -XunitId "xunit-a" -Name "A"
    $testB = New-DiscoveryTest -Id $idB -XunitId "XUNIT-A" -Name "B"
    $canonicalRoot = Join-Path $scenarioRoot "canonical"
    New-Item -ItemType Directory -Path $canonicalRoot | Out-Null
    Write-DiscoveryInventory -Path (Join-Path $canonicalRoot "project.json") -Tests @($testA, $testB)
    $laneDefinitionsPath = Join-Path $scenarioRoot "lanes.json"
    $expectedPath = Join-Path $scenarioRoot "expected.json"
    $partitionReport = Join-Path $scenarioRoot "partition.json"
    $partitionArguments = @("-CanonicalInventoryRoot", $canonicalRoot, "-LaneDefinitionPath", $laneDefinitionsPath, "-ExpectedExecutionInventoryPath", $expectedPath, "-ReportPath", $partitionReport)

    Write-LaneDefinitions -Path $laneDefinitionsPath -Lanes @(
        (New-LaneDefinition -Name "lane-a" -Filter "((FullyQualifiedName~Suite.A))&(VerificationTier!=Stress)"),
        (New-LaneDefinition -Name "lane-b" -Filter "((FullyQualifiedName~Suite.B))&(VerificationTier!=Stress)"))
    $partition = Invoke-Script -ScriptPath $partitionScriptPath -Arguments $partitionArguments
    Assert-True -Condition ($partition.ExitCode -eq 0) -Message "An exhaustive disjoint declarative partition must pass. Actual: $($partition.Output)"
    Assert-Contains -Actual $partition.Output -Expected "VERIFY_TEST_PARTITION_COMPLETE canonical=2 execution=2 lanes=2" -Message "Partition counts must be explicit."
    $partitionInventory = Get-Content -LiteralPath $expectedPath -Raw | ConvertFrom-Json
    Assert-True -Condition (@($partitionInventory.tests | Where-Object { $_.lane -ceq "lane-a" -and $_.fullyQualifiedName -ceq "Suite.A" }).Count -eq 1) -Message "The declarative include predicate must bind Suite.A to lane-a."
    Assert-True -Condition (@($partitionInventory.tests | Where-Object { $_.xunitTestCaseUniqueId -ceq "xunit-a" }).Count -eq 1) -Message "Lower-case xUnit identities must remain distinct."
    Assert-True -Condition (@($partitionInventory.tests | Where-Object { $_.xunitTestCaseUniqueId -ceq "XUNIT-A" }).Count -eq 1) -Message "Upper-case xUnit identities must remain distinct under ordinal reconciliation."

    Write-LaneDefinitions -Path $laneDefinitionsPath -Lanes @(
        (New-LaneDefinition -Name "lane-a" -Filter "((FullyQualifiedName~suite.a))&(VerificationTier!=Stress)"),
        (New-LaneDefinition -Name "lane-b" -Filter "(FullyQualifiedName!~suite.a)&(VerificationTier!=Stress)"))
    $caseInsensitive = Invoke-Script -ScriptPath $partitionScriptPath -Arguments $partitionArguments
    Assert-True -Condition ($caseInsensitive.ExitCode -eq 0) -Message "Declarative name matching must preserve VSTest's case-insensitive contains semantics. Actual: $($caseInsensitive.Output)"
    $caseInsensitiveInventory = Get-Content -LiteralPath $expectedPath -Raw | ConvertFrom-Json
    Assert-True -Condition (@($caseInsensitiveInventory.tests | Where-Object { $_.lane -ceq "lane-a" -and $_.fullyQualifiedName -ceq "Suite.A" }).Count -eq 1) -Message "A differently cased include predicate must select the VSTest test case."
    Assert-True -Condition (@($caseInsensitiveInventory.tests | Where-Object { $_.lane -ceq "lane-b" -and $_.fullyQualifiedName -ceq "Suite.B" }).Count -eq 1) -Message "A differently cased exclusion predicate must leave only the complementary VSTest test case."

    Write-LaneDefinitions -Path $laneDefinitionsPath -Lanes @(
        (New-LaneDefinition -Name "lane-a" -Filter "((FullyQualifiedName~Suite))&(VerificationTier!=Stress)"),
        (New-LaneDefinition -Name "lane-b" -Filter "((FullyQualifiedName~Suite.B))&(VerificationTier!=Stress)"))
    $overlap = Invoke-Script -ScriptPath $partitionScriptPath -Arguments $partitionArguments
    Assert-True -Condition ($overlap.ExitCode -ne 0) -Message "A test selected by two declarative lanes must fail closed."
    Assert-Contains -Actual $overlap.Output -Expected "overlap=1" -Message "Overlap diagnostics must be exact."

    Write-LaneDefinitions -Path $laneDefinitionsPath -Lanes @((New-LaneDefinition -Name "lane-a" -Filter "((FullyQualifiedName~Suite.A))&(VerificationTier!=Stress)"))
    $omission = Invoke-Script -ScriptPath $partitionScriptPath -Arguments $partitionArguments
    Assert-True -Condition ($omission.ExitCode -ne 0) -Message "A test omitted by every declarative lane must fail closed."
    Assert-Contains -Actual $omission.Output -Expected "missing=1" -Message "Omission diagnostics must be exact."

    Write-LaneDefinitions -Path $laneDefinitionsPath -Lanes @(
        (New-LaneDefinition -Name "lane-a" -Filter "((FullyQualifiedName~Suite.A))&(VerificationTier!=Stress)"),
        (New-LaneDefinition -Name "lane-empty" -Filter "((FullyQualifiedName~Suite.Z))&(VerificationTier!=Stress)"))
    $emptyLane = Invoke-Script -ScriptPath $partitionScriptPath -Arguments $partitionArguments
    Assert-True -Condition ($emptyLane.ExitCode -ne 0) -Message "An empty declarative lane must fail closed."
    Assert-Contains -Actual $emptyLane.Output -Expected "empty_lanes=1" -Message "Empty-lane diagnostics must be actionable."

    Write-LaneDefinitions -Path $laneDefinitionsPath -Lanes @((New-LaneDefinition -Name "lane-a" -Filter "((DisplayName~A))&(VerificationTier!=Stress)"))
    $unsupported = Invoke-Script -ScriptPath $partitionScriptPath -Arguments $partitionArguments
    Assert-True -Condition ($unsupported.ExitCode -ne 0) -Message "A lane predicate outside the exact supported grammar must fail closed."
    Assert-Contains -Actual $unsupported.Output -Expected "contains no fully-qualified-name partition" -Message "Unsupported-predicate diagnostics must be actionable."

    Write-LaneDefinitions -Path $laneDefinitionsPath -Lanes @(
        (New-LaneDefinition -Name "lane-a" -Filter "((FullyQualifiedName~Suite.A)&(FullyQualifiedName~Never))&(VerificationTier!=Stress)"),
        (New-LaneDefinition -Name "lane-b" -Filter "((FullyQualifiedName~Suite.B))&(VerificationTier!=Stress)"))
    $hostileGrammar = Invoke-Script -ScriptPath $partitionScriptPath -Arguments $partitionArguments
    Assert-True -Condition ($hostileGrammar.ExitCode -ne 0) -Message "A filter that changes the generated OR group into an AND group must fail closed."
    Assert-Contains -Actual $hostileGrammar.Output -Expected "contains an unsupported predicate shape" -Message "Hostile operator placement diagnostics must be actionable."

    $executionTests = @(
        [ordered]@{ id = $testA.id; xunitTestCaseUniqueId = $testA.xunitTestCaseUniqueId; fullyQualifiedName = $testA.fullyQualifiedName; displayName = $testA.displayName; source = $testA.source; lane = "lane-a" },
        [ordered]@{ id = $testB.id; xunitTestCaseUniqueId = $testB.xunitTestCaseUniqueId; fullyQualifiedName = $testB.fullyQualifiedName; displayName = $testB.displayName; source = $testB.source; lane = "lane-b" })
    Write-ExecutionInventory -Path $expectedPath -Tests $executionTests
    $passingRoot = Join-Path $scenarioRoot "passing"
    New-Item -ItemType Directory -Path $passingRoot | Out-Null
    Write-Trx -Path (Join-Path $passingRoot "lane-a.trx") -Results @(
        [pscustomobject]@{ TestId = $testA.id; ExecutionId = "00000000-0000-0000-0000-000000000001"; Name = "dynamic row 1"; Outcome = "Passed"; DurationMilliseconds = 10 },
        [pscustomobject]@{ TestId = $testA.id; ExecutionId = "00000000-0000-0000-0000-000000000002"; Name = "dynamic row 2"; Outcome = "Passed"; DurationMilliseconds = 250 })
    Write-Trx -Path (Join-Path $passingRoot "lane-b.trx") -Results @([pscustomobject]@{ TestId = $testB.id; ExecutionId = "00000000-0000-0000-0000-000000000003"; Name = "duplicate display"; Outcome = "Passed"; DurationMilliseconds = 20 })
    $executionReport = Join-Path $scenarioRoot "execution.json"
    $passing = Invoke-Script -ScriptPath $inventoryScriptPath -Arguments @("-ExpectedInventoryPath", $expectedPath, "-ResultsRoot", $passingRoot, "-ReportPath", $executionReport)
    Assert-True -Condition ($passing.ExitCode -eq 0) -Message "One report per canonical ID with dynamic data rows must pass. Actual: $($passing.Output)"
    Assert-Contains -Actual $passing.Output -Expected "expected=2 executed_rows=3 unique_tests=2" -Message "Dynamic row multiplicity must remain visible."
    Assert-Contains -Actual $passing.Output -Expected "duration_milliseconds=250" -Message "Slowest-row diagnostics must be emitted."

    $duplicateExpectedPath = Join-Path $scenarioRoot "duplicate-expected.json"
    Write-ExecutionInventory -Path $duplicateExpectedPath -Tests @($executionTests[0], $executionTests[0])
    $duplicateExpected = Invoke-Script -ScriptPath $inventoryScriptPath -Arguments @("-ExpectedInventoryPath", $duplicateExpectedPath, "-ResultsRoot", $passingRoot, "-ReportPath", (Join-Path $scenarioRoot "duplicate-expected-report.json"))
    Assert-True -Condition ($duplicateExpected.ExitCode -ne 0) -Message "Duplicate expected TestCase identities must fail closed."
    Assert-Contains -Actual $duplicateExpected.Output -Expected "contains duplicate TestCase IDs:" -Message "Duplicate expected-identity diagnostics must remain exact."

    $malformedExpectedPath = Join-Path $scenarioRoot "malformed-expected.json"
    $malformedExpected = [ordered]@{ schemaVersion = 1; totalTests = 2; tests = @($executionTests[0]) }
    [IO.File]::WriteAllText($malformedExpectedPath, ($malformedExpected | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $malformed = Invoke-Script -ScriptPath $inventoryScriptPath -Arguments @("-ExpectedInventoryPath", $malformedExpectedPath, "-ResultsRoot", $passingRoot, "-ReportPath", (Join-Path $scenarioRoot "malformed-expected-report.json"))
    Assert-True -Condition ($malformed.ExitCode -ne 0) -Message "A malformed expected inventory shape must fail closed."
    Assert-Contains -Actual $malformed.Output -Expected "Expected required-test inventory is empty or malformed" -Message "Malformed expected-inventory diagnostics must remain exact."

    $overlapResults = Join-Path $scenarioRoot "cross-report"
    New-Item -ItemType Directory -Path $overlapResults | Out-Null
    Write-Trx -Path (Join-Path $overlapResults "one.trx") -Results @([pscustomobject]@{ TestId = $testA.id; ExecutionId = "00000000-0000-0000-0000-000000000011"; Name = "A"; Outcome = "Passed"; DurationMilliseconds = 1 })
    Write-Trx -Path (Join-Path $overlapResults "two.trx") -Results @(
        [pscustomobject]@{ TestId = $testA.id; ExecutionId = "00000000-0000-0000-0000-000000000012"; Name = "A"; Outcome = "Passed"; DurationMilliseconds = 1 },
        [pscustomobject]@{ TestId = $testB.id; ExecutionId = "00000000-0000-0000-0000-000000000013"; Name = "B"; Outcome = "Passed"; DurationMilliseconds = 1 })
    $crossReport = Invoke-Script -ScriptPath $inventoryScriptPath -Arguments @("-ExpectedInventoryPath", $expectedPath, "-ResultsRoot", $overlapResults, "-ReportPath", (Join-Path $scenarioRoot "cross-report.json"))
    Assert-True -Condition ($crossReport.ExitCode -ne 0) -Message "The same canonical ID in two shard reports must fail closed."
    Assert-Contains -Actual $crossReport.Output -Expected "cross_report_overlap=1" -Message "Cross-report overlap diagnostics must be exact."

    foreach ($scenario in @(
        [pscustomobject]@{ Name = "missing"; Rows = @([pscustomobject]@{ TestId = $testA.id; ExecutionId = "00000000-0000-0000-0000-000000000021"; Name = "A"; Outcome = "Passed"; DurationMilliseconds = 1 }); Expected = "missing=1" },
        [pscustomobject]@{ Name = "unexpected"; Rows = @([pscustomobject]@{ TestId = $testA.id; ExecutionId = "00000000-0000-0000-0000-000000000022"; Name = "A"; Outcome = "Passed"; DurationMilliseconds = 1 }, [pscustomobject]@{ TestId = "cccccccc-cccc-cccc-cccc-cccccccccccc"; ExecutionId = "00000000-0000-0000-0000-000000000023"; Name = "C"; Outcome = "Passed"; DurationMilliseconds = 1 }); Expected = "unexpected=1" },
        [pscustomobject]@{ Name = "failed"; Rows = @([pscustomobject]@{ TestId = $testA.id; ExecutionId = "00000000-0000-0000-0000-000000000024"; Name = "A"; Outcome = "Failed"; DurationMilliseconds = 1 }, [pscustomobject]@{ TestId = $testB.id; ExecutionId = "00000000-0000-0000-0000-000000000025"; Name = "B"; Outcome = "Passed"; DurationMilliseconds = 1 }); Expected = "non_passing=1" },
        [pscustomobject]@{ Name = "duplicate-execution"; Rows = @([pscustomobject]@{ TestId = $testA.id; ExecutionId = "00000000-0000-0000-0000-000000000026"; Name = "A"; Outcome = "Passed"; DurationMilliseconds = 1 }, [pscustomobject]@{ TestId = $testB.id; ExecutionId = "00000000-0000-0000-0000-000000000026"; Name = "B"; Outcome = "Passed"; DurationMilliseconds = 1 }); Expected = "duplicate_execution_ids=1" })) {
        $root = Join-Path $scenarioRoot $scenario.Name
        New-Item -ItemType Directory -Path $root | Out-Null
        Write-Trx -Path (Join-Path $root "result.trx") -Results $scenario.Rows
        $result = Invoke-Script -ScriptPath $inventoryScriptPath -Arguments @("-ExpectedInventoryPath", $expectedPath, "-ResultsRoot", $root, "-ReportPath", (Join-Path $scenarioRoot "$($scenario.Name).json"))
        Assert-True -Condition ($result.ExitCode -ne 0) -Message "Inventory scenario '$($scenario.Name)' must fail closed."
        Assert-Contains -Actual $result.Output -Expected $scenario.Expected -Message "Inventory scenario '$($scenario.Name)' must identify its root cause."
    }

    $emptyRoot = Join-Path $scenarioRoot "empty"
    New-Item -ItemType Directory -Path $emptyRoot | Out-Null
    $empty = Invoke-Script -ScriptPath $inventoryScriptPath -Arguments @("-ExpectedInventoryPath", $expectedPath, "-ResultsRoot", $emptyRoot, "-ReportPath", (Join-Path $scenarioRoot "empty.json"))
    Assert-True -Condition ($empty.ExitCode -ne 0) -Message "Missing TRX output must fail closed."
    Assert-Contains -Actual $empty.Output -Expected "No TRX reports were found" -Message "Missing-result diagnostics must be actionable."

    $corruptRoot = Join-Path $scenarioRoot "corrupt"
    New-Item -ItemType Directory -Path $corruptRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $corruptRoot "corrupt.trx") -Value "<not-trx" -Encoding UTF8
    $corrupt = Invoke-Script -ScriptPath $inventoryScriptPath -Arguments @("-ExpectedInventoryPath", $expectedPath, "-ResultsRoot", $corruptRoot, "-ReportPath", (Join-Path $scenarioRoot "corrupt.json"))
    Assert-True -Condition ($corrupt.ExitCode -ne 0) -Message "Corrupt TRX output must fail closed."
    Assert-Contains -Actual $corrupt.Output -Expected "TRX report is corrupt" -Message "Corrupt-result diagnostics must be actionable."
}
finally {
    if (Test-Path -LiteralPath $scenarioRoot) { Remove-Item -LiteralPath $scenarioRoot -Recurse -Force }
}

Write-Output "Test-inventory verifier contract tests passed ($assertionCount assertions)."
