param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TestAssemblyPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Filter,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$DiagnosticPath,

    [string]$WorkingDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-XunitUniqueId {
    param([object]$TestCase)

    $matches = @($TestCase.Properties | Where-Object { $_.Key.Id -ceq "XunitTestCaseUniqueID" })
    if ($matches.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$matches[0].Value)) {
        throw "Discovered test '$($TestCase.FullyQualifiedName)' does not expose one XunitTestCaseUniqueID."
    }

    return [string]$matches[0].Value
}

$assembly = [IO.Path]::GetFullPath($TestAssemblyPath)
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw "Required-test discovery assembly is missing: $assembly"
}

$root = if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) { (Get-Location).Path } else { [IO.Path]::GetFullPath($WorkingDirectory) }
$fullDiagnosticPath = [IO.Path]::GetFullPath($DiagnosticPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $fullDiagnosticPath) -Force | Out-Null
if (Test-Path -LiteralPath $fullDiagnosticPath) {
    Remove-Item -LiteralPath $fullDiagnosticPath -Force
}

$arguments = @(
    "vstest",
    $assembly,
    "--ListTests",
    "--TestCaseFilter:$Filter",
    "--TestAdapterPath:$(Split-Path -Parent $assembly)",
    "--Diag:$fullDiagnosticPath",
    "--",
    "RunConfiguration.BatchSize=100"
)
$raw = @(& dotnet @arguments 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Required-test discovery failed for '$assembly' with exit code $LASTEXITCODE. $($raw -join [Environment]::NewLine)"
}

if (-not (Test-Path -LiteralPath $fullDiagnosticPath -PathType Leaf)) {
    throw "Required-test discovery did not produce its diagnostic protocol log: $fullDiagnosticPath"
}

$messagePrefix = "TestRequestSender.OnDiscoveryMessageReceived: Received message: "
$completedMessages = [Collections.Generic.List[object]]::new()
$tests = [Collections.Generic.List[object]]::new()
foreach ($line in [IO.File]::ReadLines($fullDiagnosticPath)) {
    $prefixIndex = $line.IndexOf($messagePrefix, [StringComparison]::Ordinal)
    if ($prefixIndex -lt 0) {
        continue
    }

    $json = $line.Substring($prefixIndex + $messagePrefix.Length)
    try {
        $message = $json | ConvertFrom-Json
    }
    catch {
        throw "Required-test discovery emitted malformed JSON protocol evidence in '$fullDiagnosticPath'. $($_.Exception.Message)"
    }

    if ($message.MessageType -ceq "TestDiscovery.TestFound") {
        foreach ($test in @($message.Payload)) {
            $tests.Add($test)
        }
    }
    elseif ($message.MessageType -ceq "TestDiscovery.Completed") {
        $completedMessages.Add($message.Payload)
        foreach ($test in @($message.Payload.LastDiscoveredTests)) {
            $tests.Add($test)
        }
    }
}

if ($completedMessages.Count -ne 1) {
    throw "Required-test discovery must emit exactly one completion message; found $($completedMessages.Count) in '$fullDiagnosticPath'."
}

$completion = $completedMessages[0]
if ([bool]$completion.IsAborted) {
    throw "Required-test discovery was aborted for '$assembly'."
}

foreach ($propertyName in @("PartiallyDiscoveredSources", "NotDiscoveredSources", "SkippedDiscoverySources")) {
    if (@($completion.$propertyName).Count -ne 0) {
        throw "Required-test discovery reported $propertyName for '$assembly': $(@($completion.$propertyName) -join ', ')"
    }
}

$fullyDiscoveredSources = @($completion.FullyDiscoveredSources | ForEach-Object { [IO.Path]::GetFullPath([string]$_) })
if ($fullyDiscoveredSources.Count -ne 1 -or -not $fullyDiscoveredSources[0].Equals($assembly, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Required-test discovery did not fully discover exactly the requested assembly '$assembly'. Actual: $($fullyDiscoveredSources -join ', ')"
}

if ([int]$completion.TotalTests -ne $tests.Count -or $tests.Count -eq 0) {
    throw "Required-test discovery count mismatch for '$assembly'. completion=$($completion.TotalTests) collected=$($tests.Count)."
}

$records = [Collections.Generic.List[object]]::new()
foreach ($test in $tests) {
    $idValue = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$test.Id, [ref]$idValue)) {
        throw "Discovered test '$($test.FullyQualifiedName)' has an invalid TestCase Id."
    }

    $source = [IO.Path]::GetFullPath([string]$test.Source)
    if (-not $source.Equals($assembly, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Discovered test '$($test.FullyQualifiedName)' reports an unexpected source '$source'."
    }

    foreach ($requiredProperty in @("FullyQualifiedName", "DisplayName", "ExecutorUri")) {
        if ([string]::IsNullOrWhiteSpace([string]$test.$requiredProperty)) {
            throw "Discovered test '$($test.Id)' is missing $requiredProperty."
        }
    }

    $records.Add([pscustomobject][ordered]@{
        id = $idValue.ToString("D")
        xunitTestCaseUniqueId = Get-XunitUniqueId -TestCase $test
        fullyQualifiedName = [string]$test.FullyQualifiedName
        displayName = [string]$test.DisplayName
        executorUri = [string]$test.ExecutorUri
        source = $source
    })
}

$duplicateIds = @($records | Group-Object -Property id -CaseSensitive | Where-Object { $_.Count -ne 1 })
$duplicateXunitIds = @($records | Group-Object -Property xunitTestCaseUniqueId -CaseSensitive | Where-Object { $_.Count -ne 1 })
if ($duplicateIds.Count -gt 0 -or $duplicateXunitIds.Count -gt 0) {
    throw "Required-test discovery returned duplicate stable identities for '$assembly'. test_ids=$($duplicateIds.Count) xunit_ids=$($duplicateXunitIds.Count)."
}

$inventory = [ordered]@{
    schemaVersion = 1
    source = $assembly
    filter = $Filter
    totalTests = $records.Count
    tests = @($records | Sort-Object -Property xunitTestCaseUniqueId)
}
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $fullOutputPath) -Force | Out-Null
[IO.File]::WriteAllText($fullOutputPath, ($inventory | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output "VERIFY_TEST_DISCOVERY_COMPLETE source=$assembly filter=$Filter count=$($records.Count) output=$fullOutputPath diagnostic=$fullDiagnosticPath working_directory=$root"
