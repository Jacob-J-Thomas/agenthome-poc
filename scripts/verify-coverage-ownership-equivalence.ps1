param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$UnfilteredResultsRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$FilteredResultsRoot,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReportPath,

    [ValidateRange(1, 2)]
    [int]$MaximumCoverageWorkers = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testsRoot = Join-Path $repoRoot "tests"
$ownershipManifestPath = Join-Path $testsRoot "verification-coverage-ownership.json"
$canonicalRunSettingsPath = Join-Path $testsRoot "verification-pull-request.runsettings"

. (Join-Path $PSScriptRoot "verification-coverage-evidence.ps1")
. (Join-Path $PSScriptRoot "verification-test-lanes.ps1")

function Assert-EquivalenceProperties {
    param(
        [Parameter(Mandatory = $true)] [object]$Value,
        [Parameter(Mandatory = $true)] [string[]]$Expected,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    Assert-VerificationCoverageOwnershipProperties -Value $Value -Expected $Expected -Description $Description
}

function Read-EquivalenceJson {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Root,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $snapshot = Read-VerificationCoverageSnapshot -Path $Path -Root $Root -Description $Description
    $document = $null
    try {
        $json = [Text.UTF8Encoding]::new($false, $true).GetString([byte[]]$snapshot.Bytes)
        $document = [Text.Json.JsonDocument]::Parse($json)
        Assert-VerificationCoverageOwnershipJsonPropertiesUnique -Element $document.RootElement -Path '$'
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw "$Description root must be a JSON object."
        }
        $value = ConvertFrom-Json -InputObject $json
    }
    catch {
        throw "$Description is corrupt: $Path. $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) { $document.Dispose() }
    }

    return [pscustomobject]@{ Json = $json; Snapshot = $snapshot; Value = $value }
}

