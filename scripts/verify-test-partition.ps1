param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$CanonicalInventoryRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$LaneDefinitionPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedExecutionInventoryPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-CanonicalInventorySet {
    param([string]$Root)

    $fullRoot = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "Canonical required-test root is missing: $fullRoot"
    }

    $files = @(Get-ChildItem -LiteralPath $fullRoot -Filter "*.json" -File | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "Canonical required-test root contains no inventory files: $fullRoot"
    }

    return @($files | ForEach-Object {
        try {
            $inventory = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        }
        catch {
            throw "Canonical required-test inventory is corrupt: $($_.FullName). $($_.Exception.Message)"
        }

        if ($inventory.schemaVersion -ne 1 -or [int]$inventory.totalTests -le 0 -or @($inventory.tests).Count -ne [int]$inventory.totalTests) {
            throw "Canonical required-test inventory is empty or malformed: $($_.FullName)"
        }

        [pscustomobject]@{
            ProjectName = [IO.Path]::GetFileNameWithoutExtension($_.Name)
            Inventory = $inventory
        }
    })
}

function Read-LaneDefinitions {
    param([string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required-test lane definition is missing: $fullPath"
    }

    try {
        $definition = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Required-test lane definition is corrupt: $fullPath. $($_.Exception.Message)"
    }

    $lanes = @($definition.lanes)
    if ($definition.schemaVersion -ne 1 -or $lanes.Count -eq 0) {
        throw "Required-test lane definition is empty or malformed: $fullPath"
    }

    $duplicates = @($lanes | Group-Object -Property name -CaseSensitive | Where-Object { $_.Count -ne 1 })
    if ($duplicates.Count -gt 0) {
        throw "Required-test lane definition contains duplicate lane names: $($duplicates.Name -join ', ')"
    }

    return $lanes
}

function Test-FullyQualifiedNameFilter {
    param([string]$FullyQualifiedName, [string]$Filter)

    $nonStressPredicate = "(VerificationTier!=Stress)"
    if ($Filter -ceq $nonStressPredicate) {
        return $true
    }

    $nonStressSuffix = "&$nonStressPredicate"
    if (-not $Filter.EndsWith($nonStressSuffix, [StringComparison]::Ordinal)) {
        throw "Required-test lane filter does not preserve the canonical non-stress predicate: $Filter"
    }

    $expression = $Filter.Substring(0, $Filter.Length - $nonStressSuffix.Length)
    $terms = @([regex]::Matches($expression, '\(FullyQualifiedName!?~([^()&|~=!]+)\)'))
    if ($terms.Count -eq 0) {
        throw "Required-test lane filter contains no fully-qualified-name partition predicate: $Filter"
    }

    $includeMatches = @($terms | Where-Object { -not $_.Value.StartsWith("(FullyQualifiedName!~", [StringComparison]::Ordinal) } | ForEach-Object { $_.Groups[1].Value })
    $excludeMatches = @($terms | Where-Object { $_.Value.StartsWith("(FullyQualifiedName!~", [StringComparison]::Ordinal) } | ForEach-Object { $_.Groups[1].Value })
    $canonicalParts = [Collections.Generic.List[string]]::new()
    if ($includeMatches.Count -gt 0) {
        $includeExpression = @($includeMatches | ForEach-Object { "(FullyQualifiedName~$_)" }) -join "|"
        $canonicalParts.Add("($includeExpression)")
    }
    foreach ($excludeMatch in $excludeMatches) {
        $canonicalParts.Add("(FullyQualifiedName!~$excludeMatch)")
    }
    if ($expression -cne ($canonicalParts -join "&")) {
        throw "Required-test lane filter contains an unsupported predicate shape: $Filter"
    }

    $included = $includeMatches.Count -eq 0 -or @($includeMatches | Where-Object { $FullyQualifiedName.Contains($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    $excluded = @($excludeMatches | Where-Object { $FullyQualifiedName.Contains($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    return $included -and -not $excluded
}

$canonicalInventories = Read-CanonicalInventorySet -Root $CanonicalInventoryRoot
$laneDefinitions = Read-LaneDefinitions -Path $LaneDefinitionPath
$canonicalTests = @($canonicalInventories | ForEach-Object {
    $projectName = $_.ProjectName
    foreach ($test in $_.Inventory.tests) {
        [pscustomobject]@{
            ProjectName = $projectName
            Id = ([Guid][string]$test.id).ToString("D")
            XunitId = [string]$test.xunitTestCaseUniqueId
            FullyQualifiedName = [string]$test.fullyQualifiedName
            DisplayName = [string]$test.displayName
            Source = [string]$test.source
        }
    }
})

$canonicalGroups = @($canonicalTests | Group-Object -Property XunitId -CaseSensitive)
$duplicateCanonical = @($canonicalGroups | Where-Object { $_.Count -ne 1 })
$laneTests = [Collections.Generic.List[object]]::new()
$emptyLanes = [Collections.Generic.List[string]]::new()
foreach ($lane in $laneDefinitions) {
    $projectTests = @($canonicalTests | Where-Object { $_.ProjectName -ceq [string]$lane.projectName })
    if ($projectTests.Count -eq 0) {
        throw "Required-test lane '$($lane.name)' references unknown or empty project '$($lane.projectName)'."
    }

    $selected = @($projectTests | Where-Object { Test-FullyQualifiedNameFilter -FullyQualifiedName $_.FullyQualifiedName -Filter ([string]$lane.filter) })
    if ($selected.Count -eq 0) {
        $emptyLanes.Add([string]$lane.name)
    }
    foreach ($test in $selected) {
        $laneTests.Add([pscustomobject]@{
            Lane = [string]$lane.name
            Id = $test.Id
            XunitId = $test.XunitId
            FullyQualifiedName = $test.FullyQualifiedName
            DisplayName = $test.DisplayName
            Source = $test.Source
        })
    }
}

$laneXunitGroups = @($laneTests | Group-Object -Property XunitId -CaseSensitive)
$laneIdGroups = @($laneTests | Group-Object -Property Id -CaseSensitive)
$overlap = @($laneXunitGroups | Where-Object { $_.Count -ne 1 })
$duplicateExecutionIds = @($laneIdGroups | Where-Object { $_.Count -ne 1 })
$canonicalIds = @($canonicalGroups.Name | Sort-Object -CaseSensitive)
$laneXunitIds = @($laneXunitGroups.Name | Sort-Object -CaseSensitive)
$missing = @($canonicalIds | Where-Object { $_ -cnotin $laneXunitIds })
$unexpected = @($laneXunitIds | Where-Object { $_ -cnotin $canonicalIds })

$report = [ordered]@{
    schemaVersion = 1
    canonicalInventoryCount = $canonicalInventories.Count
    laneDefinitionCount = $laneDefinitions.Count
    canonicalTestCount = $canonicalTests.Count
    laneTestCount = $laneTests.Count
    emptyLanes = @($emptyLanes)
    missing = @($missing)
    unexpected = @($unexpected)
    overlap = @($overlap | ForEach-Object { [ordered]@{ xunitTestCaseUniqueId = $_.Name; count = $_.Count; lanes = @($_.Group.Lane | Sort-Object -Unique) } })
    duplicateCanonical = @($duplicateCanonical | ForEach-Object { $_.Name })
    duplicateExecutionIds = @($duplicateExecutionIds | ForEach-Object { $_.Name })
}
$fullReportPath = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $fullReportPath) -Force | Out-Null
[IO.File]::WriteAllText($fullReportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

if ($duplicateCanonical.Count -gt 0 -or $emptyLanes.Count -gt 0 -or $overlap.Count -gt 0 -or $duplicateExecutionIds.Count -gt 0 -or $missing.Count -gt 0 -or $unexpected.Count -gt 0) {
    throw "Required-test partition reconciliation failed closed. canonical=$($canonicalTests.Count) lane=$($laneTests.Count) empty_lanes=$($emptyLanes.Count) missing=$($missing.Count) unexpected=$($unexpected.Count) overlap=$($overlap.Count) duplicate_canonical=$($duplicateCanonical.Count) duplicate_execution_ids=$($duplicateExecutionIds.Count) report=$fullReportPath"
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
Write-Output "VERIFY_TEST_PARTITION_COMPLETE canonical=$($canonicalTests.Count) execution=$($laneTests.Count) lanes=$($laneDefinitions.Count) inventory=$fullExpectedPath report=$fullReportPath"
