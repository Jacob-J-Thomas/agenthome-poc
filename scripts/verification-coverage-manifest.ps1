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

    $canonical = Read-VerificationCoverageSnapshot -Path $canonicalReports[0].FullName -Root $resultsRoot -Description "Coverage lane '$laneName' canonical report"
    $canonicalRelativePath = [IO.Path]::GetRelativePath($resultsRoot, $canonical.FullName)
    $canonicalSegments = @($canonicalRelativePath.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries))
    if ($canonicalSegments.Count -ne 2 -or [string]::IsNullOrWhiteSpace($canonicalSegments[0]) -or $canonicalSegments[0] -ceq "." -or $canonicalSegments[0] -ceq ".." -or $canonicalSegments[0].IndexOfAny([char[]]@('/', '\', ':')) -ge 0 -or $canonicalSegments[1] -cne "coverage.cobertura.xml") {
        throw "Coverage lane '$laneName' canonical report is outside its exact collector path: $($canonical.FullName)"
    }

    $alias = $null
    if ($deploymentReports.Count -eq 1) {
        $alias = Read-VerificationCoverageSnapshot -Path $deploymentReports[0].FullName -Root $resultsRoot -Description "Coverage lane '$laneName' staging alias"
        if (-not (Test-VerificationCoverageStagingAliasPath -Path $alias.FullName -DeploymentRoot $deploymentRoot)) {
            throw "Coverage lane '$laneName' produced a report outside the single allowed VSTest staging alias path: $($alias.FullName)"
        }
        if ($alias.Length -ne $canonical.Length -or $alias.Sha256 -cne $canonical.Sha256) {
            throw "Coverage lane '$laneName' VSTest staging alias does not byte-match its canonical report."
        }
    }

    return [pscustomobject]@{
        LaneName = $laneName
        LaneResultsRoot = $resultsRoot
        TrxPath = $trxSnapshot.FullName
        DeploymentRoot = $deploymentRootName
        Canonical = $canonical
        Alias = $alias
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
        [Parameter(Mandatory = $true)] [string]$ManifestPath
    )

    $fullVerificationResultsPath = [IO.Path]::GetFullPath($VerificationResultsPath)
    [void](Assert-VerificationCoverageOrdinaryPath -Path $fullVerificationResultsPath -Root $fullVerificationResultsPath -PathType Container -Description "Verification results root")
    $fullManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    if (-not (Test-VerificationCoverageDescendantPath -Path $fullManifestPath -Root $fullVerificationResultsPath)) {
        throw "Coverage manifest path is outside the verification results root: $fullManifestPath"
    }

    $laneReports = [Collections.Generic.List[object]]::new()
    $laneAliases = [Collections.Generic.List[object]]::new()
    foreach ($result in $TestResults) {
        $inventory = Get-VerificationCoverageLaneInventory -Result $result -MinimumWriteTimeUtc $MinimumWriteTimeUtc
        $laneReports.Add($inventory)
        if ($null -ne $inventory.Alias) { $laneAliases.Add($inventory) }
    }

    $childReports = [Collections.Generic.List[object]]::new()
    foreach ($isolation in $Isolations) {
        if (-not (Test-Path -LiteralPath $isolation.ChildResultsPath)) { continue }
        $childRoot = [IO.Path]::GetFullPath([string]$isolation.ChildResultsPath)
        [void](Assert-VerificationCoverageOrdinaryPath -Path $childRoot -Root $fullVerificationResultsPath -PathType Container -Description "Coverage child-process results root")
        foreach ($file in @(Get-ChildItem -LiteralPath $childRoot -Recurse -Filter "coverage.cobertura.xml" -File | Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc } | Sort-Object FullName)) {
            $childReports.Add((Read-VerificationCoverageSnapshot -Path $file.FullName -Root $childRoot -Description "Coverage child-process report"))
        }
    }

    $expectedPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($lane in $laneReports) {
        Add-VerificationCoverageInventoryPath -Paths $expectedPaths -Path $lane.Canonical.FullName -Description "canonical report"
        if ($null -ne $lane.Alias) { Add-VerificationCoverageInventoryPath -Paths $expectedPaths -Path $lane.Alias.FullName -Description "staging alias" }
    }
    foreach ($child in $childReports) { Add-VerificationCoverageInventoryPath -Paths $expectedPaths -Path $child.FullName -Description "child-process report" }

    $actualPaths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($file in @(Get-ChildItem -LiteralPath $fullVerificationResultsPath -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)) {
        [void](Assert-VerificationCoverageOrdinaryPath -Path $file.FullName -Root $fullVerificationResultsPath -PathType Leaf -Description "Coverage result")
        if (-not $actualPaths.Add($file.FullName)) { throw "Coverage results contain duplicate paths under the host filesystem's path semantics." }
    }
    if (-not $expectedPaths.SetEquals($actualPaths)) {
        throw "Coverage results contain stale or unexpected reports outside the successful lane/child report inventory."
    }

    $reports = [Collections.Generic.List[object]]::new()
    foreach ($lane in @($laneReports | Sort-Object { $_.Canonical.FullName })) {
        $evidence = Get-VerificationCoverageEvidence -Snapshot $lane.Canonical
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
    foreach ($child in @($childReports | Sort-Object FullName)) {
        $evidence = Get-VerificationCoverageEvidence -Snapshot $child
        $reports.Add([ordered]@{ kind = "child"; path = $evidence.path; length = $evidence.length; sha256 = $evidence.sha256 })
    }

    $aliases = @($laneAliases | Sort-Object { $_.Alias.FullName } | ForEach-Object {
        $evidence = Get-VerificationCoverageEvidence -Snapshot $_.Alias
        [ordered]@{ path = $evidence.path; canonicalPath = $_.Canonical.FullName; length = $evidence.length; sha256 = $evidence.sha256 }
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
