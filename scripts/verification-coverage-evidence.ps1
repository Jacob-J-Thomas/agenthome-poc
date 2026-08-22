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

function Read-VerificationCoverageHashSnapshot {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Root,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $before = Assert-VerificationCoverageOrdinaryPath -Path $Path -Root $Root -PathType Leaf -Description $Description
    $stream = $null
    $algorithm = $null
    try {
        $stream = [IO.FileStream]::new(
            $before.FullName,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read,
            131072,
            [IO.FileOptions]::SequentialScan)
        if ($stream.Length -ne $before.Length) {
            throw "$Description changed before its immutable hash snapshot was captured: $($before.FullName)"
        }

        $algorithm = [Security.Cryptography.SHA256]::Create()
        $hash = $algorithm.ComputeHash($stream)
    }
    finally {
        if ($null -ne $algorithm) { $algorithm.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }

    $after = Assert-VerificationCoverageOrdinaryPath -Path $before.FullName -Root $Root -PathType Leaf -Description $Description
    if ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc) {
        throw "$Description changed while its immutable hash snapshot was captured: $($before.FullName)"
    }

    return [pscustomobject]@{
        FullName = $after.FullName
        Length = $after.Length
        LastWriteTimeUtc = $after.LastWriteTimeUtc
        Sha256 = ([BitConverter]::ToString($hash)).Replace("-", "").ToLowerInvariant()
    }
}

