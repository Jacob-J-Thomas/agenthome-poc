function Get-VerificationCoveragePathComparer {
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        return [StringComparer]::OrdinalIgnoreCase
    }

    return [StringComparer]::Ordinal
}

function Get-VerificationCoveragePathComparison {
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        return [StringComparison]::OrdinalIgnoreCase
    }

    return [StringComparison]::Ordinal
}

function Test-VerificationCoverageSamePath {
    param(
        [Parameter(Mandatory = $true)] [string]$Left,
        [Parameter(Mandatory = $true)] [string]$Right
    )

    return [IO.Path]::GetFullPath($Left).Equals([IO.Path]::GetFullPath($Right), (Get-VerificationCoveragePathComparison))
}

function Test-VerificationCoverageDescendantPath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Root
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return $fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, (Get-VerificationCoveragePathComparison))
}

function Assert-VerificationCoverageOrdinaryPath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Root,
        [Parameter(Mandatory = $true)] [ValidateSet("Leaf", "Container")] [string]$PathType,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root)
    if (-not (Test-VerificationCoverageSamePath -Left $fullPath -Right $fullRoot) -and -not (Test-VerificationCoverageDescendantPath -Path $fullPath -Root $fullRoot)) {
        throw "$Description is outside its admitted root: $fullPath"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType $PathType)) {
        throw "$Description is missing or is not a $($PathType.ToLowerInvariant()): $fullPath"
    }

    $relativePath = [IO.Path]::GetRelativePath($fullRoot, $fullPath)
    $paths = [Collections.Generic.List[string]]::new()
    $paths.Add($fullRoot)
    if ($relativePath -cne ".") {
        $current = $fullRoot
        foreach ($segment in $relativePath.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)) {
            $current = Join-Path $current $segment
            $paths.Add($current)
        }
    }

    foreach ($candidatePath in $paths) {
        $item = Get-Item -LiteralPath $candidatePath -Force
        $linkTypeProperty = $item.PSObject.Properties["LinkType"]
        $hasLinkType = $null -ne $linkTypeProperty -and -not [string]::IsNullOrWhiteSpace([string]$linkTypeProperty.Value)
        $isReparsePoint = ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        if ($hasLinkType -or $isReparsePoint) {
            $linkType = if ($hasLinkType) { [string]$linkTypeProperty.Value } else { "ReparsePoint" }
            throw "$Description contains a symbolic, hard-link, or reparse artifact at '$candidatePath' (type '$linkType')."
        }
    }

    return Get-Item -LiteralPath $fullPath -Force
}

