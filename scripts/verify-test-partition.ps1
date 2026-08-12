param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CanonicalInventoryRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$LaneInventoryRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedExecutionInventoryPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-InventorySet {
    param([string]$Root, [string]$Description)

    $fullRoot = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "$Description root is missing: $fullRoot"
    }

    $files = @(Get-ChildItem -LiteralPath $fullRoot -Filter "*.json" -File | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "$Description root contains no inventory files: $fullRoot"
    }

    $inventories = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        try {
            $inventory = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        }
        catch {
            throw "$Description inventory is corrupt: $($file.FullName). $($_.Exception.Message)"
        }

        if ($inventory.schemaVersion -ne 1 -or [int]$inventory.totalTests -le 0 -or @($inventory.tests).Count -ne [int]$inventory.totalTests) {
            throw "$Description inventory is empty or malformed: $($file.FullName)"
        }

        $inventories.Add([pscustomobject]@{ File = $file.FullName; Inventory = $inventory })
    }

    return @($inventories)
}

$canonicalInventories = Read-InventorySet -Root $CanonicalInventoryRoot -Description "Canonical required-test"
$laneInventories = Read-InventorySet -Root $LaneInventoryRoot -Description "Execution-lane"
$canonicalTests = @($canonicalInventories | ForEach-Object { $_.Inventory.tests })
$laneTests = @($laneInventories | ForEach-Object {
    $laneName = [IO.Path]::GetFileNameWithoutExtension($_.File)
    foreach ($test in $_.Inventory.tests) {
        [pscustomobject]@{
            Lane = $laneName
            Id = ([Guid][string]$test.id).ToString("D")
            XunitId = [string]$test.xunitTestCaseUniqueId
            FullyQualifiedName = [string]$test.fullyQualifiedName
            DisplayName = [string]$test.displayName
            Source = [string]$test.source
        }
    }
})

$canonicalGroups = @($canonicalTests | Group-Object -Property xunitTestCaseUniqueId -CaseSensitive)
$laneXunitGroups = @($laneTests | Group-Object -Property XunitId -CaseSensitive)
$laneIdGroups = @($laneTests | Group-Object -Property Id -CaseSensitive)
$duplicateCanonical = @($canonicalGroups | Where-Object { $_.Count -ne 1 })
$overlap = @($laneXunitGroups | Where-Object { $_.Count -ne 1 })
$duplicateExecutionIds = @($laneIdGroups | Where-Object { $_.Count -ne 1 })
$canonicalIds = @($canonicalGroups.Name | Sort-Object -CaseSensitive)
$laneXunitIds = @($laneXunitGroups.Name | Sort-Object -CaseSensitive)
$missing = @($canonicalIds | Where-Object { $_ -cnotin $laneXunitIds })
$unexpected = @($laneXunitIds | Where-Object { $_ -cnotin $canonicalIds })

$report = [ordered]@{
    schemaVersion = 1
    canonicalInventoryCount = $canonicalInventories.Count
    laneInventoryCount = $laneInventories.Count
    canonicalTestCount = $canonicalTests.Count
    laneTestCount = $laneTests.Count
    missing = @($missing)
    unexpected = @($unexpected)
    overlap = @($overlap | ForEach-Object { [ordered]@{ xunitTestCaseUniqueId = $_.Name; count = $_.Count; lanes = @($_.Group.Lane | Sort-Object -Unique) } })
    duplicateCanonical = @($duplicateCanonical | ForEach-Object { $_.Name })
    duplicateExecutionIds = @($duplicateExecutionIds | ForEach-Object { $_.Name })
}
$fullReportPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $fullReportPath) -Force | Out-Null
[IO.File]::WriteAllText($fullReportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

if ($duplicateCanonical.Count -gt 0 -or $overlap.Count -gt 0 -or $duplicateExecutionIds.Count -gt 0 -or $missing.Count -gt 0 -or $unexpected.Count -gt 0) {
    throw "Required-test partition reconciliation failed closed. canonical=$($canonicalTests.Count) lane=$($laneTests.Count) missing=$($missing.Count) unexpected=$($unexpected.Count) overlap=$($overlap.Count) duplicate_canonical=$($duplicateCanonical.Count) duplicate_execution_ids=$($duplicateExecutionIds.Count) report=$fullReportPath"
}

$executionInventory = [ordered]@{
    schemaVersion = 1
    totalTests = $laneTests.Count
    tests = @($laneTests | Sort-Object -Property Id | ForEach-Object {
        [ordered]@{
            id = $_.Id
            xunitTestCaseUniqueId = $_.XunitId
            fullyQualifiedName = $_.FullyQualifiedName
            displayName = $_.DisplayName
            source = $_.Source
            lane = $_.Lane
        }
    })
}
$fullExpectedPath = [IO.Path]::GetFullPath($ExpectedExecutionInventoryPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $fullExpectedPath) -Force | Out-Null
[IO.File]::WriteAllText($fullExpectedPath, ($executionInventory | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output "VERIFY_TEST_PARTITION_COMPLETE canonical=$($canonicalTests.Count) execution=$($laneTests.Count) lanes=$($laneInventories.Count) inventory=$fullExpectedPath report=$fullReportPath"
