function Get-VerificationCoverageEvidence {
    param([Parameter(Mandatory = $true)] [System.IO.FileInfo]$File)

    return [ordered]@{
        path = $File.FullName
        length = $File.Length
        sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Test-VerificationCoverageDescendantPath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Root
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return $fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Get-VerificationCoverageDeploymentRoot {
    param(
        [Parameter(Mandatory = $true)] [string]$LaneName,
        [Parameter(Mandatory = $true)] [string]$TrxPath
    )

    $fullTrxPath = [IO.Path]::GetFullPath($TrxPath)
    if (-not (Test-Path -LiteralPath $fullTrxPath -PathType Leaf)) {
        throw "Coverage lane '$LaneName' is missing its exact TRX: $fullTrxPath"
    }

    try {
        [xml]$trx = [IO.File]::ReadAllText($fullTrxPath)
    }
    catch {
        throw "Coverage lane '$LaneName' has a malformed exact TRX: $fullTrxPath. $($_.Exception.Message)"
    }

    $expectedNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
    if ($null -eq $trx.DocumentElement -or $trx.DocumentElement.LocalName -cne "TestRun" -or $trx.DocumentElement.NamespaceURI -cne $expectedNamespace) {
        throw "Coverage lane '$LaneName' has a malformed exact TRX root: $fullTrxPath"
    }

    $namespaceManager = [Xml.XmlNamespaceManager]::new($trx.NameTable)
    $namespaceManager.AddNamespace("trx", $expectedNamespace)
    $deployments = @($trx.SelectNodes("/trx:TestRun/trx:TestSettings/trx:Deployment", $namespaceManager))
    if ($deployments.Count -ne 1) {
        throw "Coverage lane '$LaneName' exact TRX must declare exactly one Deployment: $fullTrxPath"
    }

    $deploymentRoot = [string]$deployments[0].GetAttribute("runDeploymentRoot")
    if ([string]::IsNullOrWhiteSpace($deploymentRoot) -or $deploymentRoot -ceq "." -or $deploymentRoot -ceq ".." -or $deploymentRoot.IndexOfAny([char[]]@('/', '\', ':')) -ge 0) {
        throw "Coverage lane '$LaneName' exact TRX declares an unsafe runDeploymentRoot: '$deploymentRoot'."
    }

    return $deploymentRoot
}

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
    if (-not (Test-Path -LiteralPath $resultsRoot -PathType Container)) {
        throw "Coverage lane '$laneName' results root is missing: $resultsRoot"
    }

    $trxPath = [IO.Path]::GetFullPath([string]$Result.TrxPath)
    if (-not (Test-VerificationCoverageDescendantPath -Path $trxPath -Root $resultsRoot)) {
        throw "Coverage lane '$laneName' exact TRX is outside its results root: $trxPath"
    }

    $deploymentRootName = Get-VerificationCoverageDeploymentRoot -LaneName $laneName -TrxPath $trxPath
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

    $canonical = $canonicalReports[0]
    $alias = $null
    if ($deploymentReports.Count -eq 1) {
        $alias = $deploymentReports[0]
        $relativeAliasPath = [IO.Path]::GetRelativePath($deploymentRoot, $alias.FullName)
        $aliasSegments = @($relativeAliasPath.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries))
        if ($aliasSegments.Count -ne 3 -or $aliasSegments[0] -cne "In" -or [string]::IsNullOrWhiteSpace($aliasSegments[1]) -or $aliasSegments[1] -ceq "." -or $aliasSegments[1] -ceq ".." -or $aliasSegments[1].IndexOfAny([char[]]@('/', '\', ':')) -ge 0 -or $aliasSegments[2] -cne "coverage.cobertura.xml") {
            throw "Coverage lane '$laneName' produced a report outside the single allowed VSTest staging alias path: $($alias.FullName)"
        }

        $canonicalHash = (Get-FileHash -LiteralPath $canonical.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $aliasHash = (Get-FileHash -LiteralPath $alias.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($alias.Length -ne $canonical.Length -or $aliasHash -cne $canonicalHash) {
            throw "Coverage lane '$laneName' VSTest staging alias does not byte-match its canonical report."
        }
    }

    return [pscustomobject]@{
        Canonical = $canonical
        Alias = $alias
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
    $fullManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    $laneReports = [Collections.Generic.List[System.IO.FileInfo]]::new()
    $laneAliases = [Collections.Generic.List[object]]::new()
    foreach ($result in $TestResults) {
        $inventory = Get-VerificationCoverageLaneInventory -Result $result -MinimumWriteTimeUtc $MinimumWriteTimeUtc
        $laneReports.Add($inventory.Canonical)
        if ($null -ne $inventory.Alias) {
            $laneAliases.Add([pscustomobject]@{ Alias = $inventory.Alias; Canonical = $inventory.Canonical })
        }
    }

    $childReports = @($Isolations | ForEach-Object {
        if (Test-Path -LiteralPath $_.ChildResultsPath) {
            Get-ChildItem -LiteralPath $_.ChildResultsPath -Recurse -Filter "coverage.cobertura.xml" -File | Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc }
        }
    } | Sort-Object FullName)
    $allReports = @((@($laneReports) + @($childReports)) | Sort-Object FullName)
    $allAliases = @($laneAliases | Sort-Object { $_.Alias.FullName })
    $expectedPaths = @((@($allReports.FullName) + @($allAliases | ForEach-Object { $_.Alias.FullName })) | Sort-Object -Unique)
    $allCoverageFiles = @(Get-ChildItem -LiteralPath $fullVerificationResultsPath -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)
    $actualPaths = @($allCoverageFiles.FullName | Sort-Object -Unique)
    if (@(Compare-Object -ReferenceObject $expectedPaths -DifferenceObject $actualPaths -CaseSensitive).Count -ne 0) {
        throw "Coverage results contain stale or unexpected reports outside the successful lane/child report inventory."
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        resultsRoot = $fullVerificationResultsPath
        minimumWriteTimeUtc = $MinimumWriteTimeUtc.ToString("O")
        laneReportCount = $laneReports.Count
        childReportCount = $childReports.Count
        aliasReportCount = $laneAliases.Count
        reports = @($allReports | ForEach-Object { Get-VerificationCoverageEvidence -File $_ })
        aliases = @($allAliases | ForEach-Object {
            $evidence = Get-VerificationCoverageEvidence -File $_.Alias
            [ordered]@{
                path = $evidence.path
                canonicalPath = $_.Canonical.FullName
                length = $evidence.length
                sha256 = $evidence.sha256
            }
        })
    }
    [IO.File]::WriteAllText($fullManifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output "VERIFY_COVERAGE_MANIFEST_COMPLETE lanes=$($laneReports.Count) child_reports=$($childReports.Count) aliases=$($laneAliases.Count) reports=$($allReports.Count) path=$fullManifestPath"
}
