param(
    [DateTime]$MinimumWriteTimeUtc = [DateTime]::MinValue,
    [string]$ResultsRoot,
    [string]$ManifestPath,
    [string]$ReportPath,
    [ValidateRange(1, 2)] [int]$MaximumCoverageWorkers = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$threshold = 0.90
$repoRoot = Split-Path -Parent $PSScriptRoot
$testsPath = Join-Path $repoRoot "tests"
. (Join-Path $PSScriptRoot "verification-coverage-evidence.ps1")

$sourceProjectDirectories = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
Get-ChildItem -Path (Join-Path $repoRoot "src") -Directory -Recurse | Where-Object {
    Test-Path (Join-Path $_.FullName ($_.Name + ".csproj"))
} | ForEach-Object {
    $sourceProjectDirectories.Add($_.Name, $_.FullName)
}
$expectedPackages = @($sourceProjectDirectories.Keys | Sort-Object)

$coverageFiles = @()
$coverageReductionLines = @()
$coverageObservedPackages = @()
if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    $fullResultsRoot = [IO.Path]::GetFullPath($ResultsRoot)
    [void](Assert-VerificationCoverageOrdinaryPath -Path $fullResultsRoot -Root $fullResultsRoot -PathType Container -Description "Coverage results root")
    $fullManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    if (-not (Test-VerificationCoverageDescendantPath -Path $fullManifestPath -Root $fullResultsRoot)) {
        throw "Coverage report manifest is outside the requested results root: $fullManifestPath"
    }
    try {
        $manifestSnapshot = Read-VerificationCoverageSnapshot -Path $fullManifestPath -Root $fullResultsRoot -Description "Coverage report manifest"
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $manifestJson = $strictUtf8.GetString([byte[]]$manifestSnapshot.Bytes)
        $manifestDocument = [Text.Json.JsonDocument]::Parse($manifestJson)
        $manifestRoot = $manifestDocument.RootElement
        if ($manifestRoot.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw "The manifest root is not a JSON object." }
        $rawPropertyNames = @($manifestRoot.EnumerateObject() | ForEach-Object { $_.Name } | Sort-Object -CaseSensitive)
        $expectedPropertyNames = @("schemaVersion", "resultsRoot", "minimumWriteTimeUtc", "laneReportCount", "childReportCount", "aliasReportCount", "reports", "aliases" | Sort-Object -CaseSensitive)
        if (@(Compare-Object -ReferenceObject $expectedPropertyNames -DifferenceObject $rawPropertyNames -CaseSensitive).Count -ne 0 -or $rawPropertyNames.Count -ne $expectedPropertyNames.Count) { throw "The manifest does not have its exact schema-1 property set." }
        $timestampElement = $manifestRoot.GetProperty("minimumWriteTimeUtc")
        $reportsElement = $manifestRoot.GetProperty("reports")
        $aliasesElement = $manifestRoot.GetProperty("aliases")
        if ($timestampElement.ValueKind -ne [Text.Json.JsonValueKind]::String -or $reportsElement.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $aliasesElement.ValueKind -ne [Text.Json.JsonValueKind]::Array) { throw "The manifest timestamp, reports, or aliases have invalid JSON types." }
        foreach ($propertyName in @("resultsRoot")) { Assert-VerificationCoverageJsonStringProperty -Element $manifestRoot -Name $propertyName -Description "Coverage report manifest" }
        foreach ($reportElement in $reportsElement.EnumerateArray()) {
            Assert-VerificationCoverageJsonStringProperty -Element $reportElement -Name "kind" -Description "Coverage report entry"
            $reportKind = $reportElement.GetProperty("kind").GetString()
            if ($reportKind -ceq "lane") {
                Assert-VerificationCoverageJsonElementProperties -Element $reportElement -Expected @("kind", "laneName", "laneResultsRoot", "trxPath", "deploymentRoot", "path", "length", "sha256") -Description "Coverage lane report entry"
                foreach ($propertyName in @("kind", "laneName", "laneResultsRoot", "trxPath", "deploymentRoot", "path", "sha256")) { Assert-VerificationCoverageJsonStringProperty -Element $reportElement -Name $propertyName -Description "Coverage lane report entry" }
            }
            elseif ($reportKind -ceq "child") {
                Assert-VerificationCoverageJsonElementProperties -Element $reportElement -Expected @("kind", "projectName", "childResultsRoot", "path", "length", "sha256") -Description "Coverage child report entry"
                foreach ($propertyName in @("kind", "projectName", "childResultsRoot", "path", "sha256")) { Assert-VerificationCoverageJsonStringProperty -Element $reportElement -Name $propertyName -Description "Coverage child report entry" }
            }
            else { throw "Coverage report manifest contains an unsupported report kind." }
        }
        foreach ($aliasElement in $aliasesElement.EnumerateArray()) {
            Assert-VerificationCoverageJsonElementProperties -Element $aliasElement -Expected @("path", "canonicalPath", "length", "sha256") -Description "Coverage staging alias entry"
            foreach ($propertyName in @("path", "canonicalPath", "sha256")) { Assert-VerificationCoverageJsonStringProperty -Element $aliasElement -Name $propertyName -Description "Coverage staging alias entry" }
        }
        $convertParameters = @{ InputObject = $manifestJson }
        if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey("DateKind")) { $convertParameters.DateKind = "String" }
        $manifest = ConvertFrom-Json @convertParameters
    }
    catch {
        throw "Coverage report manifest is corrupt: $fullManifestPath. $($_.Exception.Message)"
    }
    Assert-VerificationCoverageExactProperties -Value $manifest -Expected @("schemaVersion", "resultsRoot", "minimumWriteTimeUtc", "laneReportCount", "childReportCount", "aliasReportCount", "reports", "aliases") -Description "Coverage report manifest"
    $schemaVersion = Assert-VerificationCoverageJsonInteger -Value $manifest.schemaVersion -Description "Coverage report manifest schemaVersion"
    if ($schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$manifest.resultsRoot) -or -not (Test-VerificationCoverageSamePath -Left ([string]$manifest.resultsRoot) -Right $fullResultsRoot)) {
        throw "Coverage report manifest does not bind the requested schema-1 results root: $fullResultsRoot"
    }
    $manifestMinimumText = $timestampElement.GetString()
    $manifestMinimum = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact($manifestMinimumText, "O", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$manifestMinimum) -or $manifestMinimum.UtcDateTime.Ticks -ne $MinimumWriteTimeUtc.ToUniversalTime().Ticks) {
        throw "Coverage report manifest does not bind the requested minimum write time."
    }

    $laneReportCount = Assert-VerificationCoverageJsonInteger -Value $manifest.laneReportCount -Description "Coverage report manifest laneReportCount"
    $childReportCount = Assert-VerificationCoverageJsonInteger -Value $manifest.childReportCount -Description "Coverage report manifest childReportCount"
    $aliasReportCount = Assert-VerificationCoverageJsonInteger -Value $manifest.aliasReportCount -Description "Coverage report manifest aliasReportCount"
    $manifestReports = @($manifest.reports)
    if ($manifestReports.Count -eq 0) {
        throw "Coverage report manifest contains no reports: $fullManifestPath"
    }
    $manifestAliases = @($manifest.aliases)
    if ($laneReportCount + $childReportCount -ne $manifestReports.Count -or $aliasReportCount -ne $manifestAliases.Count) {
        throw "Coverage report manifest counts do not match its exact report and alias inventories."
    }

    $reportEvidenceByPath = [Collections.Generic.Dictionary[string, object]]::new((Get-VerificationCoveragePathComparer))
    $manifestPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    $laneNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $laneRoots = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    $laneTrxPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    $childRootsByProject = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $childProjectsByRoot = [Collections.Generic.Dictionary[string, string]]::new((Get-VerificationCoveragePathComparer))
    $actualLaneCount = 0
    $actualChildCount = 0
    foreach ($entry in $manifestReports) {
        if ([string]$entry.kind -ceq "lane") {
            Assert-VerificationCoverageExactProperties -Value $entry -Expected @("kind", "laneName", "laneResultsRoot", "trxPath", "deploymentRoot", "path", "length", "sha256") -Description "Coverage lane report entry"
            $actualLaneCount++
        }
        elseif ([string]$entry.kind -ceq "child") {
            Assert-VerificationCoverageExactProperties -Value $entry -Expected @("kind", "projectName", "childResultsRoot", "path", "length", "sha256") -Description "Coverage child report entry"
            $actualChildCount++
        }
        else {
            throw "Coverage report manifest contains an unsupported report kind."
        }

        if ([string]::IsNullOrWhiteSpace([string]$entry.path)) { throw "Coverage report manifest contains an empty report path." }
        $path = [IO.Path]::GetFullPath([string]$entry.path)
        if (-not (Test-VerificationCoverageDescendantPath -Path $path -Root $fullResultsRoot)) {
            throw "Coverage report manifest references a missing or out-of-root report: $path"
        }
        if (-not $manifestPaths.Add($path)) { throw "Coverage report manifest contains duplicate report paths." }

        $reportRecord = [pscustomobject]@{ Entry = $entry; Path = $path; Snapshot = $null }
        if ([string]$entry.kind -ceq "lane") {
            if ([string]::IsNullOrWhiteSpace([string]$entry.laneName) -or [string]::IsNullOrWhiteSpace([string]$entry.laneResultsRoot) -or [string]::IsNullOrWhiteSpace([string]$entry.trxPath) -or [string]::IsNullOrWhiteSpace([string]$entry.deploymentRoot)) {
                throw "Coverage lane report entry is missing exact lane or TRX provenance."
            }
            $laneResultsRoot = [IO.Path]::GetFullPath([string]$entry.laneResultsRoot)
            [void](Assert-VerificationCoverageOrdinaryPath -Path $laneResultsRoot -Root $fullResultsRoot -PathType Container -Description "Coverage lane results root")
            $trxPath = [IO.Path]::GetFullPath([string]$entry.trxPath)
            Assert-VerificationCoverageLaneProvenance -LaneName ([string]$entry.laneName) -LaneResultsRoot $laneResultsRoot -TrxPath $trxPath -CanonicalPath $path -ResultsRoot $fullResultsRoot
            if (-not $laneNames.Add([string]$entry.laneName) -or -not $laneRoots.Add($laneResultsRoot) -or -not $laneTrxPaths.Add($trxPath)) {
                throw "Coverage report manifest contains a duplicate lane name, results root, or exact TRX path."
            }
            $trxSnapshot = Read-VerificationCoverageSnapshot -Path $trxPath -Root $laneResultsRoot -Description "Coverage lane report exact TRX"
            $declaredDeploymentRoot = Get-VerificationCoverageDeploymentRoot -LaneName ([string]$entry.laneName) -TrxSnapshot $trxSnapshot
            if ($declaredDeploymentRoot -cne [string]$entry.deploymentRoot) {
                throw "Coverage lane report deployment root does not match its exact TRX."
            }
            $deploymentPath = [IO.Path]::GetFullPath((Join-Path $laneResultsRoot $declaredDeploymentRoot))
            if (Test-VerificationCoverageDescendantPath -Path $path -Root $deploymentPath) {
                throw "Coverage lane canonical report is inside its VSTest deployment root."
            }
            $reportRecord | Add-Member -NotePropertyName LaneResultsRoot -NotePropertyValue $laneResultsRoot
            $reportRecord | Add-Member -NotePropertyName DeploymentPath -NotePropertyValue $deploymentPath
        }
        else {
            $projectName = [string]$entry.projectName
            $childResultsRoot = [IO.Path]::GetFullPath([string]$entry.childResultsRoot)
            [void](Assert-VerificationCoverageOrdinaryPath -Path $childResultsRoot -Root $fullResultsRoot -PathType Container -Description "Coverage child-process results root")
            Assert-VerificationCoverageChildProvenance -ProjectName $projectName -ChildResultsRoot $childResultsRoot -ReportPath $path -ResultsRoot $fullResultsRoot -RepositoryRoot $repoRoot

            if ($childRootsByProject.ContainsKey($projectName)) {
                if (-not (Test-VerificationCoverageSamePath -Left $childRootsByProject[$projectName] -Right $childResultsRoot)) {
                    throw "Coverage report manifest maps one child-process project to multiple results roots."
                }
            }
            else { $childRootsByProject.Add($projectName, $childResultsRoot) }
            if ($childProjectsByRoot.ContainsKey($childResultsRoot)) {
                if ($childProjectsByRoot[$childResultsRoot] -cne $projectName) {
                    throw "Coverage report manifest maps one child-process results root to multiple projects."
                }
            }
            else { $childProjectsByRoot.Add($childResultsRoot, $projectName) }
        }
        $reportEvidenceByPath.Add($path, $reportRecord)
    }
    if ($actualLaneCount -ne $laneReportCount -or $actualChildCount -ne $childReportCount) { throw "Coverage report manifest report-kind counts do not match its schema-1 totals." }

    $aliasPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    $aliasedCanonicalPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    $aliasEvidenceByPath = [Collections.Generic.Dictionary[string, object]]::new((Get-VerificationCoveragePathComparer))
    foreach ($entry in $manifestAliases) {
        Assert-VerificationCoverageExactProperties -Value $entry -Expected @("path", "canonicalPath", "length", "sha256") -Description "Coverage staging alias entry"
        $pathText = [string]$entry.path
        $canonicalPathText = [string]$entry.canonicalPath
        if ([string]::IsNullOrWhiteSpace($pathText) -or [string]::IsNullOrWhiteSpace($canonicalPathText)) {
            throw "Coverage report manifest contains an incomplete staging alias entry."
        }

        $path = [IO.Path]::GetFullPath($pathText)
        $canonicalPath = [IO.Path]::GetFullPath($canonicalPathText)
        if (-not (Test-VerificationCoverageDescendantPath -Path $path -Root $fullResultsRoot)) {
            throw "Coverage report manifest references a missing or out-of-root staging alias: $path"
        }
        if (-not $reportEvidenceByPath.ContainsKey($canonicalPath)) {
            throw "Coverage report manifest staging alias does not reference one canonical report: $canonicalPath"
        }
        $canonicalRecord = $reportEvidenceByPath[$canonicalPath]
        if ([string]$canonicalRecord.Entry.kind -cne "lane") { throw "Coverage staging alias must reference one lane report." }
        if ($reportEvidenceByPath.ContainsKey($path) -or -not $aliasPaths.Add($path) -or -not $aliasedCanonicalPaths.Add($canonicalPath)) {
            throw "Coverage report manifest contains duplicate or overlapping staging alias paths."
        }

        if (-not (Test-VerificationCoverageStagingAliasPath -Path $path -DeploymentRoot $canonicalRecord.DeploymentPath)) {
            throw "Coverage report manifest staging alias is outside its exact TRX deployment path: $path"
        }
        $aliasEvidenceByPath.Add($path, [pscustomobject]@{ Entry = $entry; CanonicalPath = $canonicalPath })
    }

    $expectedPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($path in $manifestPaths) { [void]$expectedPaths.Add($path) }
    foreach ($path in $aliasPaths) { [void]$expectedPaths.Add($path) }
    $actualPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($file in @(Get-ChildItem -LiteralPath $fullResultsRoot -Recurse -Filter "coverage.cobertura.xml" -File)) {
        [void](Assert-VerificationCoverageOrdinaryPath -Path $file.FullName -Root $fullResultsRoot -PathType Leaf -Description "Coverage result")
        if (-not $actualPaths.Add($file.FullName)) { throw "Coverage results contain duplicate paths under the host filesystem's path semantics." }
    }
    if (-not $expectedPaths.SetEquals($actualPaths)) {
        throw "Coverage results contain missing, stale, or unexpected reports outside the exact manifest."
    }

    $workItems = [Collections.Generic.List[object]]::new()
    foreach ($path in @($manifestPaths | Sort-Object)) {
        $workItems.Add([pscustomobject]@{ Path = $path; Root = $fullResultsRoot; Description = "Coverage report manifest report"; Reduce = $true })
    }
    foreach ($path in @($aliasPaths | Sort-Object)) {
        $workItems.Add([pscustomobject]@{ Path = $path; Root = $fullResultsRoot; Description = "Coverage report manifest staging alias"; Reduce = $false })
    }
    $workerResult = Invoke-VerificationCoverageWorkers -WorkItems @($workItems) -RepositoryRoot $repoRoot -SourceProjectDirectories $sourceProjectDirectories -MaximumWorkers $MaximumCoverageWorkers
    $snapshotsByPath = [Collections.Generic.Dictionary[string, object]]::new((Get-VerificationCoveragePathComparer))
    foreach ($snapshot in $workerResult.Snapshots) { $snapshotsByPath.Add($snapshot.FullName, $snapshot) }
    foreach ($record in $reportEvidenceByPath.Values) {
        $snapshot = $snapshotsByPath[$record.Path]
        Assert-VerificationCoverageEvidenceEntry -Entry $record.Entry -Snapshot $snapshot -MinimumWriteTimeUtc $MinimumWriteTimeUtc -Description "Coverage report manifest report"
        $record.Snapshot = $snapshot
        $coverageFiles += $snapshot
    }
    foreach ($path in $aliasEvidenceByPath.Keys) {
        $aliasRecord = $aliasEvidenceByPath[$path]
        $snapshot = $snapshotsByPath[$path]
        Assert-VerificationCoverageEvidenceEntry -Entry $aliasRecord.Entry -Snapshot $snapshot -MinimumWriteTimeUtc $MinimumWriteTimeUtc -Description "Coverage report manifest staging alias"
        $canonicalSnapshot = $reportEvidenceByPath[$aliasRecord.CanonicalPath].Snapshot
        if ($snapshot.Length -ne $canonicalSnapshot.Length -or $snapshot.Sha256 -cne $canonicalSnapshot.Sha256) {
            throw "Coverage report manifest staging alias does not byte-match its canonical report: $path"
        }
    }
    $coverageReductionLines = @($workerResult.Lines)
    $coverageObservedPackages = @($workerResult.Packages)
}
else {
    $testProjectDirectories = Get-ChildItem -Path $testsPath -Directory | Where-Object { Test-Path (Join-Path $_.FullName ($_.Name + ".csproj")) }
    $workItems = [Collections.Generic.List[object]]::new()
    foreach ($testProjectDirectory in $testProjectDirectories) {
        $testResultsPath = Join-Path $testProjectDirectory.FullName "TestResults"
        if (Test-Path $testResultsPath) {
            foreach ($file in @(Get-ChildItem -Path $testResultsPath -Recurse -Filter "coverage.cobertura.xml" -File | Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc } | Sort-Object FullName)) {
                $workItems.Add([pscustomobject]@{ Path = $file.FullName; Root = $testResultsPath; Description = "Coverage report"; Reduce = $true })
            }
        }
    }
    if ($workItems.Count -gt 0) {
        $workerResult = Invoke-VerificationCoverageWorkers -WorkItems @($workItems) -RepositoryRoot $repoRoot -SourceProjectDirectories $sourceProjectDirectories -MaximumWorkers $MaximumCoverageWorkers
        $coverageFiles = @($workerResult.Snapshots)
        $coverageReductionLines = @($workerResult.Lines)
        $coverageObservedPackages = @($workerResult.Packages)
    }
}

