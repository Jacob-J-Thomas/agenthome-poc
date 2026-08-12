function Get-VerificationCoverageLaneInventory {
    param(
        [Parameter(Mandatory = $true)] [object]$Result,
        [Parameter(Mandatory = $true)] [DateTime]$MinimumWriteTimeUtc
    )

    $laneName = [string]$Result.Name
    if ([string]::IsNullOrWhiteSpace($laneName) -or [string]::IsNullOrWhiteSpace([string]$Result.CoverageSearchRoot) -or [string]::IsNullOrWhiteSpace([string]$Result.TrxPath)) {
        throw "Coverage lane evidence is missing its name, results root, or exact TRX path."
    }

    $resultsRoot = [IO.Path]::GetFullPath([string]$Result.CoverageSearchRoot)
    [void](Assert-VerificationCoverageOrdinaryPath -Path $resultsRoot -Root $resultsRoot -PathType Container -Description "Coverage lane '$laneName' results root")
    $trxPath = [IO.Path]::GetFullPath([string]$Result.TrxPath)
    if (-not (Test-VerificationCoverageDescendantPath -Path $trxPath -Root $resultsRoot) -or -not (Test-VerificationCoverageSamePath -Left (Split-Path -Parent $trxPath) -Right $resultsRoot)) {
        throw "Coverage lane '$laneName' exact TRX is outside the root of its results directory: $trxPath"
    }
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw "Coverage lane '$laneName' is missing its exact TRX: $trxPath" }
    $trxSnapshot = Read-VerificationCoverageSnapshot -Path $trxPath -Root $resultsRoot -Description "Coverage lane '$laneName' exact TRX"

    $deploymentRootName = Get-VerificationCoverageDeploymentRoot -LaneName $laneName -TrxSnapshot $trxSnapshot
    $deploymentRoot = [IO.Path]::GetFullPath((Join-Path $resultsRoot $deploymentRootName))
    $freshReports = @(Get-ChildItem -LiteralPath $resultsRoot -Recurse -Filter "coverage.cobertura.xml" -File | Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc } | Sort-Object FullName)
    $canonicalReports = @($freshReports | Where-Object { -not (Test-VerificationCoverageDescendantPath -Path $_.FullName -Root $deploymentRoot) })
    if ($canonicalReports.Count -ne 1) {
        throw "Coverage lane '$laneName' produced $($canonicalReports.Count) fresh canonical reports outside its declared deployment root; exactly one is required."
    }

    $deploymentReports = @($freshReports | Where-Object { Test-VerificationCoverageDescendantPath -Path $_.FullName -Root $deploymentRoot })
    if ($deploymentReports.Count -gt 1) {
        throw "Coverage lane '$laneName' produced $($deploymentReports.Count) VSTest staging reports; at most one is allowed."
    }

    $canonicalPath = [IO.Path]::GetFullPath($canonicalReports[0].FullName)
    Assert-VerificationCoverageCollectorPath -Path $canonicalPath -CollectorRoot $resultsRoot -Description "Coverage lane '$laneName' canonical report"

    $aliasPath = $null
    if ($deploymentReports.Count -eq 1) {
        $aliasPath = [IO.Path]::GetFullPath($deploymentReports[0].FullName)
        if (-not (Test-VerificationCoverageStagingAliasPath -Path $aliasPath -DeploymentRoot $deploymentRoot)) {
            throw "Coverage lane '$laneName' produced a report outside the single allowed VSTest staging alias path: $aliasPath"
        }
    }

    return [pscustomobject]@{
        LaneName = $laneName
        LaneResultsRoot = $resultsRoot
        TrxPath = $trxSnapshot.FullName
        DeploymentRoot = $deploymentRootName
        CanonicalPath = $canonicalPath
        AliasPath = $aliasPath
    }
}

function Add-VerificationCoverageInventoryPath {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [Collections.Generic.HashSet[string]]$Paths,
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    if (-not $Paths.Add([IO.Path]::GetFullPath($Path))) {
        throw "Coverage inventory contains a duplicate $Description path: $Path"
    }
}