function Get-VerificationCoverageFileKey {
    param(
        [Parameter(Mandatory = $true)] [string]$PackageName,
        [Parameter(Mandatory = $true)] [string]$FileName,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [Collections.Generic.Dictionary[string, string]]$SourceProjectDirectories
    )

    $directorySeparator = [IO.Path]::DirectorySeparatorChar
    $normalizedFileName = $FileName.Replace("/", $directorySeparator).Replace("\", $directorySeparator)
    $sourceSegment = "src$directorySeparator"
    $sourceIndex = $normalizedFileName.IndexOf($sourceSegment, [StringComparison]::OrdinalIgnoreCase)

    if ([IO.Path]::IsPathRooted($normalizedFileName)) {
        $candidatePath = $normalizedFileName
    }
    elseif ($normalizedFileName.StartsWith("src$directorySeparator", [StringComparison]::OrdinalIgnoreCase)) {
        $candidatePath = Join-Path $RepositoryRoot $normalizedFileName
    }
    elseif ($sourceIndex -ge 0) {
        $candidatePath = Join-Path $RepositoryRoot $normalizedFileName.Substring($sourceIndex)
    }
    elseif ($normalizedFileName -match "^\d{2}-") {
        $candidatePath = Join-Path (Join-Path $RepositoryRoot "src") $normalizedFileName
    }
    elseif ($normalizedFileName.StartsWith("$PackageName$directorySeparator", [StringComparison]::OrdinalIgnoreCase) -and $SourceProjectDirectories.ContainsKey($PackageName)) {
        $relativeProjectFileName = $normalizedFileName.Substring($PackageName.Length + 1)
        $candidatePath = Join-Path $SourceProjectDirectories[$PackageName] $relativeProjectFileName
    }
    else {
        $candidatePath = Join-Path $RepositoryRoot $normalizedFileName
    }

    return [IO.Path]::GetFullPath($candidatePath).ToUpperInvariant()
}

function Read-VerificationCoverageReductionSnapshot {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Root,
        [Parameter(Mandatory = $true)] [string]$Description,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [Collections.Generic.Dictionary[string, string]]$SourceProjectDirectories,
        [Collections.Generic.Dictionary[string, object]]$Destination
    )

    $captured = Read-VerificationCoverageSnapshot -Path $Path -Root $Root -Description $Description
    $coverage = ConvertFrom-VerificationCoverageXmlSnapshot -Snapshot $captured -Description $Description
    if ($null -eq $coverage.DocumentElement -or $coverage.DocumentElement.LocalName -cne "coverage") {
        throw "$Description has an invalid document root: $($captured.FullName)"
    }

    $expectedPackages = [Collections.Generic.HashSet[string]]::new($SourceProjectDirectories.Keys, [StringComparer]::Ordinal)
    $packageFileLines = if ($null -eq $Destination) {
        [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    }
    else {
        $Destination
    }
    foreach ($package in $coverage.coverage.packages.package) {
        $packageName = [string]$package.name
        if (-not $expectedPackages.Contains($packageName)) { continue }
        if (-not $packageFileLines.ContainsKey($packageName)) {
            $packageFileLines.Add($packageName, [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal))
        }

        $packageFiles = $packageFileLines[$packageName]
        foreach ($class in $package.classes.class) {
            $classLines = $class.SelectNodes("lines/line")
            if ($classLines.Count -eq 0) { continue }
            $fileKey = Get-VerificationCoverageFileKey -PackageName $packageName -FileName ([string]$class.filename) -RepositoryRoot $RepositoryRoot -SourceProjectDirectories $SourceProjectDirectories
            if (-not $packageFiles.ContainsKey($fileKey)) {
                $packageFiles.Add($fileKey, [Collections.Generic.Dictionary[int, int]]::new())
            }
            $fileLines = $packageFiles[$fileKey]
            foreach ($line in $classLines) {
                $lineNumber = [int]$line.number
                $hits = [int]$line.hits
                if (-not $fileLines.ContainsKey($lineNumber) -or $hits -gt $fileLines[$lineNumber]) {
                    $fileLines[$lineNumber] = $hits
                }
            }
        }
    }

    $lines = [Collections.Generic.List[object]]::new()
    if ($null -eq $Destination) {
        foreach ($packageEntry in @($packageFileLines.GetEnumerator() | Sort-Object Key -CaseSensitive)) {
            foreach ($fileEntry in @($packageEntry.Value.GetEnumerator() | Sort-Object Key -CaseSensitive)) {
                foreach ($lineEntry in @($fileEntry.Value.GetEnumerator() | Sort-Object Key)) {
                    $lines.Add([pscustomobject][ordered]@{
                        package = $packageEntry.Key
                        file = $fileEntry.Key
                        line = $lineEntry.Key
                        hits = $lineEntry.Value
                    })
                }
            }
        }
    }

    return [pscustomobject]@{
        Snapshot = [pscustomobject]@{
            FullName = $captured.FullName
            Length = $captured.Length
            LastWriteTimeUtc = $captured.LastWriteTimeUtc
            Sha256 = $captured.Sha256
        }
        Lines = @($lines)
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

function Initialize-VerificationCoverageParallelProcessor {
    if ($null -ne ("VerificationCoverageParallelProcessor" -as [type])) { return }

    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml;

public sealed class VerificationCoverageParallelWorkItem
{
    public int Index { get; set; }
    public string Path { get; set; }
    public string Description { get; set; }
    public bool Reduce { get; set; }
}

public sealed class VerificationCoverageParallelSnapshot
{
    public int Index { get; set; }
    public string FullName { get; set; }
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public string Sha256 { get; set; }
}

public sealed class VerificationCoverageParallelLine
{
    public string Package { get; set; }
    public string File { get; set; }
    public int Line { get; set; }
    public int Hits { get; set; }
}

public sealed class VerificationCoverageParallelResult
{
    public VerificationCoverageParallelSnapshot[] Snapshots { get; set; }
    public VerificationCoverageParallelLine[] Lines { get; set; }
    public string[] Packages { get; set; }
}

internal sealed class VerificationCoverageParallelItemResult
{
    public VerificationCoverageParallelSnapshot Snapshot { get; set; }
    public Dictionary<string, Dictionary<string, Dictionary<int, int>>> Lines { get; private set; }
    public Exception Error { get; set; }

    public VerificationCoverageParallelItemResult()
    {
        Lines = new Dictionary<string, Dictionary<string, Dictionary<int, int>>>(StringComparer.Ordinal);
    }
}

public static class VerificationCoverageParallelProcessor
{
    public static VerificationCoverageParallelResult Process(
        VerificationCoverageParallelWorkItem[] items,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories,
        int maximumWorkers)
    {
        if (items == null || items.Length == 0) throw new ArgumentException("Coverage work must not be empty.", "items");
        if (maximumWorkers < 1 || maximumWorkers > 2) throw new ArgumentOutOfRangeException("maximumWorkers");
        var ordered = items.OrderBy(item => item.Index).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Index != index) throw new InvalidDataException("Coverage work item indexes must be contiguous and unique.");
        }

        var itemResults = new VerificationCoverageParallelItemResult[ordered.Length];
        var merged = new Dictionary<string, Dictionary<string, Dictionary<int, int>>>(StringComparer.Ordinal);
        Parallel.ForEach(
            ordered,
            new ParallelOptions { MaxDegreeOfParallelism = maximumWorkers },
            item =>
            {
                try
                {
                    var itemResult = ProcessItem(item, repositoryRoot, sourceProjectDirectories);
                    lock (merged)
                    {
                        Merge(itemResult.Lines, merged);
                    }
                    itemResult.Lines.Clear();
                    itemResults[item.Index] = itemResult;
                }
                catch (Exception exception) { itemResults[item.Index] = new VerificationCoverageParallelItemResult { Error = exception }; }
            });

        for (var index = 0; index < itemResults.Length; index++)
        {
            if (itemResults[index].Error != null)
            {
                throw new InvalidDataException(
                    string.Format(CultureInfo.InvariantCulture, "Coverage worker failure for '{0}': {1}", ordered[index].Path, itemResults[index].Error.Message),
                    itemResults[index].Error);
            }
        }

        var lines = new List<VerificationCoverageParallelLine>();
        foreach (var package in merged.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        foreach (var file in package.Value.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        foreach (var line in file.Value.OrderBy(entry => entry.Key))
        {
            lines.Add(new VerificationCoverageParallelLine
            {
                Package = package.Key,
                File = file.Key,
                Line = line.Key,
                Hits = line.Value
            });
        }

        return new VerificationCoverageParallelResult
        {
            Snapshots = itemResults.Select(result => result.Snapshot).ToArray(),
            Lines = lines.ToArray(),
            Packages = merged.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray()
        };
    }

    private static void Merge(
        Dictionary<string, Dictionary<string, Dictionary<int, int>>> source,
        Dictionary<string, Dictionary<string, Dictionary<int, int>>> destination)
    {
        foreach (var package in source)
        {
            Dictionary<string, Dictionary<int, int>> packageFiles;
            if (!destination.TryGetValue(package.Key, out packageFiles))
            {
                packageFiles = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
                destination.Add(package.Key, packageFiles);
            }
            foreach (var file in package.Value)
            {
                Dictionary<int, int> fileLines;
                if (!packageFiles.TryGetValue(file.Key, out fileLines))
                {
                    fileLines = new Dictionary<int, int>();
                    packageFiles.Add(file.Key, fileLines);
                }
                foreach (var line in file.Value)
                {
                    int existingHits;
                    if (!fileLines.TryGetValue(line.Key, out existingHits) || line.Value > existingHits)
                    {
                        fileLines[line.Key] = line.Value;
                    }
                }
            }
        }
    }

    private static VerificationCoverageParallelItemResult ProcessItem(
        VerificationCoverageParallelWorkItem item,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories)
    {
        var before = new FileInfo(item.Path);
        before.Refresh();
        if (!before.Exists) throw new FileNotFoundException(string.Format(CultureInfo.InvariantCulture, "{0} is missing or is not a leaf: {1}", item.Description, item.Path), item.Path);
        if (before.Length > int.MaxValue) throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "{0} exceeds the bounded report size: {1}", item.Description, item.Path));

        byte[] bytes = null;
        byte[] hash;
        using (var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan))
        {
            if (stream.Length != before.Length) throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "{0} changed before capture: {1}", item.Description, item.Path));
            using (var algorithm = SHA256.Create())
            {
                if (item.Reduce)
                {
                    bytes = new byte[(int)stream.Length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) throw new EndOfStreamException(string.Format(CultureInfo.InvariantCulture, "{0} ended before its declared length: {1}", item.Description, item.Path));
                        offset += read;
                    }
                    if (stream.ReadByte() != -1) throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "{0} grew while captured: {1}", item.Description, item.Path));
                    hash = algorithm.ComputeHash(bytes);
                }
                else
                {
                    hash = algorithm.ComputeHash(stream);
                }
            }
        }

        var after = new FileInfo(item.Path);
        after.Refresh();
        if (!after.Exists || before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "{0} changed while its immutable byte snapshot was captured: {1}", item.Description, item.Path));
        }

        var result = new VerificationCoverageParallelItemResult
        {
            Snapshot = new VerificationCoverageParallelSnapshot
            {
                Index = item.Index,
                FullName = after.FullName,
                Length = after.Length,
                LastWriteTimeUtc = after.LastWriteTimeUtc,
                Sha256 = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()
            }
        };
        if (bytes != null) Reduce(bytes, item, repositoryRoot, sourceProjectDirectories, result.Lines);
        return result;
    }

    private static void Reduce(
        byte[] bytes,
        VerificationCoverageParallelWorkItem item,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories,
        Dictionary<string, Dictionary<string, Dictionary<int, int>>> destination)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = XmlReader.Create(stream, settings))
            {
                if (reader.MoveToContent() != XmlNodeType.Element || !string.Equals(reader.LocalName, "coverage", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "{0} has an invalid document root: {1}", item.Description, item.Path));
                }

                ReadCoverage(reader, item, repositoryRoot, sourceProjectDirectories, destination);
                while (reader.Read()) { }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "{0} is malformed XML: {1}. {2}", item.Description, item.Path, exception.Message), exception);
        }
    }

    private static void ReadCoverage(
        XmlReader reader,
        VerificationCoverageParallelWorkItem item,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories,
        Dictionary<string, Dictionary<string, Dictionary<int, int>>> destination)
    {
        var coverageDepth = reader.Depth;
        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == coverageDepth + 1 && reader.NamespaceURI.Length == 0 && reader.LocalName == "packages")
            {
                ReadPackages(reader, item, repositoryRoot, sourceProjectDirectories, destination);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == coverageDepth) return;
        }
    }

    private static void ReadPackages(
        XmlReader reader,
        VerificationCoverageParallelWorkItem item,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories,
        Dictionary<string, Dictionary<string, Dictionary<int, int>>> destination)
    {
        var packagesDepth = reader.Depth;
        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == packagesDepth + 1 && reader.NamespaceURI.Length == 0 && reader.LocalName == "package")
            {
                ReadPackage(reader, item, repositoryRoot, sourceProjectDirectories, destination);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == packagesDepth) return;
        }
    }

    private static void ReadPackage(
        XmlReader reader,
        VerificationCoverageParallelWorkItem item,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories,
        Dictionary<string, Dictionary<string, Dictionary<int, int>>> destination)
    {
        var packageName = reader.GetAttribute("name") ?? "";
        Dictionary<string, Dictionary<int, int>> packageFiles;
        if (!sourceProjectDirectories.ContainsKey(packageName)) return;
        if (!destination.TryGetValue(packageName, out packageFiles))
        {
            packageFiles = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
            destination.Add(packageName, packageFiles);
        }

        var packageDepth = reader.Depth;
        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == packageDepth + 1 && reader.NamespaceURI.Length == 0 && reader.LocalName == "classes")
            {
                ReadClasses(reader, item, repositoryRoot, sourceProjectDirectories, packageName, packageFiles);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == packageDepth) return;
        }
    }

    private static void ReadClasses(
        XmlReader reader,
        VerificationCoverageParallelWorkItem item,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories,
        string packageName,
        Dictionary<string, Dictionary<int, int>> packageFiles)
    {
        var classesDepth = reader.Depth;
        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == classesDepth + 1 && reader.NamespaceURI.Length == 0 && reader.LocalName == "class")
            {
                ReadClass(reader, item, repositoryRoot, sourceProjectDirectories, packageName, packageFiles);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == classesDepth) return;
        }
    }

    private static void ReadClass(
        XmlReader reader,
        VerificationCoverageParallelWorkItem item,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories,
        string packageName,
        Dictionary<string, Dictionary<int, int>> packageFiles)
    {
        var fileName = reader.GetAttribute("filename") ?? "";
        var classDepth = reader.Depth;
        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == classDepth + 1 && reader.NamespaceURI.Length == 0 && reader.LocalName == "lines")
            {
                ReadLines(reader, item, repositoryRoot, sourceProjectDirectories, packageName, fileName, packageFiles);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == classDepth) return;
        }
    }

    private static void ReadLines(
        XmlReader reader,
        VerificationCoverageParallelWorkItem item,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories,
        string packageName,
        string fileName,
        Dictionary<string, Dictionary<int, int>> packageFiles)
    {
        var linesDepth = reader.Depth;
        Dictionary<int, int> fileLines = null;
        if (reader.IsEmptyElement) return;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == linesDepth + 1 && reader.NamespaceURI.Length == 0 && reader.LocalName == "line")
            {
                if (fileLines == null)
                {
                    var fileKey = GetFileKey(packageName, fileName, repositoryRoot, sourceProjectDirectories);
                    if (!packageFiles.TryGetValue(fileKey, out fileLines))
                    {
                        fileLines = new Dictionary<int, int>();
                        packageFiles.Add(fileKey, fileLines);
                    }
                }
                var lineNumber = int.Parse(reader.GetAttribute("number") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture);
                var hits = int.Parse(reader.GetAttribute("hits") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture);
                int existingHits;
                if (!fileLines.TryGetValue(lineNumber, out existingHits) || hits > existingHits) fileLines[lineNumber] = hits;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == linesDepth) return;
        }
    }

    private static string GetFileKey(
        string packageName,
        string fileName,
        string repositoryRoot,
        Dictionary<string, string> sourceProjectDirectories)
    {
        var normalized = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var sourceSegment = "src" + Path.DirectorySeparatorChar;
        var sourceIndex = normalized.IndexOf(sourceSegment, StringComparison.OrdinalIgnoreCase);
        string candidatePath;
        if (Path.IsPathRooted(normalized)) candidatePath = normalized;
        else if (normalized.StartsWith(sourceSegment, StringComparison.OrdinalIgnoreCase)) candidatePath = Path.Combine(repositoryRoot, normalized);
        else if (sourceIndex >= 0) candidatePath = Path.Combine(repositoryRoot, normalized.Substring(sourceIndex));
        else if (normalized.Length >= 3 && char.IsDigit(normalized[0]) && char.IsDigit(normalized[1]) && normalized[2] == '-') candidatePath = Path.Combine(repositoryRoot, "src", normalized);
        else if (normalized.StartsWith(packageName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && sourceProjectDirectories.ContainsKey(packageName))
        {
            var projectDirectory = sourceProjectDirectories[packageName];
            candidatePath = Path.Combine(projectDirectory, normalized.Substring(packageName.Length + 1));
        }
        else candidatePath = Path.Combine(repositoryRoot, normalized);
        return Path.GetFullPath(candidatePath).ToUpperInvariant();
    }
}
'@
}

