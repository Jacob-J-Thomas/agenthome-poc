param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedInventoryPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ResultsRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReportPath,

    [ValidateRange(1, 100)]
    [int]$SlowestTestCount = 25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-ExpectedInventory {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Expected required-test inventory is missing: $fullPath"
    }

    try {
        $inventory = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Expected required-test inventory is corrupt: $fullPath. $($_.Exception.Message)"
    }

    $tests = @($inventory.tests)
    if ($inventory.schemaVersion -ne 1 -or [int]$inventory.totalTests -le 0 -or $tests.Count -ne [int]$inventory.totalTests) {
        throw "Expected required-test inventory is empty or malformed: $fullPath"
    }

    $normalized = @($tests | ForEach-Object {
        [pscustomobject]@{
            Id = ([Guid][string]$_.id).ToString("D")
            FullyQualifiedName = [string]$_.fullyQualifiedName
            DisplayName = [string]$_.displayName
            Lane = [string]$_.lane
        }
    })
    $duplicates = @($normalized | Group-Object -Property Id -CaseSensitive | Where-Object { $_.Count -ne 1 })
    if ($duplicates.Count -gt 0) {
        throw "Expected required-test inventory contains duplicate TestCase IDs: $($duplicates.Name -join ', ')"
    }

    return $normalized
}

function Read-TestResults {
    param([string]$Root)

    $fullRoot = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "Verification result root is missing: $fullRoot"
    }

    $trxFiles = @(Get-ChildItem -LiteralPath $fullRoot -Recurse -Filter "*.trx" -File | Sort-Object FullName)
    if ($trxFiles.Count -eq 0) {
        throw "No TRX reports were found under verification result root: $fullRoot"
    }

    $results = [Collections.Generic.List[object]]::new()
    foreach ($trxFile in $trxFiles) {
        try {
            [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Raw
        }
        catch {
            throw "TRX report is corrupt: $($trxFile.FullName). $($_.Exception.Message)"
        }

        $namespace = [Xml.XmlNamespaceManager]::new($trx.NameTable)
        $namespace.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        $nodes = @($trx.SelectNodes("/t:TestRun/t:Results/t:UnitTestResult", $namespace))
        if ($nodes.Count -eq 0) {
            throw "TRX report contains no executed tests: $($trxFile.FullName)"
        }

        foreach ($node in $nodes) {
            $testId = [Guid]::Empty
            if (-not [Guid]::TryParse([string]$node.testId, [ref]$testId)) {
                throw "TRX report contains a result without a valid TestCase ID: $($trxFile.FullName)"
            }

            $executionId = [Guid]::Empty
            if (-not [Guid]::TryParse([string]$node.executionId, [ref]$executionId)) {
                throw "TRX report contains a result without a valid execution ID: $($trxFile.FullName)"
            }

            $duration = [TimeSpan]::Zero
            if (-not [TimeSpan]::TryParse([string]$node.duration, [Globalization.CultureInfo]::InvariantCulture, [ref]$duration)) {
                throw "TRX report contains an invalid test duration for '$($node.testName)': $($trxFile.FullName)"
            }

            $results.Add([pscustomobject]@{
                Id = $testId.ToString("D")
                ExecutionId = $executionId.ToString("D")
                Name = [string]$node.testName
                Outcome = [string]$node.outcome
                DurationMilliseconds = [Math]::Round($duration.TotalMilliseconds, 3)
                Report = $trxFile.FullName
            })
        }
    }

    return @($results)
}