function Get-VerificationCoverageSha256 {
    param([Parameter(Mandatory = $true)] [byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Read-VerificationCoverageSnapshot {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Root,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $before = Assert-VerificationCoverageOrdinaryPath -Path $Path -Root $Root -PathType Leaf -Description $Description
    $bytes = [IO.File]::ReadAllBytes($before.FullName)
    $after = Assert-VerificationCoverageOrdinaryPath -Path $before.FullName -Root $Root -PathType Leaf -Description $Description
    if ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc -or $bytes.LongLength -ne $after.Length) {
        throw "$Description changed while its immutable byte snapshot was captured: $($before.FullName)"
    }

    return [pscustomobject]@{
        FullName = $after.FullName
        Bytes = $bytes
        Length = $bytes.LongLength
        LastWriteTimeUtc = $after.LastWriteTimeUtc
        Sha256 = Get-VerificationCoverageSha256 -Bytes $bytes
    }
}

function ConvertFrom-VerificationCoverageXmlSnapshot {
    param(
        [Parameter(Mandatory = $true)] [object]$Snapshot,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $stream = [IO.MemoryStream]::new([byte[]]$Snapshot.Bytes, $false)
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    catch {
        throw "$Description is malformed XML: $($Snapshot.FullName). $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        $stream.Dispose()
    }
}

function Get-VerificationCoverageDeploymentRoot {
    param(
        [Parameter(Mandatory = $true)] [string]$LaneName,
        [Parameter(Mandatory = $true)] [object]$TrxSnapshot
    )

    try {
        $trx = ConvertFrom-VerificationCoverageXmlSnapshot -Snapshot $TrxSnapshot -Description "Coverage lane '$LaneName' exact TRX"
    }
    catch {
        throw "Coverage lane '$LaneName' has a malformed exact TRX: $($TrxSnapshot.FullName). $($_.Exception.Message)"
    }

    $expectedNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
    if ($null -eq $trx.DocumentElement -or $trx.DocumentElement.LocalName -cne "TestRun" -or $trx.DocumentElement.NamespaceURI -cne $expectedNamespace) {
        throw "Coverage lane '$LaneName' has a malformed exact TRX root: $($TrxSnapshot.FullName)"
    }

    $namespaceManager = [Xml.XmlNamespaceManager]::new($trx.NameTable)
    $namespaceManager.AddNamespace("trx", $expectedNamespace)
    $deployments = @($trx.SelectNodes("/trx:TestRun/trx:TestSettings/trx:Deployment", $namespaceManager))
    if ($deployments.Count -ne 1) {
        throw "Coverage lane '$LaneName' exact TRX must declare exactly one Deployment: $($TrxSnapshot.FullName)"
    }

    $deploymentRoot = [string]$deployments[0].GetAttribute("runDeploymentRoot")
    if ([string]::IsNullOrWhiteSpace($deploymentRoot) -or $deploymentRoot -ceq "." -or $deploymentRoot -ceq ".." -or $deploymentRoot.IndexOfAny([char[]]@('/', '\', ':')) -ge 0) {
        throw "Coverage lane '$LaneName' exact TRX declares an unsafe runDeploymentRoot: '$deploymentRoot'."
    }

    return $deploymentRoot
}

function Test-VerificationCoverageStagingAliasPath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$DeploymentRoot
    )

    if (-not (Test-VerificationCoverageDescendantPath -Path $Path -Root $DeploymentRoot)) { return $false }
    $relativeAliasPath = [IO.Path]::GetRelativePath($DeploymentRoot, $Path)
    $segments = @($relativeAliasPath.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries))
    return $segments.Count -eq 3 -and $segments[0] -ceq "In" -and -not [string]::IsNullOrWhiteSpace($segments[1]) -and $segments[1] -cne "." -and $segments[1] -cne ".." -and $segments[1].IndexOfAny([char[]]@('/', '\', ':')) -lt 0 -and $segments[2] -ceq "coverage.cobertura.xml"
}

function Test-VerificationCoverageSafeSegment {
    param([Parameter(Mandatory = $true)] [string]$Value)

    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value -cne "." -and
        $Value -cne ".." -and
        $Value -cmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$'
}

function Assert-VerificationCoverageCollectorPath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$CollectorRoot,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    if (-not (Test-VerificationCoverageDescendantPath -Path $Path -Root $CollectorRoot)) {
        throw "$Description is outside its exact collector root: $Path"
    }

    $relativePath = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($CollectorRoot), [IO.Path]::GetFullPath($Path))
    $segments = @($relativePath.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries))
    $collectorId = [Guid]::Empty
    if ($segments.Count -ne 2 -or -not [Guid]::TryParseExact($segments[0], "D", [ref]$collectorId) -or $segments[1] -cne "coverage.cobertura.xml") {
        throw "$Description is outside its exact GUID collector path: $Path"
    }
}

function Assert-VerificationCoverageLaneProvenance {
    param(
        [Parameter(Mandatory = $true)] [string]$LaneName,
        [Parameter(Mandatory = $true)] [string]$LaneResultsRoot,
        [Parameter(Mandatory = $true)] [string]$TrxPath,
        [Parameter(Mandatory = $true)] [string]$CanonicalPath,
        [Parameter(Mandatory = $true)] [string]$ResultsRoot
    )

    $prefix = "tests-"
    if (-not $LaneName.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "Coverage lane name does not use the exact test-lane identity prefix: '$LaneName'."
    }
    $laneId = $LaneName.Substring($prefix.Length)
    if (-not (Test-VerificationCoverageSafeSegment -Value $laneId)) {
        throw "Coverage lane name contains an unsafe exact test-lane identity: '$LaneName'."
    }

    $expectedRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path $ResultsRoot "StandardTests") $laneId))
    $fullLaneRoot = [IO.Path]::GetFullPath($LaneResultsRoot)
    if (-not (Test-VerificationCoverageSamePath -Left $fullLaneRoot -Right $expectedRoot)) {
        throw "Coverage lane '$LaneName' results root does not match its exact test-lane identity: $fullLaneRoot"
    }

    $expectedTrxPath = [IO.Path]::GetFullPath((Join-Path $expectedRoot "$laneId.trx"))
    if (-not (Test-VerificationCoverageSamePath -Left $TrxPath -Right $expectedTrxPath)) {
        throw "Coverage lane '$LaneName' TRX does not match its exact test-lane identity: $TrxPath"
    }

    Assert-VerificationCoverageCollectorPath -Path $CanonicalPath -CollectorRoot $expectedRoot -Description "Coverage lane '$LaneName' canonical report"
}