function Write-CoverageManifest {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$TestResults,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$Isolations,
        [Parameter(Mandatory = $true)] [DateTime]$MinimumWriteTimeUtc,
        [Parameter(Mandatory = $true)] [string]$VerificationResultsPath,
        [Parameter(Mandatory = $true)] [string]$ManifestPath,
        [ValidateRange(1, 2)] [int]$MaximumCoverageWorkers = 2
    )

    $fullVerificationResultsPath = [IO.Path]::GetFullPath($VerificationResultsPath)
    [void](Assert-VerificationCoverageOrdinaryPath -Path $fullVerificationResultsPath -Root $fullVerificationResultsPath -PathType Container -Description "Verification results root")
    $fullManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    if (-not (Test-VerificationCoverageDescendantPath -Path $fullManifestPath -Root $fullVerificationResultsPath)) {
        throw "Coverage manifest path is outside the verification results root: $fullManifestPath"
    }

    $laneReports = [Collections.Generic.List[object]]::new()
    $laneAliases = [Collections.Generic.List[object]]::new()
    $laneNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $laneRoots = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    $laneTrxPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($result in $TestResults) {
        $inventory = Get-VerificationCoverageLaneInventory -Result $result -MinimumWriteTimeUtc $MinimumWriteTimeUtc
        Assert-VerificationCoverageLaneProvenance -LaneName $inventory.LaneName -LaneResultsRoot $inventory.LaneResultsRoot -TrxPath $inventory.TrxPath -CanonicalPath $inventory.CanonicalPath -ResultsRoot $fullVerificationResultsPath
        if (-not $laneNames.Add($inventory.LaneName) -or -not $laneRoots.Add($inventory.LaneResultsRoot) -or -not $laneTrxPaths.Add($inventory.TrxPath)) {
            throw "Coverage inventory contains a duplicate lane name, results root, or exact TRX path."
        }
        $laneReports.Add($inventory)
        if ($null -ne $inventory.AliasPath) { $laneAliases.Add($inventory) }
    }

    $childReports = [Collections.Generic.List[object]]::new()
    $childRootsByProject = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $childProjectsByRoot = [Collections.Generic.Dictionary[string, string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($isolation in $Isolations) {
        if (-not (Test-Path -LiteralPath $isolation.ChildResultsPath)) { continue }
        $projectName = [string]$isolation.Project.BaseName
        $childRoot = [IO.Path]::GetFullPath([string]$isolation.ChildResultsPath)
        [void](Assert-VerificationCoverageOrdinaryPath -Path $childRoot -Root $fullVerificationResultsPath -PathType Container -Description "Coverage child-process results root")
        if ($childRootsByProject.ContainsKey($projectName) -or $childProjectsByRoot.ContainsKey($childRoot)) {
            throw "Coverage inventory contains a duplicate child-process project or results root declaration."
        }
        $childRootsByProject.Add($projectName, $childRoot)
        $childProjectsByRoot.Add($childRoot, $projectName)
        foreach ($file in @(Get-ChildItem -LiteralPath $childRoot -Recurse -Filter "coverage.cobertura.xml" -File | Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc } | Sort-Object FullName)) {
            Assert-VerificationCoverageChildProvenance -ProjectName $projectName -ChildResultsRoot $childRoot -ReportPath $file.FullName -ResultsRoot $fullVerificationResultsPath
            $childReports.Add([pscustomobject]@{
                ProjectName = $projectName
                ChildResultsRoot = $childRoot
                Path = [IO.Path]::GetFullPath($file.FullName)
            })
        }
    }

    $expectedPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($lane in $laneReports) {
        Add-VerificationCoverageInventoryPath -Paths $expectedPaths -Path $lane.CanonicalPath -Description "canonical report"
        if ($null -ne $lane.AliasPath) { Add-VerificationCoverageInventoryPath -Paths $expectedPaths -Path $lane.AliasPath -Description "staging alias" }
    }
    foreach ($child in $childReports) { Add-VerificationCoverageInventoryPath -Paths $expectedPaths -Path $child.Path -Description "child-process report" }

    $actualPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($file in @(Get-ChildItem -LiteralPath $fullVerificationResultsPath -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)) {
        [void](Assert-VerificationCoverageOrdinaryPath -Path $file.FullName -Root $fullVerificationResultsPath -PathType Leaf -Description "Coverage result")
        if (-not $actualPaths.Add($file.FullName)) { throw "Coverage results contain duplicate paths under the host filesystem's path semantics." }
    }
    if (-not $expectedPaths.SetEquals($actualPaths)) {
        throw "Coverage results contain stale or unexpected reports outside the successful lane/child report inventory."
    }

    $workItems = @($expectedPaths | Sort-Object | ForEach-Object {
        [pscustomobject]@{
            Path = $_
            Root = $fullVerificationResultsPath
            Description = "Coverage manifest report"
            Reduce = $false
        }
    })
    $sourceProjectDirectories = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $workerResult = Invoke-VerificationCoverageWorkers -WorkItems $workItems -RepositoryRoot (Split-Path -Parent $PSScriptRoot) -SourceProjectDirectories $sourceProjectDirectories -MaximumWorkers $MaximumCoverageWorkers
    $snapshotsByPath = [Collections.Generic.Dictionary[string, object]]::new((Get-VerificationCoveragePathComparer))
    foreach ($snapshot in $workerResult.Snapshots) {
        if ($snapshot.LastWriteTimeUtc -lt $MinimumWriteTimeUtc) {
            throw "Coverage manifest report is stale: $($snapshot.FullName)"
        }
        $snapshotsByPath.Add($snapshot.FullName, $snapshot)
    }
    foreach ($lane in $laneAliases) {
        $canonicalSnapshot = $snapshotsByPath[$lane.CanonicalPath]
        $aliasSnapshot = $snapshotsByPath[$lane.AliasPath]
        if ($aliasSnapshot.Length -ne $canonicalSnapshot.Length -or $aliasSnapshot.Sha256 -cne $canonicalSnapshot.Sha256) {
            throw "Coverage lane '$($lane.LaneName)' VSTest staging alias does not byte-match its canonical report."
        }
    }

    $reports = [Collections.Generic.List[object]]::new()
    foreach ($lane in @($laneReports | Sort-Object CanonicalPath)) {
        $evidence = Get-VerificationCoverageEvidence -Snapshot $snapshotsByPath[$lane.CanonicalPath]
        $reports.Add([ordered]@{
            kind = "lane"
            laneName = $lane.LaneName
            laneResultsRoot = $lane.LaneResultsRoot
            trxPath = $lane.TrxPath
            deploymentRoot = $lane.DeploymentRoot
            path = $evidence.path
            length = $evidence.length
            sha256 = $evidence.sha256
        })
    }
    foreach ($child in @($childReports | Sort-Object Path)) {
        $evidence = Get-VerificationCoverageEvidence -Snapshot $snapshotsByPath[$child.Path]
        $reports.Add([ordered]@{
            kind = "child"
            projectName = $child.ProjectName
            childResultsRoot = $child.ChildResultsRoot
            path = $evidence.path
            length = $evidence.length
            sha256 = $evidence.sha256
        })
    }

    $aliases = @($laneAliases | Sort-Object AliasPath | ForEach-Object {
        $evidence = Get-VerificationCoverageEvidence -Snapshot $snapshotsByPath[$_.AliasPath]
        [ordered]@{ path = $evidence.path; canonicalPath = $_.CanonicalPath; length = $evidence.length; sha256 = $evidence.sha256 }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        resultsRoot = $fullVerificationResultsPath
        minimumWriteTimeUtc = $MinimumWriteTimeUtc.ToUniversalTime().ToString("O")
        laneReportCount = $laneReports.Count
        childReportCount = $childReports.Count
        aliasReportCount = $laneAliases.Count
        reports = @($reports)
        aliases = $aliases
    }
    [IO.File]::WriteAllText($fullManifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output "VERIFY_COVERAGE_MANIFEST_COMPLETE lanes=$($laneReports.Count) child_reports=$($childReports.Count) aliases=$($laneAliases.Count) reports=$($reports.Count) path=$fullManifestPath"
}