if ($coverageFiles.Count -eq 0) {
    throw "Coverage output was not found in the exact current-run report inventory. Run the canonical verifier first."
}

$failures = @()
$packageSummaries = [Collections.Generic.List[object]]::new()
$packageFileLines = @{}
foreach ($package in $coverageObservedPackages) { $packageFileLines[$package] = @{} }
foreach ($line in $coverageReductionLines) {
    if (-not $packageFileLines.ContainsKey($line.Package)) { $packageFileLines[$line.Package] = @{} }
    $packageFiles = $packageFileLines[$line.Package]
    if (-not $packageFiles.ContainsKey($line.File)) { $packageFiles[$line.File] = @{} }
    $fileLines = $packageFiles[$line.File]
    if (-not $fileLines.ContainsKey($line.Line) -or $line.Hits -gt $fileLines[$line.Line]) {
        $fileLines[$line.Line] = $line.Hits
    }
}

foreach ($expectedPackage in $expectedPackages) {
    if (-not $packageFileLines.ContainsKey($expectedPackage)) {
        $failures += "Coverage output did not include expected production package {0}" -f $expectedPackage
        continue
    }

    $packageFiles = $packageFileLines[$expectedPackage]
    $lineCount = 0
    $coveredLineCount = 0
    foreach ($fileLines in $packageFiles.Values) {
        $lineCount += $fileLines.Count
        foreach ($hits in $fileLines.Values) {
            if ($hits -gt 0) {
                $coveredLineCount++
            }
        }
    }

    if ($lineCount -eq 0) {
        $failures += "Coverage output for {0} did not include executable lines" -f $expectedPackage
        continue
    }

    $lineRate = $coveredLineCount / $lineCount
    $percent = [math]::Round($lineRate * 100, 2)
    Write-Output ("{0}: {1}%" -f $expectedPackage, $percent)
    $packageSummaries.Add([pscustomobject][ordered]@{
        package = $expectedPackage
        coveredLines = $coveredLineCount
        totalLines = $lineCount
        lineRate = [math]::Round($lineRate, 8)
        percent = $percent
    })

    if ($lineRate -lt $threshold) {
        $normalizedRepoRoot = [IO.Path]::GetFullPath($repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).ToUpperInvariant()
        $packageFiles.GetEnumerator() |
            ForEach-Object {
                $fileLineCount = $_.Value.Count
                $fileCoveredLineCount = @($_.Value.Values | Where-Object { $_ -gt 0 }).Count
                $uncoveredLineNumbers = @($_.Value.GetEnumerator() | Where-Object { $_.Value -le 0 } | ForEach-Object { $_.Key } | Sort-Object)
                $displayPath = if ($_.Key.StartsWith($normalizedRepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    $_.Key.Substring($normalizedRepoRoot.Length + 1)
                }
                else {
                    $_.Key
                }

                [pscustomobject]@{
                    File = $displayPath
                    Uncovered = $fileLineCount - $fileCoveredLineCount
                    Total = $fileLineCount
                    UncoveredLines = $uncoveredLineNumbers
                }
            } |
            Where-Object { $_.Uncovered -gt 0 } |
            Sort-Object -Property @{ Expression = "Uncovered"; Descending = $true }, @{ Expression = "Total"; Descending = $true }, @{ Expression = "File"; Descending = $false } |
            Select-Object -First 25 |
            ForEach-Object {
                Write-Output ("COVERAGE_GAP package={0} uncovered={1} total={2} file={3} lines={4}" -f $expectedPackage, $_.Uncovered, $_.Total, $_.File, ($_.UncoveredLines -join ","))
            }

        $failures += "{0} line coverage {1}% is below {2}%" -f $expectedPackage, $percent, ($threshold * 100)
    }
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $fullReportPath = [IO.Path]::GetFullPath($ReportPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $fullReportPath) -Force | Out-Null
    $report = [ordered]@{
        schemaVersion = 1
        threshold = $threshold
        reports = @($coverageFiles | Sort-Object FullName | ForEach-Object { $_.FullName })
        packages = @($packageSummaries | Sort-Object -Property package)
        failures = @($failures)
    }
    [IO.File]::WriteAllText($fullReportPath, ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output "VERIFY_COVERAGE_REPORT reports=$($coverageFiles.Count) packages=$($packageSummaries.Count) path=$fullReportPath"
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}