function Resolve-EquivalenceManifestRelativePath {
    param(
        [Parameter(Mandatory = $true)] [string]$RelativePath,
        [Parameter(Mandatory = $true)] [string]$Root,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $segments = @($RelativePath.Split('/'))
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.Contains('\', [StringComparison]::Ordinal) -or
        $RelativePath.Contains(':', [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($RelativePath) -or
        @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -ceq "." -or $_ -ceq ".." }).Count -ne 0) {
        throw "$Description is not a canonical safe relative path."
    }

    $fullRoot = [IO.Path]::GetFullPath($Root)
    $resolved = [IO.Path]::GetFullPath((Join-Path $fullRoot $RelativePath))
    if (-not (Test-VerificationCoverageDescendantPath -Path $resolved -Root $fullRoot)) {
        throw "$Description escapes its declared root."
    }
    $canonicalRelativePath = [IO.Path]::GetRelativePath($fullRoot, $resolved).Replace('\', '/')
    if ($canonicalRelativePath -cne $RelativePath) {
        throw "$Description is not in canonical relative-path form."
    }
    return $resolved
}

function Read-EquivalenceContext {
    param(
        [Parameter(Mandatory = $true)] [string]$ResultsRoot,
        [Parameter(Mandatory = $true)] [string]$ExpectedMode,
        [Parameter(Mandatory = $true)] [object]$Ownership,
        [Parameter(Mandatory = $true)] [string]$HeadSha,
        [Parameter(Mandatory = $true)] [string]$Platform
    )

    $path = Join-Path $ResultsRoot "coverage-ownership-evidence-context.json"
    $read = Read-EquivalenceJson -Path $path -Root $ResultsRoot -Description "$ExpectedMode evidence context"
    $context = $read.Value
    Assert-EquivalenceProperties -Value $context -Expected @(
        "schemaVersion", "mode", "headSha", "platform", "collectorVersion", "ownershipSha256", "runSettingsSha256"
    ) -Description "$ExpectedMode evidence context"
    if (($context.schemaVersion -isnot [long]) -or [long]$context.schemaVersion -ne 1 -or
        [string]$context.mode -cne $ExpectedMode -or
        [string]$context.headSha -cne $HeadSha -or
        [string]$context.platform -cne $Platform -or
        [string]$context.collectorVersion -cne $Ownership.CollectorVersion -or
        [string]$context.ownershipSha256 -cne $Ownership.OwnershipSha256 -or
        [string]$context.runSettingsSha256 -cne $Ownership.RunSettingsSha256) {
        throw "$ExpectedMode evidence context does not bind the exact current clean head, platform, ownership map, collector, and runsettings."
    }

    return $read.Snapshot
}

function Read-EquivalenceExpectedInventory {
    param([Parameter(Mandatory = $true)] [string]$ResultsRoot)

    $path = Join-Path $ResultsRoot "required-execution-tests.json"
    $read = Read-EquivalenceJson -Path $path -Root $ResultsRoot -Description "Required execution inventory"
    $inventory = $read.Value
    Assert-EquivalenceProperties -Value $inventory -Expected @("schemaVersion", "tests", "totalTests") -Description "Required execution inventory"
    $tests = @($inventory.tests)
    if (($inventory.schemaVersion -isnot [long]) -or [long]$inventory.schemaVersion -ne 1 -or
        ($inventory.totalTests -isnot [long]) -or [long]$inventory.totalTests -lt 1 -or
        $tests.Count -ne [long]$inventory.totalTests) {
        throw "Required execution inventory is empty or malformed: $path"
    }

    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $idsByLane = [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::Ordinal)
    foreach ($test in $tests) {
        Assert-EquivalenceProperties -Value $test -Expected @("id", "xunitTestCaseUniqueId", "fullyQualifiedName", "displayName", "source", "lane") -Description "Required execution test"
        $id = [Guid]::Empty
        if (-not [Guid]::TryParseExact([string]$test.id, "D", [ref]$id) -or
            [string]::IsNullOrWhiteSpace([string]$test.xunitTestCaseUniqueId) -or
            [string]::IsNullOrWhiteSpace([string]$test.fullyQualifiedName) -or
            [string]::IsNullOrWhiteSpace([string]$test.lane) -or
            -not $ids.Add($id.ToString("D"))) {
            throw "Required execution inventory contains an invalid or duplicate exact TestCase.Id."
        }
        $lane = [string]$test.lane
        if (-not $idsByLane.ContainsKey($lane)) { $idsByLane.Add($lane, [Collections.Generic.List[string]]::new()) }
        $idsByLane[$lane].Add($id.ToString("D"))
    }

    return [pscustomobject]@{
        Ids = @($ids | Sort-Object -CaseSensitive)
        IdsByLane = $idsByLane
        Snapshot = $read.Snapshot
    }
}

function Read-EquivalenceTrx {
    param(
        [Parameter(Mandatory = $true)] [object]$Snapshot,
        [Parameter(Mandatory = $true)] [string]$LaneName
    )

    $trx = ConvertFrom-VerificationCoverageXmlSnapshot -Snapshot $Snapshot -Description "Coverage equivalence lane '$LaneName' TRX"
    $namespaceUri = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
    if ($null -eq $trx.DocumentElement -or $trx.DocumentElement.LocalName -cne "TestRun" -or $trx.DocumentElement.NamespaceURI -cne $namespaceUri) {
        throw "Coverage equivalence lane '$LaneName' has an invalid TRX root."
    }
    $namespace = [Xml.XmlNamespaceManager]::new($trx.NameTable)
    $namespace.AddNamespace("t", $namespaceUri)
    $nodes = @($trx.SelectNodes("/t:TestRun/t:Results/t:UnitTestResult", $namespace))
    if ($nodes.Count -eq 0) { throw "Coverage equivalence lane '$LaneName' contains no executed tests." }

    $ids = [Collections.Generic.List[string]]::new()
    $executionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $nodes) {
        $testId = [Guid]::Empty
        $executionId = [Guid]::Empty
        if (-not [Guid]::TryParseExact([string]$node.testId, "D", [ref]$testId) -or
            -not [Guid]::TryParseExact([string]$node.executionId, "D", [ref]$executionId) -or
            -not $executionIds.Add($executionId.ToString("D"))) {
            throw "Coverage equivalence lane '$LaneName' contains an invalid or duplicate execution identity."
        }
        if ([string]$node.outcome -cne "Passed") {
            throw "Coverage equivalence lane '$LaneName' contains non-passing test '$([string]$node.testName)' with outcome '$([string]$node.outcome)'."
        }
        $ids.Add($testId.ToString("D"))
    }
    return @($ids)
}

function Read-EquivalenceBinaryEntries {
    param(
        [Parameter(Mandatory = $true)] [object[]]$Entries,
        [Parameter(Mandatory = $true)] [string]$BinaryRoot,
        [Parameter(Mandatory = $true)] [string]$ResultsRoot,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    return Read-VerificationCoverageBinaryInventoryEvidence -Entries $Entries -BinaryRoot $BinaryRoot -ResultsRoot $ResultsRoot -Description $Description
}

function Read-EquivalenceBinaryManifest {
    param(
        [Parameter(Mandatory = $true)] [string]$ResultsRoot,
        [Parameter(Mandatory = $true)] [string]$ExpectedMode,
        [Parameter(Mandatory = $true)] [string]$HeadSha,
        [Parameter(Mandatory = $true)] [IO.FileInfo[]]$TestProjects
    )

    $path = Join-Path $ResultsRoot "coverage-ownership-binary-manifest.json"
    $read = Read-EquivalenceJson -Path $path -Root $ResultsRoot -Description "$ExpectedMode binary manifest"
    $typeDocument = $null
    try {
        $typeDocument = [Text.Json.JsonDocument]::Parse($read.Json)
        $typeRoot = $typeDocument.RootElement
        if ($typeRoot.GetProperty("schemaVersion").ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            $typeRoot.GetProperty("mode").ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $typeRoot.GetProperty("headSha").ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $typeRoot.GetProperty("projects").ValueKind -ne [Text.Json.JsonValueKind]::Array) {
            throw "$ExpectedMode binary manifest has invalid root JSON value types."
        }
        foreach ($projectElement in $typeRoot.GetProperty("projects").EnumerateArray()) {
            if ($projectElement.ValueKind -ne [Text.Json.JsonValueKind]::Object -or
                $projectElement.GetProperty("project").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                $projectElement.GetProperty("canonicalRoot").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                $projectElement.GetProperty("canonicalBinaries").ValueKind -ne [Text.Json.JsonValueKind]::Array -or
                $projectElement.GetProperty("parentSettingsPath").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                $projectElement.GetProperty("childSettingsPath").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                $projectElement.GetProperty("settingsSha256").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                $projectElement.GetProperty("childInvocations").ValueKind -ne [Text.Json.JsonValueKind]::Array) {
                throw "$ExpectedMode binary manifest has invalid project JSON value types."
            }
            foreach ($binaryElement in $projectElement.GetProperty("canonicalBinaries").EnumerateArray()) {
                if ($binaryElement.ValueKind -ne [Text.Json.JsonValueKind]::Object -or
                    $binaryElement.GetProperty("path").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                    $binaryElement.GetProperty("length").ValueKind -ne [Text.Json.JsonValueKind]::Number -or
                    $binaryElement.GetProperty("sha256").ValueKind -ne [Text.Json.JsonValueKind]::String) {
                    throw "$ExpectedMode binary manifest has invalid canonical binary JSON value types."
                }
            }
            foreach ($invocationElement in $projectElement.GetProperty("childInvocations").EnumerateArray()) {
                if ($invocationElement.ValueKind -ne [Text.Json.JsonValueKind]::Object -or
                    $invocationElement.GetProperty("relativeRoot").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                    $invocationElement.GetProperty("binarySha256").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                    $invocationElement.GetProperty("binaries").ValueKind -ne [Text.Json.JsonValueKind]::Array) {
                    throw "$ExpectedMode binary manifest has invalid child invocation JSON value types."
                }
                foreach ($binaryElement in $invocationElement.GetProperty("binaries").EnumerateArray()) {
                    if ($binaryElement.ValueKind -ne [Text.Json.JsonValueKind]::Object -or
                        $binaryElement.GetProperty("path").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                        $binaryElement.GetProperty("length").ValueKind -ne [Text.Json.JsonValueKind]::Number -or
                        $binaryElement.GetProperty("sha256").ValueKind -ne [Text.Json.JsonValueKind]::String) {
                        throw "$ExpectedMode binary manifest has invalid child binary JSON value types."
                    }
                }
            }
        }
    }
    catch {
        throw "$ExpectedMode binary manifest is not strict schema-1 JSON: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $typeDocument) { $typeDocument.Dispose() }
    }
    $manifest = $read.Value
    Assert-EquivalenceProperties -Value $manifest -Expected @("schemaVersion", "mode", "headSha", "projects") -Description "$ExpectedMode binary manifest"
    $projects = @($manifest.projects)
    if (($manifest.schemaVersion -isnot [long]) -or [long]$manifest.schemaVersion -ne 1 -or
        [string]$manifest.mode -cne $ExpectedMode -or [string]$manifest.headSha -cne $HeadSha -or
        $projects.Count -ne $TestProjects.Count) {
        throw "$ExpectedMode binary manifest does not bind the exact current head and test-project inventory."
    }

    $expectedProjectNames = @($TestProjects.BaseName | Sort-Object -CaseSensitive)
    $actualProjectNames = @($projects.project | Sort-Object -CaseSensitive)
    if ($actualProjectNames.Count -ne $expectedProjectNames.Count -or
        @(Compare-Object -ReferenceObject $expectedProjectNames -DifferenceObject $actualProjectNames -CaseSensitive).Count -ne 0) {
        throw "$ExpectedMode binary manifest does not classify every canonical test project exactly once."
    }

    $summaries = [Collections.Generic.List[object]]::new()
    foreach ($project in @($projects | Sort-Object project -CaseSensitive)) {
        Assert-EquivalenceProperties -Value $project -Expected @(
            "project", "canonicalRoot", "canonicalBinaries", "parentSettingsPath", "childSettingsPath", "settingsSha256", "childInvocations"
        ) -Description "$ExpectedMode binary project"
        $projectName = [string]$project.project
        $projectIsolationRoot = Join-Path (Join-Path $ResultsRoot "CoverageIsolation") $projectName
        $releaseRoot = Join-Path (Join-Path (Join-Path $projectIsolationRoot "canonical") "bin") "Release"
        $expectedCanonicalRoot = Join-Path $releaseRoot "net10.0"
        $expectedParentSettingsPath = Join-Path $projectIsolationRoot "verification-pull-request.runsettings"
        $expectedChildSettingsPath = Join-Path $releaseRoot "verification-pull-request.runsettings"
        $expectedInvocationsRoot = Join-Path $releaseRoot "Invocations"

        $canonicalRoot = Resolve-EquivalenceManifestRelativePath -RelativePath ([string]$project.canonicalRoot) -Root $ResultsRoot -Description "$ExpectedMode binary manifest canonical root for '$projectName'"
        if (-not (Test-VerificationCoverageSamePath -Left $canonicalRoot -Right $expectedCanonicalRoot)) {
            throw "$ExpectedMode binary manifest canonical root is not the exact Release/net10.0 pristine output for '$projectName'."
        }
        $canonical = Read-EquivalenceBinaryEntries -Entries @($project.canonicalBinaries) -BinaryRoot $canonicalRoot -ResultsRoot $ResultsRoot -Description "$ExpectedMode '$projectName' canonical"

        $parentSettingsPath = Resolve-EquivalenceManifestRelativePath -RelativePath ([string]$project.parentSettingsPath) -Root $ResultsRoot -Description "$ExpectedMode binary manifest parent settings for '$projectName'"
        $childSettingsPath = Resolve-EquivalenceManifestRelativePath -RelativePath ([string]$project.childSettingsPath) -Root $ResultsRoot -Description "$ExpectedMode binary manifest child settings for '$projectName'"
        if (-not (Test-VerificationCoverageSamePath -Left $parentSettingsPath -Right $expectedParentSettingsPath) -or
            -not (Test-VerificationCoverageSamePath -Left $childSettingsPath -Right $expectedChildSettingsPath) -or
            [string]$project.settingsSha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw "$ExpectedMode binary manifest has unsafe parent/child settings provenance for '$projectName'."
        }
        $parentSettings = Read-VerificationCoverageHashSnapshot -Path $parentSettingsPath -Root $ResultsRoot -Description "$ExpectedMode '$projectName' parent settings"
        $childSettings = Read-VerificationCoverageHashSnapshot -Path $childSettingsPath -Root $ResultsRoot -Description "$ExpectedMode '$projectName' child settings"
        if ($parentSettings.Sha256 -cne [string]$project.settingsSha256 -or $childSettings.Sha256 -cne $parentSettings.Sha256 -or $childSettings.Length -ne $parentSettings.Length) {
            throw "$ExpectedMode binary manifest parent/child settings evidence differs for '$projectName'."
        }

        $invocationDigests = [Collections.Generic.List[string]]::new()
        $invocationRoots = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
        foreach ($invocation in @($project.childInvocations)) {
            Assert-EquivalenceProperties -Value $invocation -Expected @("relativeRoot", "binarySha256", "binaries") -Description "$ExpectedMode '$projectName' child invocation"
            $invocationRoot = Resolve-EquivalenceManifestRelativePath -RelativePath ([string]$invocation.relativeRoot) -Root $ResultsRoot -Description "$ExpectedMode '$projectName' child invocation root"
            if (-not (Test-VerificationCoverageDescendantPath -Path $invocationRoot -Root $expectedInvocationsRoot) -or
                -not $invocationRoots.Add($invocationRoot) -or [string]$invocation.binarySha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw "$ExpectedMode binary manifest has unsafe or duplicate child invocation provenance for '$projectName'."
            }
            $invocationId = [Guid]::Empty
            if (-not [Guid]::TryParseExact((Split-Path -Leaf $invocationRoot), "N", [ref]$invocationId)) {
                throw "$ExpectedMode binary manifest has an invalid child invocation identity for '$projectName'."
            }
            $invocationEvidence = Read-EquivalenceBinaryEntries -Entries @($invocation.binaries) -BinaryRoot $invocationRoot -ResultsRoot $ResultsRoot -Description "$ExpectedMode '$projectName' child invocation"
            if ($invocationEvidence.Sha256 -cne [string]$invocation.binarySha256) {
                throw "$ExpectedMode binary manifest child invocation digest differs for '$projectName'."
            }
            $invocationDigests.Add("$($invocationEvidence.Count)" + [char]0 + $invocationEvidence.Sha256)
        }
        $actualInvocationRoots = if (Test-Path -LiteralPath $expectedInvocationsRoot -PathType Container) {
            [void](Assert-VerificationCoverageOrdinaryPath -Path $expectedInvocationsRoot -Root $ResultsRoot -PathType Container -Description "$ExpectedMode '$projectName' child invocation container")
            $children = @(Get-ChildItem -LiteralPath $expectedInvocationsRoot -Force)
            if (@($children | Where-Object { -not $_.PSIsContainer }).Count -ne 0) {
                throw "$ExpectedMode binary manifest child invocation container has an unexpected file for '$projectName'."
            }
            foreach ($child in $children) {
                [void](Assert-VerificationCoverageOrdinaryPath -Path $child.FullName -Root $expectedInvocationsRoot -PathType Container -Description "$ExpectedMode '$projectName' child invocation directory")
            }
            @($children)
        }
        else { @() }
        $actualInvocationRootSet = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
        foreach ($actualInvocationRoot in $actualInvocationRoots) { [void]$actualInvocationRootSet.Add([IO.Path]::GetFullPath($actualInvocationRoot.FullName)) }
        if (-not $actualInvocationRootSet.SetEquals($invocationRoots)) {
            throw "$ExpectedMode binary manifest omits or adds a child invocation directory for '$projectName'."
        }

        $summaries.Add([pscustomobject][ordered]@{
            project = $projectName
            canonicalBinaryCount = $canonical.Count
            canonicalBinarySha256 = $canonical.Sha256
            childInvocationCount = $invocationDigests.Count
            childInvocationSha256 = Get-VerificationCoverageOwnershipRecordSha256 -Records @($invocationDigests)
            settingsSha256 = $parentSettings.Sha256
        })
    }
    return [pscustomobject]@{ Snapshot = $read.Snapshot; Projects = @($summaries) }
}

function Get-EquivalencePackageEvidence {
    param(
        [Parameter(Mandatory = $true)] [object]$Ownership,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$Lines
    )

    $summaries = [Collections.Generic.List[object]]::new()
    foreach ($owner in @($Ownership.Owners | Sort-Object Package -CaseSensitive)) {
        $packageLines = @($Lines | Where-Object Package -CEQ $owner.Package)
        if ($packageLines.Count -eq 0) {
            throw "Coverage equivalence contains no executable lines for production package '$($owner.Package)'."
        }
        $lineSet = Get-VerificationCoverageLineSetEvidence -Ownership $Ownership -RepositoryRoot $repoRoot -Lines $packageLines
        $rate = [decimal]$lineSet.HitLineCount / [decimal]$lineSet.CoverableLineCount
        if ($rate -lt [decimal]0.90) {
            throw "Coverage equivalence package '$($owner.Package)' is below the unchanged 90% line threshold."
        }
        $summaries.Add([pscustomobject][ordered]@{
            package = $owner.Package
            coverableLineCount = $lineSet.CoverableLineCount
            coverableLineSha256 = $lineSet.CoverableLineSha256
            hitLineCount = $lineSet.HitLineCount
            hitLineSha256 = $lineSet.HitLineSha256
            lineRate = $rate.ToString("0.000000", [Globalization.CultureInfo]::InvariantCulture)
        })
    }
    return @($summaries)
}

function Read-EquivalenceResults {
    param(
        [Parameter(Mandatory = $true)] [string]$ResultsRoot,
        [Parameter(Mandatory = $true)] [ValidateSet("UnfilteredEvidence", "FilteredEvidence")] [string]$Mode,
        [Parameter(Mandatory = $true)] [object]$Ownership,
        [Parameter(Mandatory = $true)] [IO.FileInfo[]]$TestProjects,
        [Parameter(Mandatory = $true)] [string]$HeadSha,
        [Parameter(Mandatory = $true)] [string]$Platform,
        [Parameter(Mandatory = $true)] [Collections.Generic.Dictionary[string, string]]$SourceProjectDirectories,
        [Parameter(Mandatory = $true)] [string]$SettingsProbeRoot
    )

    $fullRoot = [IO.Path]::GetFullPath($ResultsRoot)
    [void](Assert-VerificationCoverageOrdinaryPath -Path $fullRoot -Root $fullRoot -PathType Container -Description "$Mode results root")
    $contextSnapshot = Read-EquivalenceContext -ResultsRoot $fullRoot -ExpectedMode $Mode -Ownership $Ownership -HeadSha $HeadSha -Platform $Platform
    $inventory = Read-EquivalenceExpectedInventory -ResultsRoot $fullRoot
    $binaryEvidence = Read-EquivalenceBinaryManifest -ResultsRoot $fullRoot -ExpectedMode $Mode -HeadSha $HeadSha -TestProjects $TestProjects
    $standardRoot = Join-Path $fullRoot "StandardTests"
    [void](Assert-VerificationCoverageOrdinaryPath -Path $standardRoot -Root $fullRoot -PathType Container -Description "$Mode standard test results root")
    $coverageLaneBindings = Get-VerificationCoverageLaneBindings -TestProjects $TestProjects
    $expectedLaneNames = @($coverageLaneBindings.Keys | ForEach-Object {
        if (-not $_.StartsWith("tests-", [StringComparison]::Ordinal)) {
            throw "$Mode checked-in coverage lane has an invalid profile name: $_"
        }
        $_.Substring("tests-".Length)
    } | Sort-Object -CaseSensitive)
    $actualLaneNames = @(Get-ChildItem -LiteralPath $standardRoot -Directory -Force | ForEach-Object Name | Sort-Object -CaseSensitive)
    if ($actualLaneNames.Count -ne $expectedLaneNames.Count -or
        @(Compare-Object -ReferenceObject $expectedLaneNames -DifferenceObject $actualLaneNames -CaseSensitive).Count -ne 0) {
        throw "$Mode results do not contain the exact checked-in $($expectedLaneNames.Count)-lane directory inventory."
    }

    $workItems = [Collections.Generic.List[object]]::new()
    $projectByReportPath = [Collections.Generic.Dictionary[string, string]]::new((Get-VerificationCoveragePathComparer))
    $accountedReports = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    $testIds = [Collections.Generic.List[string]]::new()
    $laneTestIdEvidence = [Collections.Generic.List[object]]::new()
    $trxEvidence = [Collections.Generic.List[object]]::new()
    $reportEvidence = [Collections.Generic.List[object]]::new()
    $canonicalSettingsHash = (Get-FileHash -LiteralPath $canonicalRunSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    foreach ($laneName in $expectedLaneNames) {
        $profileName = "tests-$laneName"
        $projectName = Get-VerificationCoverageLaneTestProjectName -Bindings $coverageLaneBindings -LaneName $profileName
        $laneRoot = Join-Path $standardRoot $laneName
        [void](Assert-VerificationCoverageOrdinaryPath -Path $laneRoot -Root $fullRoot -PathType Container -Description "$Mode lane '$laneName' root")
        $trxPath = Join-Path $laneRoot "$laneName.trx"
        $trxSnapshot = Read-VerificationCoverageSnapshot -Path $trxPath -Root $laneRoot -Description "$Mode lane '$laneName' exact TRX"
        $laneTestIds = @(Read-EquivalenceTrx -Snapshot $trxSnapshot -LaneName $laneName | Sort-Object -CaseSensitive)
        if (-not $inventory.IdsByLane.ContainsKey($laneName)) {
            throw "$Mode canonical discovery inventory does not classify lane '$laneName'."
        }
        $expectedLaneTestIds = @($inventory.IdsByLane[$laneName] | Sort-Object -CaseSensitive)
        if (@($laneTestIds | Group-Object -CaseSensitive | Where-Object Count -ne 1).Count -ne 0 -or
            $laneTestIds.Count -ne $expectedLaneTestIds.Count -or
            @(Compare-Object -ReferenceObject $expectedLaneTestIds -DifferenceObject $laneTestIds -CaseSensitive).Count -ne 0) {
            throw "$Mode lane '$laneName' executed TestCase.Id inventory does not exactly equal its canonical discovery assignment."
        }
        foreach ($testId in $laneTestIds) { $testIds.Add($testId) }
        $trxEvidence.Add([pscustomobject][ordered]@{ lane = $laneName; length = $trxSnapshot.Length; sha256 = $trxSnapshot.Sha256 })
        $laneTestIdEvidence.Add([pscustomobject][ordered]@{
            lane = $laneName
            testCaseCount = $laneTestIds.Count
            testCaseIdSha256 = Get-VerificationCoverageOwnershipRecordSha256 -Records $laneTestIds -Presorted
        })

        $deploymentName = Get-VerificationCoverageDeploymentRoot -LaneName "tests-$laneName" -TrxSnapshot $trxSnapshot
        $deploymentRoot = Join-Path $laneRoot $deploymentName
        $laneReports = @(Get-ChildItem -LiteralPath $laneRoot -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)
        $canonicalReports = @($laneReports | Where-Object { -not (Test-VerificationCoverageDescendantPath -Path $_.FullName -Root $deploymentRoot) })
        $aliases = @($laneReports | Where-Object { Test-VerificationCoverageDescendantPath -Path $_.FullName -Root $deploymentRoot })
        if ($canonicalReports.Count -ne 1 -or $aliases.Count -gt 1) {
            throw "$Mode lane '$laneName' must contain one canonical coverage report and at most one staging alias."
        }
        $canonicalPath = [IO.Path]::GetFullPath($canonicalReports[0].FullName)
        Assert-VerificationCoverageCollectorPath -Path $canonicalPath -CollectorRoot $laneRoot -Description "$Mode lane '$laneName' canonical report"
        if (-not $accountedReports.Add($canonicalPath)) { throw "$Mode coverage report inventory contains a duplicate canonical path." }
        $projectByReportPath.Add($canonicalPath, $projectName)
        $workItems.Add([pscustomobject]@{ Path = $canonicalPath; Root = $fullRoot; Description = "$Mode lane '$laneName' canonical report"; Reduce = $true })
        foreach ($alias in $aliases) {
            if (-not (Test-VerificationCoverageStagingAliasPath -Path $alias.FullName -DeploymentRoot $deploymentRoot) -or -not $accountedReports.Add($alias.FullName)) {
                throw "$Mode lane '$laneName' contains an invalid or duplicate staging alias."
            }
            $canonicalSnapshot = Read-VerificationCoverageHashSnapshot -Path $canonicalPath -Root $fullRoot -Description "$Mode lane '$laneName' canonical report"
            $aliasSnapshot = Read-VerificationCoverageHashSnapshot -Path $alias.FullName -Root $fullRoot -Description "$Mode lane '$laneName' staging alias"
            if ($canonicalSnapshot.Length -ne $aliasSnapshot.Length -or $canonicalSnapshot.Sha256 -cne $aliasSnapshot.Sha256) {
                throw "$Mode lane '$laneName' staging alias does not byte-match its canonical report."
            }
        }
    }

    foreach ($testProject in $TestProjects) {
        $projectName = $testProject.BaseName
        $isolationRoot = Join-Path (Join-Path $fullRoot "CoverageIsolation") $projectName
        [void](Assert-VerificationCoverageOrdinaryPath -Path $isolationRoot -Root $fullRoot -PathType Container -Description "$Mode project '$projectName' coverage isolation root")
        $laneSettingsPath = Join-Path $isolationRoot "verification-pull-request.runsettings"
        $laneSettings = Read-VerificationCoverageHashSnapshot -Path $laneSettingsPath -Root $fullRoot -Description "$Mode project '$projectName' runsettings"
        $childSettings = @(Get-ChildItem -LiteralPath (Join-Path $isolationRoot "canonical/bin") -Recurse -Filter "verification-pull-request.runsettings" -File)
        if ($childSettings.Count -ne 1) { throw "$Mode project '$projectName' must contain one exact child-process runsettings copy." }
        $childSettingsSnapshot = Read-VerificationCoverageHashSnapshot -Path $childSettings[0].FullName -Root $fullRoot -Description "$Mode project '$projectName' child runsettings"
        if ($laneSettings.Length -ne $childSettingsSnapshot.Length -or $laneSettings.Sha256 -cne $childSettingsSnapshot.Sha256) {
            throw "$Mode project '$projectName' child-process runsettings do not byte-match the parent settings."
        }
        if ($Mode -ceq "UnfilteredEvidence") {
            if ($laneSettings.Sha256 -cne $canonicalSettingsHash) { throw "$Mode project '$projectName' is not exact canonical unfiltered collection." }
        }
        else {
            $expectedSettingsPath = Join-Path $SettingsProbeRoot "$projectName.runsettings"
            $selection = Get-VerificationCoverageSelection -Ownership $Ownership -TestProject $testProject
            Write-VerificationCoverageRunSettings -SourcePath $canonicalRunSettingsPath -DestinationPath $expectedSettingsPath -Selection $selection
            $expectedSettings = Read-VerificationCoverageHashSnapshot -Path $expectedSettingsPath -Root $SettingsProbeRoot -Description "$Mode project '$projectName' expected runsettings"
            if ($laneSettings.Length -ne $expectedSettings.Length -or $laneSettings.Sha256 -cne $expectedSettings.Sha256) {
                throw "$Mode project '$projectName' does not use the exact current ownership-derived filter."
            }
        }

        foreach ($childReport in @(Get-ChildItem -LiteralPath $isolationRoot -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)) {
            $childRoot = Split-Path -Parent (Split-Path -Parent $childReport.FullName)
            Assert-VerificationCoverageChildProvenance -ProjectName $projectName -ChildResultsRoot $childRoot -ReportPath $childReport.FullName -ResultsRoot $fullRoot -RepositoryRoot $repoRoot
            if (-not $accountedReports.Add($childReport.FullName)) { throw "$Mode coverage report inventory contains a duplicate child path." }
            $projectByReportPath.Add([IO.Path]::GetFullPath($childReport.FullName), $projectName)
            $workItems.Add([pscustomobject]@{ Path = $childReport.FullName; Root = $fullRoot; Description = "$Mode child report for '$projectName'"; Reduce = $true })
        }
    }

    $actualReports = [Collections.Generic.HashSet[string]]::new((Get-VerificationCoveragePathComparer))
    foreach ($report in @(Get-ChildItem -LiteralPath $fullRoot -Recurse -Filter "coverage.cobertura.xml" -File)) {
        [void](Assert-VerificationCoverageOrdinaryPath -Path $report.FullName -Root $fullRoot -PathType Leaf -Description "$Mode coverage report")
        if (-not $actualReports.Add($report.FullName)) { throw "$Mode coverage reports contain a path collision." }
    }
    if (-not $actualReports.SetEquals($accountedReports)) { throw "$Mode results contain missing, stale, or unexpected coverage reports." }

    $worker = Invoke-VerificationCoverageWorkers -WorkItems @($workItems) -RepositoryRoot $repoRoot -SourceProjectDirectories $SourceProjectDirectories -MaximumWorkers $MaximumCoverageWorkers
    $ownershipReports = [Collections.Generic.List[object]]::new()
    foreach ($snapshot in $worker.Snapshots) {
        $projectName = $projectByReportPath[[IO.Path]::GetFullPath($snapshot.FullName)]
        $ownershipReports.Add([pscustomobject]@{ TestProjectName = $projectName; ProductionFiles = @($snapshot.ProductionFiles) })
        $reportEvidence.Add([pscustomobject][ordered]@{ testProject = $projectName; length = $snapshot.Length; sha256 = $snapshot.Sha256 })
    }
    Assert-VerificationCoverageOwnershipReports -Ownership $Ownership -RepositoryRoot $repoRoot -Reports @($ownershipReports) -AllowUnselectedFiles:($Mode -ceq "UnfilteredEvidence") | Out-Null

    $expectedPackages = @($Ownership.Owners.Package | Sort-Object -CaseSensitive)
    $actualPackages = @($worker.Packages | Sort-Object -CaseSensitive)
    if ($actualPackages.Count -ne $expectedPackages.Count -or
        @(Compare-Object -ReferenceObject $expectedPackages -DifferenceObject $actualPackages -CaseSensitive).Count -ne 0) {
        throw "$Mode results do not contain the exact production package inventory."
    }
    $globalLineSet = Get-VerificationCoverageLineSetEvidence -Ownership $Ownership -RepositoryRoot $repoRoot -Lines @($worker.Lines)
    $packages = Get-EquivalencePackageEvidence -Ownership $Ownership -Lines @($worker.Lines)

    $executedIds = @($testIds | Sort-Object -CaseSensitive)
    if (@($executedIds | Group-Object -CaseSensitive | Where-Object Count -ne 1).Count -ne 0 -or
        $executedIds.Count -ne $inventory.Ids.Count -or
        @(Compare-Object -ReferenceObject $inventory.Ids -DifferenceObject $executedIds -CaseSensitive).Count -ne 0) {
        throw "$Mode executed TestCase.Id inventory does not exactly equal its canonical discovery inventory."
    }

    return [pscustomobject]@{
        ContextSnapshot = $contextSnapshot
        InventorySnapshot = $inventory.Snapshot
        BinaryManifestSnapshot = $binaryEvidence.Snapshot
        BinaryProjects = @($binaryEvidence.Projects)
        TestIds = $executedIds
        TestIdSha256 = Get-VerificationCoverageOwnershipRecordSha256 -Records $executedIds
        TestIdsByLane = @($laneTestIdEvidence | Sort-Object lane)
        GlobalLineSet = $globalLineSet
        Packages = $packages
        Reports = @($reportEvidence | Sort-Object testProject, sha256)
        Trx = @($trxEvidence | Sort-Object lane)
    }
}

$fullUnfilteredRoot = [IO.Path]::GetFullPath($UnfilteredResultsRoot)
$fullFilteredRoot = [IO.Path]::GetFullPath($FilteredResultsRoot)
if (Test-VerificationCoverageSamePath -Left $fullUnfilteredRoot -Right $fullFilteredRoot) {
    throw "Unfiltered and filtered evidence roots must be distinct."
}
$fullReportPath = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $fullReportPath) { throw "Coverage ownership equivalence report path already exists: $fullReportPath" }

$gitStatus = @(& git -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $gitStatus.Count -ne 0) { throw "Coverage ownership equivalence requires a clean committed worktree." }
$headSha = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $headSha -cnotmatch '^[0-9a-f]{40}$') { throw "Coverage ownership equivalence could not resolve one exact Git head." }
$platform = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { "windows" } else { "nonWindows" }
$testProjects = @(Get-VerificationCanonicalTestProjects -RepositoryRoot $repoRoot)
$ownership = Read-VerificationCoverageOwnership -ManifestPath $ownershipManifestPath -RepositoryRoot $repoRoot -TestProjects $testProjects
$sourceProjectDirectories = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($owner in $ownership.Owners) { $sourceProjectDirectories.Add($owner.Package, [IO.Path]::GetFullPath((Join-Path $repoRoot $owner.SourceRoot))) }

$settingsProbeRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-coverage-equivalence-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $settingsProbeRoot | Out-Null
try {
    $unfiltered = Read-EquivalenceResults -ResultsRoot $fullUnfilteredRoot -Mode "UnfilteredEvidence" -Ownership $ownership -TestProjects $testProjects -HeadSha $headSha -Platform $platform -SourceProjectDirectories $sourceProjectDirectories -SettingsProbeRoot $settingsProbeRoot
    $filtered = Read-EquivalenceResults -ResultsRoot $fullFilteredRoot -Mode "FilteredEvidence" -Ownership $ownership -TestProjects $testProjects -HeadSha $headSha -Platform $platform -SourceProjectDirectories $sourceProjectDirectories -SettingsProbeRoot $settingsProbeRoot
}
finally {
    if (Test-Path -LiteralPath $settingsProbeRoot) { Remove-Item -LiteralPath $settingsProbeRoot -Recurse -Force }
}

if ($unfiltered.TestIds.Count -ne $filtered.TestIds.Count -or
    $unfiltered.TestIdSha256 -cne $filtered.TestIdSha256 -or
    @(Compare-Object -ReferenceObject $unfiltered.TestIds -DifferenceObject $filtered.TestIds -CaseSensitive).Count -ne 0) {
    throw "Filtered coverage execution does not preserve the exact unfiltered TestCase.Id inventory."
}
if ($unfiltered.TestIdsByLane.Count -ne $filtered.TestIdsByLane.Count) {
    throw "Filtered coverage execution does not preserve the exact unfiltered lane inventory."
}
for ($index = 0; $index -lt $unfiltered.TestIdsByLane.Count; $index++) {
    $left = $unfiltered.TestIdsByLane[$index]
    $right = $filtered.TestIdsByLane[$index]
    foreach ($property in @("lane", "testCaseCount", "testCaseIdSha256")) {
        if ($left.$property -cne $right.$property) {
            throw "Filtered coverage execution lane '$($left.lane)' $property does not equal the exact unfiltered evidence."
        }
    }
}
if ($unfiltered.BinaryProjects.Count -ne $filtered.BinaryProjects.Count) {
    throw "Filtered coverage binary project inventory does not equal unfiltered evidence."
}
for ($index = 0; $index -lt $unfiltered.BinaryProjects.Count; $index++) {
    $left = $unfiltered.BinaryProjects[$index]
    $right = $filtered.BinaryProjects[$index]
    foreach ($property in @("project", "canonicalBinaryCount", "canonicalBinarySha256", "childInvocationCount", "childInvocationSha256")) {
        if ($left.$property -cne $right.$property) {
            throw "Filtered coverage binary project '$($left.project)' $property does not equal the exact unfiltered evidence."
        }
    }
}
foreach ($property in @("CoverableLineCount", "CoverableLineSha256", "HitLineCount", "HitLineSha256")) {
    if ($unfiltered.GlobalLineSet.$property -cne $filtered.GlobalLineSet.$property) {
        throw "Filtered global production $property does not equal the exact unfiltered evidence."
    }
}
if ($unfiltered.Packages.Count -ne $filtered.Packages.Count) { throw "Filtered package coverage inventory does not equal unfiltered evidence." }
for ($index = 0; $index -lt $unfiltered.Packages.Count; $index++) {
    $left = $unfiltered.Packages[$index]
    $right = $filtered.Packages[$index]
    foreach ($property in @("package", "coverableLineCount", "coverableLineSha256", "hitLineCount", "hitLineSha256", "lineRate")) {
        if ($left.$property -cne $right.$property) {
            throw "Filtered package '$($left.package)' $property does not equal the exact unfiltered evidence."
        }
    }
}

$report = [ordered]@{
    schemaVersion = 1
    status = "passed"
    headSha = $headSha
    platform = $platform
    ownershipSha256 = $ownership.OwnershipSha256
    collectorVersion = $ownership.CollectorVersion
    runSettingsSha256 = $ownership.RunSettingsSha256
    testCaseCount = $unfiltered.TestIds.Count
    testCaseIdSha256 = $unfiltered.TestIdSha256
    testCasesByLane = @($unfiltered.TestIdsByLane)
    binaries = @($unfiltered.BinaryProjects | ForEach-Object {
        $unfilteredBinary = $_
        $filteredBinary = @($filtered.BinaryProjects | Where-Object { $_.project -ceq $unfilteredBinary.project })[0]
        [ordered]@{
            project = $unfilteredBinary.project
            canonicalBinaryCount = $unfilteredBinary.canonicalBinaryCount
            canonicalBinarySha256 = $unfilteredBinary.canonicalBinarySha256
            childInvocationCount = $unfilteredBinary.childInvocationCount
            childInvocationSha256 = $unfilteredBinary.childInvocationSha256
            unfilteredSettingsSha256 = $unfilteredBinary.settingsSha256
            filteredSettingsSha256 = $filteredBinary.settingsSha256
        }
    })
    global = [ordered]@{
        coverableLineCount = $unfiltered.GlobalLineSet.CoverableLineCount
        coverableLineSha256 = $unfiltered.GlobalLineSet.CoverableLineSha256
        hitLineCount = $unfiltered.GlobalLineSet.HitLineCount
        hitLineSha256 = $unfiltered.GlobalLineSet.HitLineSha256
    }
    packages = @($unfiltered.Packages)
    unfiltered = [ordered]@{
        contextSha256 = $unfiltered.ContextSnapshot.Sha256
        inventorySha256 = $unfiltered.InventorySnapshot.Sha256
        binaryManifestSha256 = $unfiltered.BinaryManifestSnapshot.Sha256
        reports = @($unfiltered.Reports)
        trx = @($unfiltered.Trx)
    }
    filtered = [ordered]@{
        contextSha256 = $filtered.ContextSnapshot.Sha256
        inventorySha256 = $filtered.InventorySnapshot.Sha256
        binaryManifestSha256 = $filtered.BinaryManifestSnapshot.Sha256
        reports = @($filtered.Reports)
        trx = @($filtered.Trx)
    }
}
$reportParent = Split-Path -Parent $fullReportPath
if ([string]::IsNullOrWhiteSpace($reportParent)) { throw "Coverage ownership equivalence report requires an explicit parent directory." }
New-Item -ItemType Directory -Path $reportParent -Force | Out-Null
[IO.File]::WriteAllText($fullReportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output "VERIFY_COVERAGE_OWNERSHIP_EQUIVALENCE_COMPLETE status=passed head_sha=$headSha platform=$platform tests=$($unfiltered.TestIds.Count) coverable_lines=$($unfiltered.GlobalLineSet.CoverableLineCount) hit_lines=$($unfiltered.GlobalLineSet.HitLineCount) packages=$($unfiltered.Packages.Count) report=$fullReportPath"