function Assert-VerificationCoverageChildProvenance {
    param(
        [Parameter(Mandatory = $true)] [string]$ProjectName,
        [Parameter(Mandatory = $true)] [string]$ChildResultsRoot,
        [Parameter(Mandatory = $true)] [string]$ReportPath,
        [Parameter(Mandatory = $true)] [string]$ResultsRoot,
        [string]$RepositoryRoot
    )

    if (-not (Test-VerificationCoverageSafeSegment -Value $ProjectName)) {
        throw "Coverage child-process project name is not a safe exact project identity: '$ProjectName'."
    }

    $fullResultsRoot = [IO.Path]::GetFullPath($ResultsRoot)
    $fullChildRoot = [IO.Path]::GetFullPath($ChildResultsRoot)
    if (-not (Test-VerificationCoverageDescendantPath -Path $fullChildRoot -Root $fullResultsRoot)) {
        throw "Coverage child-process results root is outside the verification results root: $fullChildRoot"
    }
    $relativeRoot = [IO.Path]::GetRelativePath($fullResultsRoot, $fullChildRoot)
    $segments = @($relativeRoot.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries))
    $hasExactRoot = $segments.Count -eq 6 -and
        $segments[0] -ceq "CoverageIsolation" -and
        $segments[1] -ceq $ProjectName -and
        $segments[2] -ceq "canonical" -and
        $segments[3] -ceq "bin" -and
        ($segments[4] -ceq "Debug" -or $segments[4] -ceq "Release") -and
        $segments[5] -ceq "Results"
    if (-not $hasExactRoot) {
        throw "Coverage child-process results root does not match its exact project isolation provenance: $fullChildRoot"
    }

    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $projectPath = Join-Path (Join-Path (Join-Path $RepositoryRoot "tests") $ProjectName) "$ProjectName.csproj"
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Coverage child-process project identity does not name an admitted test project: '$ProjectName'."
        }
    }

    Assert-VerificationCoverageCollectorPath -Path $ReportPath -CollectorRoot $fullChildRoot -Description "Coverage child-process report"
}

function Get-VerificationCoverageEvidence {
    param([Parameter(Mandatory = $true)] [object]$Snapshot)

    return [ordered]@{
        path = $Snapshot.FullName
        length = $Snapshot.Length
        sha256 = $Snapshot.Sha256
    }
}

function Assert-VerificationCoverageExactProperties {
    param(
        [Parameter(Mandatory = $true)] [object]$Value,
        [Parameter(Mandatory = $true)] [string[]]$Expected,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    if ($null -eq $Value -or $null -eq $Value.PSObject) {
        throw "$Description must be a JSON object."
    }
    $actual = @($Value.PSObject.Properties.Name | Sort-Object -CaseSensitive)
    $expectedSorted = @($Expected | Sort-Object -CaseSensitive)
    if (@(Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actual -CaseSensitive).Count -ne 0) {
        throw "$Description does not match its exact schema-1 property set."
    }
}

function Assert-VerificationCoverageJsonElementProperties {
    param(
        [Parameter(Mandatory = $true)] [Text.Json.JsonElement]$Element,
        [Parameter(Mandatory = $true)] [string[]]$Expected,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw "$Description must be a JSON object." }
    $actual = @($Element.EnumerateObject() | ForEach-Object { $_.Name } | Sort-Object -CaseSensitive)
    $expectedSorted = @($Expected | Sort-Object -CaseSensitive)
    if ($actual.Count -ne $expectedSorted.Count -or @(Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actual -CaseSensitive).Count -ne 0) {
        throw "$Description does not match its exact schema-1 JSON property set."
    }
}

function Assert-VerificationCoverageJsonStringProperty {
    param(
        [Parameter(Mandatory = $true)] [Text.Json.JsonElement]$Element,
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    if ($Element.GetProperty($Name).ValueKind -ne [Text.Json.JsonValueKind]::String) {
        throw "$Description property '$Name' must be a schema-1 JSON string."
    }
}

function Assert-VerificationCoverageJsonInteger {
    param(
        [Parameter(Mandatory = $true)] [object]$Value,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $integerTypes = @([byte], [sbyte], [int16], [uint16], [int32], [uint32], [int64], [uint64])
    if (-not ($integerTypes | Where-Object { $Value -is $_ }) -or [decimal]$Value -lt 0 -or [decimal]$Value -gt [int]::MaxValue) {
        throw "$Description must be a non-negative schema-1 integer."
    }

    return [int]$Value
}

function Assert-VerificationCoverageEvidenceEntry {
    param(
        [Parameter(Mandatory = $true)] [object]$Entry,
        [Parameter(Mandatory = $true)] [object]$Snapshot,
        [Parameter(Mandatory = $true)] [DateTime]$MinimumWriteTimeUtc,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $length = Assert-VerificationCoverageJsonInteger -Value $Entry.length -Description "$Description length"
    $hash = [string]$Entry.sha256
    if ($length -ne $Snapshot.Length -or $hash -cnotmatch '^[0-9a-f]{64}$' -or $hash -cne $Snapshot.Sha256 -or $Snapshot.LastWriteTimeUtc -lt $MinimumWriteTimeUtc) {
        throw "$Description evidence does not match its fresh immutable byte snapshot: $($Snapshot.FullName)"
    }
}