function Invoke-VerificationCoverageWorkers {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$WorkItems,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [Collections.Generic.Dictionary[string, string]]$SourceProjectDirectories,
        [ValidateRange(1, 2)] [int]$MaximumWorkers = 2
    )

    if ($WorkItems.Count -eq 0) { return [pscustomobject]@{ Snapshots = @(); Lines = @(); Packages = @() } }
    if ($MaximumWorkers -eq 1 -or $WorkItems.Count -lt 4) {
        $snapshots = [Collections.Generic.List[object]]::new()
        $packageFileLines = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $paths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
        for ($index = 0; $index -lt $WorkItems.Count; $index++) {
            $item = $WorkItems[$index]
            $path = [IO.Path]::GetFullPath([string]$item.Path)
            if (-not $paths.Add($path)) { throw "Coverage worker input contains a duplicate path: $path" }
            if ([bool]$item.Reduce) {
                $reduction = Read-VerificationCoverageReductionSnapshot -Path $path -Root ([string]$item.Root) -Description ([string]$item.Description) -RepositoryRoot $RepositoryRoot -SourceProjectDirectories $SourceProjectDirectories -Destination $packageFileLines
                $snapshot = $reduction.Snapshot
            }
            else {
                $snapshot = Read-VerificationCoverageHashSnapshot -Path $path -Root ([string]$item.Root) -Description ([string]$item.Description)
            }
            $snapshot | Add-Member -NotePropertyName Index -NotePropertyValue $index
            $snapshots.Add($snapshot)
        }

        $lines = [Collections.Generic.List[object]]::new()
        foreach ($packageEntry in @($packageFileLines.GetEnumerator() | Sort-Object Key -CaseSensitive)) {
            foreach ($fileEntry in @($packageEntry.Value.GetEnumerator() | Sort-Object Key -CaseSensitive)) {
                foreach ($lineEntry in @($fileEntry.Value.GetEnumerator() | Sort-Object Key)) {
                    $lines.Add([pscustomobject]@{ Package = $packageEntry.Key; File = $fileEntry.Key; Line = $lineEntry.Key; Hits = $lineEntry.Value })
                }
            }
        }
        return [pscustomobject]@{
            Snapshots = @($snapshots)
            Lines = @($lines)
            Packages = @($packageFileLines.Keys | Sort-Object)
        }
    }

    Initialize-VerificationCoverageParallelProcessor
    $typedItems = [Collections.Generic.List[VerificationCoverageParallelWorkItem]]::new()
    $roots = [Collections.Generic.List[string]]::new()
    $paths = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    for ($index = 0; $index -lt $WorkItems.Count; $index++) {
        $item = $WorkItems[$index]
        $path = [IO.Path]::GetFullPath([string]$item.Path)
        $root = [IO.Path]::GetFullPath([string]$item.Root)
        if ([string]::IsNullOrWhiteSpace([string]$item.Description)) { throw "Coverage worker item $index is missing its description." }
        if (-not $paths.Add($path)) { throw "Coverage worker input contains a duplicate path: $path" }
        [void](Assert-VerificationCoverageOrdinaryPath -Path $path -Root $root -PathType Leaf -Description ([string]$item.Description))
        $typedItems.Add([VerificationCoverageParallelWorkItem]@{
            Index = $index
            Path = $path
            Description = [string]$item.Description
            Reduce = [bool]$item.Reduce
        })
        $roots.Add($root)
    }

    $result = [VerificationCoverageParallelProcessor]::Process($typedItems.ToArray(), [IO.Path]::GetFullPath($RepositoryRoot), $SourceProjectDirectories, $MaximumWorkers)
    foreach ($snapshot in $result.Snapshots) {
        $after = Assert-VerificationCoverageOrdinaryPath -Path $snapshot.FullName -Root $roots[$snapshot.Index] -PathType Leaf -Description ([string]$WorkItems[$snapshot.Index].Description)
        if ($after.Length -ne $snapshot.Length -or $after.LastWriteTimeUtc -ne $snapshot.LastWriteTimeUtc) {
            throw "Coverage worker result changed before reconciliation: $($snapshot.FullName)"
        }
    }
    return [pscustomobject]@{
        Snapshots = @($result.Snapshots | Sort-Object Index)
        Lines = @($result.Lines)
        Packages = @($result.Packages)
    }
}