$expected = @(Read-ExpectedInventory -Path $ExpectedInventoryPath)
$executed = @(Read-TestResults -Root $ResultsRoot)
$executedGroups = @($executed | Group-Object -Property Id -CaseSensitive)
$crossReportOverlap = @($executedGroups | Where-Object { @($_.Group.Report | Sort-Object -Unique).Count -ne 1 } | Sort-Object Name)
$executionIdDuplicates = @($executed | Group-Object -Property ExecutionId -CaseSensitive | Where-Object { $_.Count -ne 1 } | Sort-Object Name)
$executedIds = @($executedGroups.Name | Sort-Object -CaseSensitive)
$expectedIds = @($expected.Id | Sort-Object -CaseSensitive)
$expectedIdSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$executedIdSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($id in $expectedIds) { [void]$expectedIdSet.Add($id) }
foreach ($id in $executedIds) { [void]$executedIdSet.Add($id) }
$missing = @($expectedIds | Where-Object { -not $executedIdSet.Contains($_) })
$unexpected = @($executedIds | Where-Object { -not $expectedIdSet.Contains($_) })
$nonPassing = @($executed | Where-Object { $_.Outcome -cne "Passed" } | Sort-Object Id)
$slowest = @($executed | Sort-Object -Property @{ Expression = "DurationMilliseconds"; Descending = $true }, @{ Expression = "Id"; Descending = $false } | Select-Object -First $SlowestTestCount)
$expectedById = @{}
foreach ($test in $expected) {
    $expectedById[$test.Id] = $test
}

$report = [ordered]@{
    schemaVersion = 1
    expectedCount = $expected.Count
    executedCount = $executed.Count
    uniqueExecutedCount = $executedIds.Count
    missing = @($missing)
    unexpected = @($unexpected)
    crossReportOverlap = @($crossReportOverlap | ForEach-Object { [ordered]@{ id = $_.Name; rowCount = $_.Count; reports = @($_.Group.Report | Sort-Object -Unique) } })
    duplicateExecutionId = @($executionIdDuplicates | ForEach-Object { [ordered]@{ executionId = $_.Name; count = $_.Count } })
    nonPassing = @($nonPassing | ForEach-Object { [ordered]@{ id = $_.Id; name = $_.Name; outcome = $_.Outcome; report = $_.Report } })
    slowest = @($slowest | ForEach-Object {
        $metadata = $expectedById[$_.Id]
        $fullyQualifiedName = if ($null -eq $metadata) { $_.Name } else { $metadata.FullyQualifiedName }
        $lane = if ($null -eq $metadata) { "unexpected" } else { $metadata.Lane }
        [ordered]@{ id = $_.Id; name = $_.Name; fullyQualifiedName = $fullyQualifiedName; lane = $lane; durationMilliseconds = $_.DurationMilliseconds; report = $_.Report }
    })
}

$fullReportPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $fullReportPath) -Force | Out-Null
[IO.File]::WriteAllText($fullReportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
foreach ($item in $slowest) {
    $metadata = $expectedById[$item.Id]
    $fullyQualifiedName = if ($null -eq $metadata) { $item.Name } else { $metadata.FullyQualifiedName }
    $lane = if ($null -eq $metadata) { "unexpected" } else { $metadata.Lane }
    Write-Output "VERIFY_SLOW_TEST duration_milliseconds=$($item.DurationMilliseconds) test_id=$($item.Id) lane=$lane name=$fullyQualifiedName report=$($item.Report)"
}

if ($missing.Count -gt 0 -or $unexpected.Count -gt 0 -or $crossReportOverlap.Count -gt 0 -or $executionIdDuplicates.Count -gt 0 -or $nonPassing.Count -gt 0) {
    throw "Required-test inventory reconciliation failed closed. expected=$($expected.Count) executed_rows=$($executed.Count) unique_tests=$($executedIds.Count) missing=$($missing.Count) unexpected=$($unexpected.Count) cross_report_overlap=$($crossReportOverlap.Count) duplicate_execution_ids=$($executionIdDuplicates.Count) non_passing=$($nonPassing.Count) report=$fullReportPath"
}

Write-Output "VERIFY_TEST_INVENTORY_COMPLETE expected=$($expected.Count) executed_rows=$($executed.Count) unique_tests=$($executedIds.Count) report=$fullReportPath"
