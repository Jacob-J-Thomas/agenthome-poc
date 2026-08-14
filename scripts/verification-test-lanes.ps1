Set-StrictMode -Version Latest

function Get-VerificationCanonicalTestProjects {
    param([Parameter(Mandatory = $true)] [string]$RepositoryRoot)

    $testsRoot = Join-Path ([IO.Path]::GetFullPath($RepositoryRoot)) "tests"
    $helperProjectNames = @(
        "EmbodySense.CancellationHost.csproj"
        "EmbodySense.Tests.Support.csproj"
    )
    return @(Get-ChildItem -LiteralPath $testsRoot -Recurse -Filter "*.csproj" -File | Where-Object {
        $helperProjectNames -cnotcontains $_.Name
    } | Sort-Object FullName)
}

function Assert-VerificationCoverageOwnershipProperties {
    param(
        [Parameter(Mandatory = $true)] [object]$Value,
        [Parameter(Mandatory = $true)] [string[]]$Expected,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    $actual = @($Value.PSObject.Properties.Name | Sort-Object -CaseSensitive)
    $expectedNames = @($Expected | Sort-Object -CaseSensitive)
    if ($actual.Count -ne $expectedNames.Count -or @(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actual -CaseSensitive).Count -ne 0) {
        throw "$Description does not have its exact schema-1 property set."
    }
}

function Assert-VerificationCoverageOwnershipJsonPropertiesUnique {
    param(
        [Parameter(Mandatory = $true)] [Text.Json.JsonElement]$Element,
        [Parameter(Mandatory = $true)] [string]$Path
    )

    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) {
                throw "Verification coverage ownership JSON contains duplicate property '$($property.Name)' at '$Path'."
            }
            Assert-VerificationCoverageOwnershipJsonPropertiesUnique -Element $property.Value -Path "$Path.$($property.Name)"
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            Assert-VerificationCoverageOwnershipJsonPropertiesUnique -Element $item -Path "$Path[$index]"
            $index++
        }
    }
}

function Assert-VerificationCoverageOwnershipOrdinaryItem {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [ValidateSet("Leaf", "Container")] [string]$PathType,
        [Parameter(Mandatory = $true)] [string]$Description,
        [string]$RepositoryRoot,
        [Collections.Generic.HashSet[string]]$ValidatedPaths
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType $PathType)) {
        throw "$Description is missing or is not a $($PathType.ToLowerInvariant()): $fullPath"
    }
    $paths = [Collections.Generic.List[string]]::new()
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $paths.Add($fullPath)
    }
    else {
        $fullRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
        $relativePath = [IO.Path]::GetRelativePath($fullRepositoryRoot, $fullPath)
        if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath -ceq ".." -or $relativePath.StartsWith("../", [StringComparison]::Ordinal) -or $relativePath.StartsWith("..\", [StringComparison]::Ordinal)) {
            throw "$Description is outside the verification repository root: $fullPath"
        }
        $paths.Add($fullRepositoryRoot)
        $current = $fullRepositoryRoot
        if ($relativePath -cne ".") {
            foreach ($segment in $relativePath.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)) {
                $current = Join-Path $current $segment
                $paths.Add($current)
            }
        }
    }

    foreach ($candidatePath in $paths) {
        if ($null -ne $ValidatedPaths -and -not $ValidatedPaths.Add([IO.Path]::GetFullPath($candidatePath))) { continue }
        $item = Get-Item -LiteralPath $candidatePath -Force
        $linkType = $item.PSObject.Properties["LinkType"]
        if (($null -ne $linkType -and -not [string]::IsNullOrWhiteSpace([string]$linkType.Value)) -or
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "$Description cannot traverse a symbolic, hard-link, or reparse artifact: $candidatePath"
        }
    }
    return Get-Item -LiteralPath $fullPath -Force
}

function Get-VerificationCoverageOwnershipRecordSha256 {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Records,
        [switch]$Presorted
    )

    $orderedRecords = [string[]]@($Records)
    if (-not $Presorted) { [Array]::Sort($orderedRecords, [StringComparer]::Ordinal) }
    $payload = if ($orderedRecords.Count -eq 0) { "" } else { ($orderedRecords -join "`n") + "`n" }
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($payload)
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-VerificationCoverageLineSetEvidence {
    param(
        [Parameter(Mandatory = $true)] [object]$Ownership,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$Lines
    )

    $pathComparer = if (Get-Command Get-VerificationCoveragePathComparer -ErrorAction SilentlyContinue) {
        Get-VerificationCoveragePathComparer
    }
    elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        [StringComparer]::OrdinalIgnoreCase
    }
    else {
        [StringComparer]::Ordinal
    }
    $relativePathsByAbsolutePath = [Collections.Generic.Dictionary[string, string]]::new($pathComparer)
    $packagesByRelativePath = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($relativePath in $Ownership.ProductionFiles) {
        $absolutePath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relativePath))
        if (-not $relativePathsByAbsolutePath.TryAdd($absolutePath, $relativePath)) {
            throw "Verification coverage production source inventory has a case-folded path collision: $relativePath"
        }
        $owners = @($Ownership.Owners | Where-Object { $relativePath.StartsWith("$($_.SourceRoot)/", [StringComparison]::Ordinal) })
        if ($owners.Count -ne 1) {
            throw "Verification coverage production source does not bind one primary package: $relativePath"
        }
        $packagesByRelativePath.Add($relativePath, $owners[0].Package)
    }

    $coverableRecords = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $hitRecords = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in $Lines) {
        $absolutePath = [IO.Path]::GetFullPath([string]$line.File)
        if (-not $relativePathsByAbsolutePath.ContainsKey($absolutePath)) {
            $relativePath = Get-VerificationRepositoryRelativePath -Path $absolutePath -RepositoryRoot $RepositoryRoot
            $generatedOwners = @($Ownership.Owners | Where-Object {
                $relativePath.StartsWith("$($_.SourceRoot)/obj/", [StringComparison]::Ordinal) -and
                $relativePath.EndsWith(".cs", [StringComparison]::Ordinal)
            })
            if ($generatedOwners.Count -ne 1) {
                throw "Verification coverage line-set evidence references a missing or unowned production source: $([string]$line.File)"
            }
            $relativePathsByAbsolutePath.Add($absolutePath, $relativePath)
            $packagesByRelativePath.Add($relativePath, $generatedOwners[0].Package)
        }
        $relativePath = $relativePathsByAbsolutePath[$absolutePath]
        $package = [string]$line.Package
        if ($package -cne $packagesByRelativePath[$relativePath]) {
            throw "Verification coverage line-set evidence package '$package' does not own source '$relativePath'."
        }
        $lineNumber = [int]$line.Line
        if ($lineNumber -lt 1) {
            throw "Verification coverage line-set evidence contains an invalid source line number for '$relativePath'."
        }

        $lineText = $lineNumber.ToString([Globalization.CultureInfo]::InvariantCulture)
        $recordPrefix = $package + [char]0 + $relativePath + [char]0
        $sortKey = $recordPrefix + $lineNumber.ToString("D10", [Globalization.CultureInfo]::InvariantCulture)
        $record = $recordPrefix + $lineText
        $coverableRecords[$sortKey] = $record
        if ([int]$line.Hits -gt 0) { $hitRecords[$sortKey] = $record }
    }

    $coverableKeys = [string[]]@($coverableRecords.Keys)
    $hitKeys = [string[]]@($hitRecords.Keys)
    [Array]::Sort($coverableKeys, [StringComparer]::Ordinal)
    [Array]::Sort($hitKeys, [StringComparer]::Ordinal)
    $orderedCoverableRecords = @($coverableKeys | ForEach-Object { $coverableRecords[$_] })
    $orderedHitRecords = @($hitKeys | ForEach-Object { $hitRecords[$_] })

    return [pscustomobject]@{
        CoverableLineCount = $coverableRecords.Count
        CoverableLineSha256 = Get-VerificationCoverageOwnershipRecordSha256 -Records $orderedCoverableRecords -Presorted
        HitLineCount = $hitRecords.Count
        HitLineSha256 = Get-VerificationCoverageOwnershipRecordSha256 -Records $orderedHitRecords -Presorted
    }
}

function Get-VerificationCoverageCollectorContract {
    param(
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Collections.Generic.HashSet[string]]$ValidatedPaths
    )

    $fullRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    $testsRoot = Join-Path $fullRepositoryRoot "tests"
    $runSettingsPath = Join-Path $testsRoot "verification-pull-request.runsettings"
    $runSettingsItem = Assert-VerificationCoverageOwnershipOrdinaryItem -Path $runSettingsPath -PathType Leaf -Description "Canonical pull-request coverage settings" -RepositoryRoot $fullRepositoryRoot -ValidatedPaths $ValidatedPaths
    $collectorVersions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($projectFile in @(Get-ChildItem -LiteralPath $testsRoot -Recurse -Filter "*.csproj" -File)) {
        [xml]$project = Get-Content -LiteralPath $projectFile.FullName -Raw
        foreach ($reference in @($project.SelectNodes("/Project/ItemGroup/PackageReference[@Include='coverlet.collector']"))) {
            [void]$collectorVersions.Add([string]$reference.GetAttribute("Version"))
        }
    }
    $collectorVersion = if ($collectorVersions.Count -eq 1) { [string]@($collectorVersions)[0] } else { "" }
    if ($collectorVersions.Count -ne 1 -or $collectorVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Verification coverage bound inputs must declare one exact coverlet.collector version."
    }

    return [pscustomobject]@{
        CollectorVersion = $collectorVersion
        RunSettingsPath = $runSettingsItem.FullName
        RunSettingsSha256 = (Get-FileHash -LiteralPath $runSettingsItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-VerificationRepositoryRelativePath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot
    )

    $fullRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $relative = [IO.Path]::GetRelativePath($fullRepositoryRoot, $fullPath).Replace('\', '/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative -ceq ".." -or $relative.StartsWith("../", [StringComparison]::Ordinal)) {
        throw "Verification coverage ownership path is outside the repository: $fullPath"
    }

    return $relative
}

function Read-VerificationCoverageOwnership {
    param(
        [Parameter(Mandatory = $true)] [string]$ManifestPath,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [System.IO.FileInfo[]]$TestProjects
    )

    $fullManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    $ordinaryPathComparer = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $validatedOrdinaryPaths = [Collections.Generic.HashSet[string]]::new($ordinaryPathComparer)
    [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path ([IO.Path]::GetFullPath($RepositoryRoot)) -PathType Container -Description "Verification repository root" -ValidatedPaths $validatedOrdinaryPaths)
    [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path $fullManifestPath -PathType Leaf -Description "Verification coverage ownership manifest" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)

    $manifestRelativePath = Get-VerificationRepositoryRelativePath -Path $fullManifestPath -RepositoryRoot $RepositoryRoot
    if ($manifestRelativePath -cne "tests/verification-coverage-ownership.json") {
        throw "Verification coverage ownership must use the canonical checked-in manifest path."
    }

    $jsonDocument = $null
    try {
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $manifestJson = $strictUtf8.GetString([IO.File]::ReadAllBytes($fullManifestPath))
        $jsonDocument = [Text.Json.JsonDocument]::Parse($manifestJson)
        Assert-VerificationCoverageOwnershipJsonPropertiesUnique -Element $jsonDocument.RootElement -Path '$'
        if ($jsonDocument.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object -or
            $jsonDocument.RootElement.GetProperty("schemaVersion").ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            $jsonDocument.RootElement.GetProperty("exceptions").ValueKind -ne [Text.Json.JsonValueKind]::Object -or
            $jsonDocument.RootElement.GetProperty("owners").ValueKind -ne [Text.Json.JsonValueKind]::Array) {
            throw "The manifest has invalid schema-1 JSON value types."
        }
        foreach ($exceptionProperty in $jsonDocument.RootElement.GetProperty("exceptions").EnumerateObject()) {
            if ($exceptionProperty.Value.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
                throw "Coverage exception lane '$($exceptionProperty.Name)' must be a JSON array."
            }
        }
        $manifest = ConvertFrom-Json -InputObject $manifestJson
    }
    catch {
        throw "Verification coverage ownership manifest is corrupt: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $jsonDocument) {
            $jsonDocument.Dispose()
        }
    }

    Assert-VerificationCoverageOwnershipProperties -Value $manifest -Expected @("schemaVersion", "exceptions", "owners") -Description "Verification coverage ownership manifest"
    if (($manifest.schemaVersion -isnot [long]) -or ([long]$manifest.schemaVersion -ne 1)) {
        throw "Verification coverage ownership schema is invalid."
    }

    $owners = [Collections.Generic.List[object]]::new()
    $ownerPackages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $ownerRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($owner in @($manifest.owners)) {
        Assert-VerificationCoverageOwnershipProperties -Value $owner -Expected @("package", "sourceRoot", "testProject") -Description "Verification coverage owner"
        $package = [string]$owner.package
        $sourceRoot = [string]$owner.sourceRoot
        $testProject = [string]$owner.testProject
        if (($package -notmatch '^EmbodySense(?:\.[A-Za-z0-9]+)+$') -or
            ($testProject -notmatch '^EmbodySense(?:\.[A-Za-z0-9]+)+$') -or
            ($sourceRoot -cne "src/$package") -or
            (-not $ownerPackages.Add($package)) -or
            (-not $ownerRoots.Add($sourceRoot))) {
            throw "Verification coverage ownership contains an invalid or duplicate primary owner."
        }

        $fullSourceRoot = Join-Path $RepositoryRoot $sourceRoot
        if ((-not (Test-Path -LiteralPath $fullSourceRoot -PathType Container)) -or
            (-not (Test-Path -LiteralPath (Join-Path $fullSourceRoot "$package.csproj") -PathType Leaf))) {
            throw "Verification coverage owner '$package' does not bind one exact source project."
        }
        [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path $fullSourceRoot -PathType Container -Description "Verification coverage source root '$package'" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)
        [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path (Join-Path $fullSourceRoot "$package.csproj") -PathType Leaf -Description "Verification coverage source project '$package'" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)

        $owners.Add([pscustomobject]@{
            Package = $package
            SourceRoot = $sourceRoot
            TestProject = $testProject
        })
    }

    $sourceDirectory = Join-Path $RepositoryRoot "src"
    [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path $sourceDirectory -PathType Container -Description "Verification production project root" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)
    $actualSourceProjects = @(Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Filter "*.csproj" -File -Force | Sort-Object FullName)
    if ($actualSourceProjects.Count -eq 0) { throw "Verification coverage production project inventory is empty." }
    $actualSourceProjectPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sourceProject in $actualSourceProjects) {
        [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path $sourceProject.FullName -PathType Leaf -Description "Verification production project" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)
        $relativeProjectPath = Get-VerificationRepositoryRelativePath -Path $sourceProject.FullName -RepositoryRoot $RepositoryRoot
        $segments = @($relativeProjectPath.Split('/'))
        $package = [IO.Path]::GetFileNameWithoutExtension($sourceProject.Name)
        if ($segments.Count -ne 3 -or $segments[0] -cne "src" -or $segments[1] -cne $package -or
            $segments[2] -cne "$package.csproj" -or $package -notmatch '^EmbodySense(?:\.[A-Za-z0-9]+)+$' -or
            -not $ownerPackages.Contains($package) -or -not $actualSourceProjectPaths.Add($relativeProjectPath)) {
            throw "Verification coverage production project must use one owned canonical src/<package>/<package>.csproj path: $relativeProjectPath"
        }
    }
    $declaredSourceProjectPaths = @($owners | ForEach-Object { "$($_.SourceRoot)/$($_.Package).csproj" } | Sort-Object -CaseSensitive)
    $orderedActualSourceProjectPaths = @($actualSourceProjectPaths | Sort-Object -CaseSensitive)
    if ($orderedActualSourceProjectPaths.Count -ne $declaredSourceProjectPaths.Count -or
        @(Compare-Object -ReferenceObject $declaredSourceProjectPaths -DifferenceObject $orderedActualSourceProjectPaths -CaseSensitive).Count -ne 0) {
        throw "Verification coverage primary owners must equal the complete canonical production project inventory."
    }

    $productionFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($owner in $owners) {
        $fullSourceRoot = Join-Path $RepositoryRoot $owner.SourceRoot
        foreach ($directory in @(Get-ChildItem -LiteralPath $fullSourceRoot -Recurse -Directory -Force | Where-Object {
            $_.FullName -notmatch '[\\/](?:bin|obj)(?:[\\/]|$)'
        })) {
            [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path $directory.FullName -PathType Container -Description "Verification coverage source directory" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $fullSourceRoot -Recurse -Filter "*.cs" -File | Where-Object {
            $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
        })) {
            [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path $file.FullName -PathType Leaf -Description "Verification coverage production source" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)
            $relativePath = Get-VerificationRepositoryRelativePath -Path $file.FullName -RepositoryRoot $RepositoryRoot
            if ($relativePath.IndexOfAny([char[]]@(',', ';', '*', '?', '[', ']')) -ge 0) {
                throw "Verification coverage production source path cannot be represented by an exact Coverlet file filter: $relativePath"
            }
            if (-not $productionFiles.Add($relativePath)) {
                throw "Verification coverage production source inventory contains a duplicate path: $relativePath"
            }
        }
    }
    if ($productionFiles.Count -eq 0) {
        throw "Verification coverage production source inventory is empty."
    }
    $orderedProductionFiles = @($productionFiles | Sort-Object -CaseSensitive)

    $exceptionsByTestProject = [Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
    $exceptionPropertyNames = @($manifest.exceptions.PSObject.Properties | ForEach-Object Name)
    if (@($exceptionPropertyNames | Group-Object -CaseSensitive | Where-Object Count -ne 1).Count -ne 0) {
        throw "Verification coverage ownership contains duplicate exception lanes."
    }
    foreach ($exceptionProperty in $manifest.exceptions.PSObject.Properties) {
        $testProject = [string]$exceptionProperty.Name
        if ($testProject -notmatch '^EmbodySense(?:\.[A-Za-z0-9]+)+$') {
            throw "Verification coverage ownership contains an invalid exception test project: $testProject"
        }

        $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($rawPath in @($exceptionProperty.Value)) {
            $relativePath = [string]$rawPath
            if ([string]::IsNullOrWhiteSpace($relativePath) -or
                $relativePath.Contains('\', [StringComparison]::Ordinal) -or
                (-not $relativePath.StartsWith("src/", [StringComparison]::Ordinal)) -or
                (-not $relativePath.EndsWith(".cs", [StringComparison]::Ordinal)) -or
                (-not $productionFiles.Contains($relativePath)) -or
                (-not $paths.Add($relativePath))) {
                throw "Verification coverage ownership contains an invalid, missing, generated, or duplicate exception source: $relativePath"
            }

            $primaryOwner = @($owners | Where-Object { $relativePath.StartsWith("$($_.SourceRoot)/", [StringComparison]::Ordinal) })
            if ($primaryOwner.Count -ne 1 -or $primaryOwner[0].TestProject -ceq $testProject) {
                throw "Verification coverage exception '$relativePath' must cross exactly one primary source owner."
            }
        }
        if ($paths.Count -eq 0) {
            throw "Verification coverage exception lane '$testProject' is empty."
        }
        $exceptionsByTestProject.Add($testProject, @($paths | Sort-Object -CaseSensitive))
    }

    $actualTestProjectNames = @($TestProjects | ForEach-Object BaseName | Sort-Object -CaseSensitive)
    foreach ($testProjectFile in $TestProjects) {
        [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path $testProjectFile.FullName -PathType Leaf -Description "Verification coverage test project" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)
    }
    $declaredTestProjectNames = @($owners.TestProject) + @($exceptionsByTestProject.Keys)
    $declaredTestProjectNames = @($declaredTestProjectNames | Sort-Object -Unique -CaseSensitive)
    if (($actualTestProjectNames.Count -ne $declaredTestProjectNames.Count) -or
        (@(Compare-Object -ReferenceObject $actualTestProjectNames -DifferenceObject $declaredTestProjectNames -CaseSensitive).Count -ne 0)) {
        throw "Verification coverage ownership must classify every canonical test project exactly."
    }

    $laneManifestPath = Join-Path (Split-Path -Parent $fullManifestPath) "verification-coverage-lane-ownership.json"
    [void](Assert-VerificationCoverageOwnershipOrdinaryItem -Path $laneManifestPath -PathType Leaf -Description "Verification coverage lane ownership manifest" -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths)
    $laneDocument = $null
    try {
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $laneJson = $strictUtf8.GetString([IO.File]::ReadAllBytes($laneManifestPath))
        $laneDocument = [Text.Json.JsonDocument]::Parse($laneJson)
        Assert-VerificationCoverageOwnershipJsonPropertiesUnique -Element $laneDocument.RootElement -Path '$'
        if ($laneDocument.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object -or
            $laneDocument.RootElement.GetProperty("schemaVersion").ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            $laneDocument.RootElement.GetProperty("projects").ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw "The lane manifest has invalid schema-1 JSON value types."
        }
        foreach ($projectProperty in $laneDocument.RootElement.GetProperty("projects").EnumerateObject()) {
            if ($projectProperty.Value.ValueKind -ne [Text.Json.JsonValueKind]::Object -or
                $projectProperty.Value.GetProperty("primaryLane").ValueKind -ne [Text.Json.JsonValueKind]::String -or
                $projectProperty.Value.GetProperty("secondaryFiles").ValueKind -ne [Text.Json.JsonValueKind]::Object) {
                throw "Coverage lane project '$($projectProperty.Name)' has invalid JSON value types."
            }
            foreach ($laneProperty in $projectProperty.Value.GetProperty("secondaryFiles").EnumerateObject()) {
                if ($laneProperty.Value.ValueKind -ne [Text.Json.JsonValueKind]::Array) {
                    throw "Coverage lane '$($projectProperty.Name)/$($laneProperty.Name)' must be a JSON array."
                }
                foreach ($pathElement in $laneProperty.Value.EnumerateArray()) {
                    if ($pathElement.ValueKind -ne [Text.Json.JsonValueKind]::String) {
                        throw "Coverage lane '$($projectProperty.Name)/$($laneProperty.Name)' must contain only source-path strings."
                    }
                }
            }
        }
        $laneManifest = ConvertFrom-Json -InputObject $laneJson
    }
    catch {
        throw "Verification coverage lane ownership manifest is corrupt: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $laneDocument) { $laneDocument.Dispose() }
    }

    Assert-VerificationCoverageOwnershipProperties -Value $laneManifest -Expected @("projects", "schemaVersion") -Description "Verification coverage lane ownership manifest"
    if ($laneManifest.schemaVersion -isnot [long] -or [long]$laneManifest.schemaVersion -ne 1) {
        throw "Verification coverage lane ownership schema is invalid."
    }
    $expectedMultiLaneProjects = @($TestProjects | Where-Object { @(Get-VerificationTestProjectLanes -TestProject $_).Count -gt 1 } | ForEach-Object BaseName | Sort-Object -CaseSensitive)
    $actualMultiLaneProjects = @($laneManifest.projects.PSObject.Properties | ForEach-Object Name | Sort-Object -CaseSensitive)
    if ($actualMultiLaneProjects.Count -ne $expectedMultiLaneProjects.Count -or
        @(Compare-Object -ReferenceObject $expectedMultiLaneProjects -DifferenceObject $actualMultiLaneProjects -CaseSensitive).Count -ne 0) {
        throw "Verification coverage lane ownership must classify every and only multi-lane test project."
    }

    $laneSelectionsByTestProject = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $laneOwnershipRecords = [Collections.Generic.List[string]]::new()
    foreach ($testProjectName in $expectedMultiLaneProjects) {
        $record = $laneManifest.projects.PSObject.Properties[$testProjectName].Value
        Assert-VerificationCoverageOwnershipProperties -Value $record -Expected @("primaryLane", "secondaryFiles") -Description "Verification coverage lane project '$testProjectName'"
        $testProject = @($TestProjects | Where-Object BaseName -CEQ $testProjectName)
        if ($testProject.Count -ne 1) { throw "Verification coverage lane project '$testProjectName' is not canonical." }
        $laneNames = @(Get-VerificationTestProjectLanes -TestProject $testProject[0] | ForEach-Object Name)
        $primaryLane = [string]$record.primaryLane
        if ($laneNames -cnotcontains $primaryLane) { throw "Verification coverage primary lane '$testProjectName/$primaryLane' is not checked in." }
        $expectedSecondaryLaneNames = @($laneNames | Where-Object { $_ -cne $primaryLane } | Sort-Object -CaseSensitive)
        $actualSecondaryLaneNames = @($record.secondaryFiles.PSObject.Properties | ForEach-Object Name | Sort-Object -CaseSensitive)
        if ($actualSecondaryLaneNames.Count -ne $expectedSecondaryLaneNames.Count -or
            @(Compare-Object -ReferenceObject $expectedSecondaryLaneNames -DifferenceObject $actualSecondaryLaneNames -CaseSensitive).Count -ne 0) {
            throw "Verification coverage secondary lanes for '$testProjectName' do not exactly match the checked-in lane inventory."
        }

        $allowedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($owner in @($owners | Where-Object TestProject -CEQ $testProjectName)) {
            foreach ($sourceFile in @($orderedProductionFiles | Where-Object { $_.StartsWith("$($owner.SourceRoot)/", [StringComparison]::Ordinal) })) { [void]$allowedFiles.Add($sourceFile) }
        }
        if ($exceptionsByTestProject.ContainsKey($testProjectName)) {
            foreach ($sourceFile in $exceptionsByTestProject[$testProjectName]) { [void]$allowedFiles.Add($sourceFile) }
        }

        $secondaryFiles = [Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
        $laneOwnershipRecords.Add("lane-primary" + [char]0 + $testProjectName + [char]0 + $primaryLane)
        foreach ($laneName in $expectedSecondaryLaneNames) {
            $paths = @($record.secondaryFiles.PSObject.Properties[$laneName].Value)
            $orderedPaths = @($paths | Sort-Object -CaseSensitive)
            if ($paths.Count -eq 0 -or @($paths | Group-Object -CaseSensitive | Where-Object Count -ne 1).Count -ne 0 -or
                ($paths -join "`n") -cne ($orderedPaths -join "`n")) {
                throw "Verification coverage secondary lane '$testProjectName/$laneName' must contain a nonempty, sorted, unique source list."
            }
            foreach ($sourceFile in $paths) {
                if (-not $allowedFiles.Contains([string]$sourceFile)) {
                    throw "Verification coverage secondary lane '$testProjectName/$laneName' selects a source outside its project ownership: $sourceFile"
                }
                $laneOwnershipRecords.Add("lane-file" + [char]0 + $testProjectName + [char]0 + $laneName + [char]0 + [string]$sourceFile)
            }
            $secondaryFiles.Add($laneName, [string[]]$paths)
        }
        $laneSelectionsByTestProject.Add($testProjectName, [pscustomobject]@{ PrimaryLane = $primaryLane; SecondaryFiles = $secondaryFiles })
    }
    $collectorContract = Get-VerificationCoverageCollectorContract -RepositoryRoot $RepositoryRoot -ValidatedPaths $validatedOrdinaryPaths
    $ownershipRecords = [Collections.Generic.List[string]]::new()
    foreach ($owner in @($owners | Sort-Object Package -CaseSensitive)) {
        $ownershipRecords.Add("owner" + [char]0 + $owner.Package + [char]0 + $owner.SourceRoot + [char]0 + $owner.TestProject)
    }
    foreach ($testProjectName in @($exceptionsByTestProject.Keys | Sort-Object -CaseSensitive)) {
        foreach ($relativePath in $exceptionsByTestProject[$testProjectName]) {
            $ownershipRecords.Add("exception" + [char]0 + $testProjectName + [char]0 + $relativePath)
        }
    }
    foreach ($record in $laneOwnershipRecords) { $ownershipRecords.Add($record) }

    return [pscustomobject]@{
        CollectorVersion = $collectorContract.CollectorVersion
        RunSettingsPath = $collectorContract.RunSettingsPath
        RunSettingsSha256 = $collectorContract.RunSettingsSha256
        OwnershipSha256 = Get-VerificationCoverageOwnershipRecordSha256 -Records @($ownershipRecords)
        Owners = @($owners)
        ProductionFiles = $orderedProductionFiles
        ExceptionsByTestProject = $exceptionsByTestProject
        LaneSelectionsByTestProject = $laneSelectionsByTestProject
        TestProjectNames = $actualTestProjectNames
    }
}

function Get-VerificationCoverageExcludeByFilePatterns {
    param(
        [Parameter(Mandatory = $true)] [object]$Ownership,
        [Parameter(Mandatory = $true)] [string[]]$IncludedPackages,
        [Parameter(Mandatory = $true)] [string[]]$SelectedFiles
    )

    $selected = [Collections.Generic.HashSet[string]]::new($SelectedFiles, [StringComparer]::Ordinal)
    $covered = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $patterns = [Collections.Generic.List[string]]::new()
    foreach ($owner in @($Ownership.Owners | Where-Object { $IncludedPackages -ccontains $_.Package } | Sort-Object Package -CaseSensitive)) {
        $rootPrefix = "$($owner.SourceRoot)/"
        $packageFiles = @($Ownership.ProductionFiles | Where-Object { $_.StartsWith($rootPrefix, [StringComparison]::Ordinal) })
        $excludedPackageFiles = @($packageFiles | Where-Object { -not $selected.Contains($_) })
        $candidateDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($sourceFile in $excludedPackageFiles) {
            $directory = [IO.Path]::GetDirectoryName($sourceFile).Replace('\', '/')
            while ($directory.StartsWith($rootPrefix, [StringComparison]::Ordinal)) {
                [void]$candidateDirectories.Add($directory)
                $directory = [IO.Path]::GetDirectoryName($directory).Replace('\', '/')
            }
        }
        $orderedDirectories = @($candidateDirectories | Sort-Object @{ Expression = { @($_.Split('/')).Count }; Ascending = $true }, @{ Expression = { $_ }; Ascending = $true } -CaseSensitive)
        foreach ($directory in $orderedDirectories) {
            $prefix = "$directory/"
            if (@($packageFiles | Where-Object { $selected.Contains($_) -and $_.StartsWith($prefix, [StringComparison]::Ordinal) }).Count -ne 0) { continue }
            $descendants = @($excludedPackageFiles | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })
            if ($descendants.Count -eq 0 -or @($descendants | Where-Object { -not $covered.Contains($_) }).Count -eq 0) { continue }
            $patterns.Add("**/$directory/**/*.cs")
            foreach ($sourceFile in $descendants) { [void]$covered.Add($sourceFile) }
        }
        foreach ($sourceFile in @($excludedPackageFiles | Where-Object { -not $covered.Contains($_) } | Sort-Object -CaseSensitive)) {
            $patterns.Add("**/$sourceFile")
            [void]$covered.Add($sourceFile)
        }
    }

    $expectedExcluded = @($Ownership.ProductionFiles | Where-Object {
        $sourceFile = $_
        -not $selected.Contains($sourceFile) -and @($Ownership.Owners | Where-Object {
            $IncludedPackages -ccontains $_.Package -and $sourceFile.StartsWith("$($_.SourceRoot)/", [StringComparison]::Ordinal)
        }).Count -eq 1
    })
    if ($covered.Count -ne $expectedExcluded.Count -or @($expectedExcluded | Where-Object { -not $covered.Contains($_) }).Count -ne 0) {
        throw "Verification coverage compressed exclusion patterns do not cover the exact intended physical-source complement."
    }
    return @($patterns | Sort-Object -Unique -CaseSensitive)
}

function Get-VerificationCoverageSelection {
    param(
        [Parameter(Mandatory = $true)] [object]$Ownership,
        [Parameter(Mandatory = $true)] [System.IO.FileInfo]$TestProject
    )

    $testProjectName = $TestProject.BaseName
    if ($Ownership.TestProjectNames -cnotcontains $testProjectName) {
        throw "Verification coverage selection requested an unclassified test project: $testProjectName"
    }
    $selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $includedPackages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $primaryRoots = @($Ownership.Owners | Where-Object TestProject -CEQ $testProjectName | ForEach-Object SourceRoot)
    foreach ($owner in @($Ownership.Owners | Where-Object TestProject -CEQ $testProjectName)) {
        [void]$includedPackages.Add($owner.Package)
    }
    foreach ($sourceFile in @($Ownership.ProductionFiles)) {
        foreach ($primaryRoot in $primaryRoots) {
            if ($sourceFile.StartsWith("$primaryRoot/", [StringComparison]::Ordinal)) {
                [void]$selected.Add($sourceFile)
                break
            }
        }
    }
    if ($Ownership.ExceptionsByTestProject.ContainsKey($testProjectName)) {
        foreach ($sourceFile in $Ownership.ExceptionsByTestProject[$testProjectName]) {
            [void]$selected.Add($sourceFile)
            $exceptionOwners = @($Ownership.Owners | Where-Object { $sourceFile.StartsWith("$($_.SourceRoot)/", [StringComparison]::Ordinal) })
            if ($exceptionOwners.Count -ne 1) {
                throw "Verification coverage exception does not resolve one included production package: $sourceFile"
            }
            [void]$includedPackages.Add($exceptionOwners[0].Package)
        }
    }

    $excluded = @($Ownership.ProductionFiles | Where-Object { -not $selected.Contains($_) } | Sort-Object -CaseSensitive)
    if ($selected.Count + $excluded.Count -ne $Ownership.ProductionFiles.Count) {
        throw "Verification coverage selection does not partition the production source inventory for '$testProjectName'."
    }

    if ($includedPackages.Count -eq 0) {
        throw "Verification coverage selection includes no production assembly for '$testProjectName'."
    }
    $orderedIncludedPackages = @($includedPackages | Sort-Object -CaseSensitive)
    $orderedSelectedFiles = @($selected | Sort-Object -CaseSensitive)

    return [pscustomobject]@{
        TestProject = $testProjectName
        PrimaryRoots = @($primaryRoots | Sort-Object -CaseSensitive)
        IncludedPackages = $orderedIncludedPackages
        SelectedFiles = $orderedSelectedFiles
        ExcludedFiles = $excluded
        # Production source-generator output under src/*/obj is deliberately not excluded.
        # It contributes to the existing package denominator and remains instrumented whenever
        # its exact production assembly is selected. Unrelated assemblies are not instrumented.
        IncludeAssemblyPatterns = @($orderedIncludedPackages | ForEach-Object { "[$_]*" })
        ExcludeByFilePatterns = @(Get-VerificationCoverageExcludeByFilePatterns -Ownership $Ownership -IncludedPackages $orderedIncludedPackages -SelectedFiles $orderedSelectedFiles)
    }
}

function Get-VerificationCoverageLaneSelection {
    param(
        [Parameter(Mandatory = $true)] [object]$Ownership,
        [Parameter(Mandatory = $true)] [System.IO.FileInfo]$TestProject,
        [Parameter(Mandatory = $true)] [string]$LaneName
    )

    if ([string]::IsNullOrWhiteSpace($LaneName)) { throw "Verification coverage lane name cannot be empty." }
    $projectSelection = Get-VerificationCoverageSelection -Ownership $Ownership -TestProject $TestProject
    $testProjectName = $TestProject.BaseName
    $checkedInLanes = @(Get-VerificationTestProjectLanes -TestProject $TestProject | ForEach-Object Name)
    if ($checkedInLanes -cnotcontains $LaneName) {
        throw "Verification coverage selection requested an unknown lane: $testProjectName/$LaneName"
    }
    if (-not $Ownership.LaneSelectionsByTestProject.ContainsKey($testProjectName)) {
        if ($checkedInLanes.Count -ne 1) { throw "Verification coverage multi-lane selection is missing for '$testProjectName'." }
        return $projectSelection
    }

    $laneOwnership = $Ownership.LaneSelectionsByTestProject[$testProjectName]
    if ($LaneName -ceq $laneOwnership.PrimaryLane) { return $projectSelection }
    if (-not $laneOwnership.SecondaryFiles.ContainsKey($LaneName)) {
        throw "Verification coverage secondary lane selection is missing for '$testProjectName/$LaneName'."
    }

    $projectSelected = [Collections.Generic.HashSet[string]]::new([string[]]$projectSelection.SelectedFiles, [StringComparer]::Ordinal)
    $selectedFiles = [string[]]$laneOwnership.SecondaryFiles[$LaneName]
    foreach ($sourceFile in $selectedFiles) {
        if (-not $projectSelected.Contains($sourceFile)) {
            throw "Verification coverage secondary lane '$testProjectName/$LaneName' escaped its project selection: $sourceFile"
        }
        $owner = @($Ownership.Owners | Where-Object { $sourceFile.StartsWith("$($_.SourceRoot)/", [StringComparison]::Ordinal) })
        if ($owner.Count -ne 1) { throw "Verification coverage secondary lane source does not bind one production package: $sourceFile" }
    }
    $orderedPackages = @($projectSelection.IncludedPackages)
    $selectedFileSet = [Collections.Generic.HashSet[string]]::new([string[]]$selectedFiles, [StringComparer]::Ordinal)
    $excludedFiles = @($Ownership.ProductionFiles | Where-Object { -not $selectedFileSet.Contains($_) } | Sort-Object -CaseSensitive)
    return [pscustomobject]@{
        TestProject = $testProjectName
        PrimaryRoots = @()
        IncludedPackages = $orderedPackages
        SelectedFiles = $selectedFiles
        ExcludedFiles = $excludedFiles
        IncludeAssemblyPatterns = @($orderedPackages | ForEach-Object { "[$_]*" })
        ExcludeByFilePatterns = @(Get-VerificationCoverageExcludeByFilePatterns -Ownership $Ownership -IncludedPackages $orderedPackages -SelectedFiles $selectedFiles)
    }
}

function Get-VerificationCoverageRunSettingsBytes {
    param(
        [Parameter(Mandatory = $true)] [string]$SourcePath,
        [Parameter(Mandatory = $true)] [object]$Selection
    )

    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load([IO.Path]::GetFullPath($SourcePath))
    $collectors = @($document.SelectNodes("/RunSettings/DataCollectionRunSettings/DataCollectors/DataCollector") | Where-Object {
        $_.GetAttribute("friendlyName") -ceq "XPlat Code Coverage"
    })
    if ($collectors.Count -ne 1) {
        throw "Verification coverage runsettings must contain one exact XPlat Code Coverage collector."
    }
    $configuration = @($collectors[0].SelectNodes("Configuration"))
    if ($configuration.Count -ne 1) {
        throw "Verification coverage runsettings must retain one exact collector configuration."
    }
    $singleHit = @($configuration[0].SelectNodes("SingleHit"))
    $sourceFilterCount = 0
    foreach ($filterName in @("Include", "Exclude", "IncludeByFile", "ExcludeByFile")) {
        $sourceFilterCount += @($configuration[0].SelectNodes($filterName)).Count
    }
    if (($singleHit.Count -ne 1) -or
        ([string]$singleHit[0].InnerText -cne "true") -or
        ($sourceFilterCount -ne 0)) {
        throw "Verification coverage runsettings must retain one SingleHit collector without an inherited assembly or source filter."
    }

    $excludeByFile = $document.CreateElement("ExcludeByFile")
    $include = $document.CreateElement("Include")
    $include.InnerText = @($Selection.IncludeAssemblyPatterns) -join ","
    [void]$configuration[0].AppendChild($include)
    if (@($Selection.ExcludeByFilePatterns).Count -gt 0) {
        $excludeByFile.InnerText = @($Selection.ExcludeByFilePatterns) -join ","
        [void]$configuration[0].AppendChild($excludeByFile)
    }
    $writerSettings = [Xml.XmlWriterSettings]::new()
    $writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $writerSettings.Indent = $true
    $writerSettings.NewLineChars = [Environment]::NewLine
    $writerSettings.NewLineHandling = [Xml.NewLineHandling]::Replace
    $stream = [IO.MemoryStream]::new()
    $writer = [Xml.XmlWriter]::Create($stream, $writerSettings)
    try {
        $document.Save($writer)
        $writer.Flush()
        return ,([byte[]]$stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function Write-VerificationCoverageRunSettings {
    param(
        [Parameter(Mandatory = $true)] [string]$SourcePath,
        [Parameter(Mandatory = $true)] [string]$DestinationPath,
        [Parameter(Mandatory = $true)] [object]$Selection
    )

    $bytes = Get-VerificationCoverageRunSettingsBytes -SourcePath $SourcePath -Selection $Selection
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($DestinationPath), $bytes)
}

function Assert-VerificationCoverageOwnershipReports {
    param(
        [Parameter(Mandatory = $true)] [object]$Ownership,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$Reports,
        [switch]$AllowUnselectedFiles
    )

    $reportsByTestProject = [Collections.Generic.Dictionary[string, Collections.Generic.List[object]]]::new([StringComparer]::Ordinal)
    foreach ($testProjectName in $Ownership.TestProjectNames) {
        $reportsByTestProject.Add($testProjectName, [Collections.Generic.List[object]]::new())
    }
    $pathComparer = if (Get-Command Get-VerificationCoveragePathComparer -ErrorAction SilentlyContinue) {
        Get-VerificationCoveragePathComparer
    }
    elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        [StringComparer]::OrdinalIgnoreCase
    }
    else {
        [StringComparer]::Ordinal
    }
    $pathComparison = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $globalObservedFiles = [Collections.Generic.HashSet[string]]::new($pathComparer)
    foreach ($report in $Reports) {
        $testProjectName = [string]$report.TestProjectName
        if (-not $reportsByTestProject.ContainsKey($testProjectName)) {
            throw "Coverage report ownership references an unclassified test project: $testProjectName"
        }

        $testProject = [IO.FileInfo]::new((Join-Path (Join-Path $RepositoryRoot "tests") "$testProjectName/$testProjectName.csproj"))
        $selection = Get-VerificationCoverageSelection -Ownership $Ownership -TestProject $testProject
        $selectedAbsoluteFiles = [Collections.Generic.HashSet[string]]::new($pathComparer)
        foreach ($relativePath in $selection.SelectedFiles) {
            [void]$selectedAbsoluteFiles.Add([IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relativePath)))
        }

        $observedFiles = [Collections.Generic.HashSet[string]]::new($pathComparer)
        foreach ($observedFile in @($report.ProductionFiles)) {
            $fullObservedFile = [IO.Path]::GetFullPath([string]$observedFile)
            $relativeObservedFile = Get-VerificationRepositoryRelativePath -Path $fullObservedFile -RepositoryRoot $RepositoryRoot
            $generatedOwners = @($Ownership.Owners | Where-Object {
                $relativeObservedFile.StartsWith("$($_.SourceRoot)/obj/", [StringComparison]::Ordinal) -and
                $relativeObservedFile.EndsWith(".cs", [StringComparison]::Ordinal)
            })
            $isRetainedGeneratedProductionSource = $generatedOwners.Count -eq 1 -and $selection.IncludedPackages -ccontains $generatedOwners[0].Package
            if (-not $AllowUnselectedFiles -and -not $selectedAbsoluteFiles.Contains($fullObservedFile) -and -not $isRetainedGeneratedProductionSource) {
                throw "Coverage report for '$testProjectName' contains an unselected production source file: $observedFile"
            }
            [void]$observedFiles.Add($fullObservedFile)
            [void]$globalObservedFiles.Add($fullObservedFile)
        }
        $reportsByTestProject[$testProjectName].Add([pscustomobject]@{ ProductionFiles = $observedFiles })
    }

    foreach ($testProjectName in $Ownership.TestProjectNames) {
        if ($reportsByTestProject[$testProjectName].Count -eq 0) {
            throw "Coverage report ownership is missing the canonical report for test project '$testProjectName'."
        }
    }

    foreach ($owner in $Ownership.Owners) {
        $ownerRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $owner.SourceRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $ownerPrefix = $ownerRoot + [IO.Path]::DirectorySeparatorChar
        $globalOwnerFiles = @($globalObservedFiles | Where-Object { $_.StartsWith($ownerPrefix, $pathComparison) })
        if ($globalOwnerFiles.Count -eq 0) {
            throw "Coverage reports contain no executable-line denominator for primary production package '$($owner.Package)'."
        }

        $primaryObservedFiles = [Collections.Generic.HashSet[string]]::new($pathComparer)
        foreach ($report in $reportsByTestProject[$owner.TestProject]) {
            foreach ($observedFile in $report.ProductionFiles) { [void]$primaryObservedFiles.Add($observedFile) }
        }
        $missingPrimaryFiles = @($globalOwnerFiles | Where-Object { -not $primaryObservedFiles.Contains($_) })
        if ($missingPrimaryFiles.Count -ne 0) {
            throw "Coverage reports omit $($missingPrimaryFiles.Count) globally observed denominator files from primary lane '$($owner.TestProject)' for package '$($owner.Package)'."
        }
    }

    Write-Output "VERIFY_COVERAGE_OWNERSHIP_REPORTS reports=$($Reports.Count) production_files=$($globalObservedFiles.Count) owners=$($Ownership.Owners.Count) status=passed"
}

function New-VerificationTestLane {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [string[]]$IncludeFullyQualifiedName = @(),

        [string[]]$ExcludeFullyQualifiedName = @()
    )

    foreach ($value in @($IncludeFullyQualifiedName) + @($ExcludeFullyQualifiedName)) {
        if ([string]::IsNullOrWhiteSpace($value) -or -not $value.EndsWith(".", [StringComparison]::Ordinal) -or $value.IndexOfAny(@('(', ')', '&', '|', '~', '=', '!')) -ge 0) {
            throw "Verification lane '$Name' contains an unsafe fully-qualified-name predicate."
        }
    }

    return [pscustomobject]@{
        Name = $Name
        IncludeFullyQualifiedName = @($IncludeFullyQualifiedName)
        ExcludeFullyQualifiedName = @($ExcludeFullyQualifiedName)
    }
}

function Get-VerificationTestProjectLanes {
    param([System.IO.FileInfo]$TestProject)

    if ($TestProject.BaseName -ceq "EmbodySense.Core.Persistence.Tests") {
        return @(
            (New-VerificationTestLane -Name "shard-1" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputResponseStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopDefinitionReceiptRetentionTests."
                "EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTestsNormalArchival."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopSequentialEvidenceStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopFrontierStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.Revisions.GovernedLoopRevisionLifecycleStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.Execution.Authority.GovernedLoopEffectAuthorityEvidenceStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTestsRetirementReservation."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopTraceRetentionStoreTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.LocalSkillDependencyManifestDiscoveryTests."
                "EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnStorePublicBehaviorCoverageTests."
            ))
            (New-VerificationTestLane -Name "shard-2" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Persistence.Tests.Authority.AuthorityGrantStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.GraphAuthoring.GovernedLoopGraphRevisionStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.Admission.GovernedLoopAdmissionStoreTests."
                "EmbodySense.Core.Persistence.Tests.Triggers.TriggerQueueStoreTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.CapabilityLifecycleMutationStoreTests."
                "EmbodySense.Core.Persistence.Tests.Memory.ConversationMemoryStoreTests."
                "EmbodySense.Core.Persistence.Tests.ToolResults.ToolResultRetentionStoreTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.CapabilityArtifactStoreTests."
                "EmbodySense.Core.Persistence.Tests.ContextualRoles.ContextualRoleCatalogReaderTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.CapabilityAuthorityTransactionTests."
                "EmbodySense.Core.Persistence.Tests.Memory.FileConversationWorkspaceLeaseTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.CapabilityDependentAdaptersTests."
                "EmbodySense.Core.Persistence.Tests.Verification.VerificationPhaseProbeTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.LocalCapabilityArtifactSourceTests."
                "EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnLeaseHardLinkTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.CapabilityLifecycleBaselineSourceTests."
            ))
            (New-VerificationTestLane -Name "shard-3" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTests."
                "EmbodySense.Core.Persistence.Tests.Credentials.CredentialRegistryStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopRunArtifactMaximumShapeTests."
                "EmbodySense.Core.Persistence.Tests.ContextualRoles.ContextualRoleRevisionStoreTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.CapabilityCatalogStoreTests."
                "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequestStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTestsRetirementEvidenceBounds."
                "EmbodySense.Core.Persistence.Tests.HumanInput.Requests.HumanInputRequestStoreInvariantTests."
                "EmbodySense.Core.Persistence.Tests.Loops.LoopDefinitionStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.LoopRunStoreTests."
                "EmbodySense.Core.Persistence.Tests.ContextualRoles.WorkspaceContextualRoleInstructionSourceProbeTests."
            ))
            (New-VerificationTestLane -Name "shard-4" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopControlOperationStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopDefinitionStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopWorkspaceExecutionGateTests."
                "EmbodySense.Core.Persistence.Tests.Credentials.CredentialLifecyclePersistenceRestartTests."
                "EmbodySense.Core.Persistence.Tests.Authority.AuthorityProfileStoreTests."
                "EmbodySense.Core.Persistence.Tests.Loops.CustomLoopInvocationOperationStoreTests."
                "EmbodySense.Core.Persistence.Tests.Credentials.WindowsCredentialValueProviderTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.CapabilityLifecycleTargetResolverTests."
                "EmbodySense.Core.Persistence.Tests.Loops.PersistencePublicBoundaryCoverageTests."
                "EmbodySense.Core.Persistence.Tests.Audit.AuditLogTests."
                "EmbodySense.Core.Persistence.Tests.Capabilities.FileCapabilityCatalogTrustProviderTests."
                "EmbodySense.Core.Persistence.Tests.Workspace.WorkspaceContextStoreTests."
            ))
        )
    }

    if ($TestProject.BaseName -ceq "EmbodySense.IntegrationTests") {
        return @(
            # The integration assembly contains independent CLI, provider, governance, and
            # architecture fixtures. Immutable assembly copies and disjoint fixture roots keep
            # each measured class partition isolated without changing the two-thread xUnit bound.
            (New-VerificationTestLane -Name "shard-1" -IncludeFullyQualifiedName @(
                "EmbodySense.IntegrationTests.Core.Governance.Tools.ToolBrokerTests."
                "EmbodySense.IntegrationTests.Core.Governance.Tools.ToolBrokerActuationAuthorityBoundaryTests."
                "EmbodySense.IntegrationTests.Core.Capabilities.CapabilityAuthorityWorkspaceMutationIntegrationTests."
            ))
            (New-VerificationTestLane -Name "shard-2" -IncludeFullyQualifiedName @(
                "EmbodySense.IntegrationTests.Cli.CliBehaviorTests."
                "EmbodySense.IntegrationTests.Architecture.CSharpParameterNamingTests."
                "EmbodySense.IntegrationTests.Architecture.TestBoundaryGuardTests."
                "EmbodySense.IntegrationTests.Architecture.ProjectReferenceGuardTests."
                "EmbodySense.IntegrationTests.Architecture.CredentialReconciliationPublicSurfaceTests."
                "EmbodySense.IntegrationTests.Architecture.AuthoredGlobalTypeGuardTests."
            ))
            (New-VerificationTestLane -Name "shard-3" -IncludeFullyQualifiedName @(
                "EmbodySense.IntegrationTests.CodexAppServer.CodexAppServerInferenceTests."
                "EmbodySense.IntegrationTests.Architecture.GovernedLoopExecutionArchitectureTests."
                "EmbodySense.IntegrationTests.Architecture.ProductionSourceLayoutTests."
                "EmbodySense.IntegrationTests.Architecture.ModelSourceLayoutTests."
            ))
        )
    }

    if ($TestProject.BaseName -ceq "EmbodySense.Core.Startup.Tests") {
        return @(
            # Six wrappers retain their shared serial xUnit collection within each process. The
            # independently rooted invocation-retention boundary uses the second bounded xUnit
            # thread inside runtime-2. Immutable assembly copies and fixture roots keep all three
            # process lanes disjoint from every other provider, catalog, trust, and runtime state.
            (New-VerificationTestLane -Name "runtime-1" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTestsResumeAndAuthority."
            ))
            (New-VerificationTestLane -Name "runtime-2" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTestsAdmissionAndContext."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTestsInvocationRetention."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTestsCompletionConstraints."
            ))
            (New-VerificationTestLane -Name "runtime-3" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTestsPublicationAndConcurrency."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeTestsDurabilityAndRecovery."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopRuntimeTestsAdmissionAndBinding."
            ))
            (New-VerificationTestLane -Name "shard-1" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Startup.Tests.Workspace.WorkspaceInitializerTests."
                "EmbodySense.Core.Startup.Tests.Loops.LoopAuthoringFacadeTests."
                "EmbodySense.Core.Startup.Tests.Capabilities.BuiltInCapabilityCatalogSeederTests."
                "EmbodySense.Core.Startup.Tests.Capabilities.CapabilityAdmissionFactoryTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopRuntimeReceiptRecoveryTests."
                "EmbodySense.Core.Startup.Tests.Runtime.DefaultConversationRequestReconciliationReaderTests."
                "EmbodySense.Core.Startup.Tests.Loops.GovernedLoopGraphAuthoringFactoryTests."
                "EmbodySense.Core.Startup.Tests.Runtime.ConversationTranscriptReaderTests."
                "EmbodySense.Core.Startup.Tests.Workspace.DefaultContextualRoleSeederTests."
                "EmbodySense.Core.Startup.Tests.Inference.LlmInferenceClientTests."
                "EmbodySense.Core.Startup.Tests.Credentials.CredentialLifecycleFactoryTests."
                "EmbodySense.Core.Startup.Tests.Triggers.TriggerWorkerRuntimeFacadeTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.ConversationPublicationEffectAuthorityRequestFactoryTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopConversationPublicationAuthorityBoundaryProviderTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopReadOnlyWorkspaceToolAdapterTests."
            ))
            (New-VerificationTestLane -Name "shard-2" -IncludeFullyQualifiedName @(
                "EmbodySense.Core.Startup.Tests.Loops.LoopReceiptRetentionFacadeTests."
                "EmbodySense.Core.Startup.Tests.Configuration.WorkspaceConfigurationReaderTests."
                "EmbodySense.Core.Startup.Tests.Workspace.WorkspaceStatusReaderTests."
                "EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryTests."
                "EmbodySense.Core.Startup.Tests.Capabilities.CapabilityPostureFacadeTests."
                "EmbodySense.Core.Startup.Tests.Capabilities.CapabilityCatalogFacadeTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopInferenceAttemptExecutorTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.LoopRunInspectionFacadeTests."
                "EmbodySense.Core.Startup.Tests.ContextualRoles.ContextualRoleCatalogFacadeTests."
                "EmbodySense.Core.Startup.Tests.Loops.GovernedLoopAdmissionFactoryTests."
                "EmbodySense.Core.Startup.Tests.Loops.CustomLoopToolAuthorityProviderTests."
                "EmbodySense.Core.Startup.Tests.Audit.AuditTailReaderTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopConversationPublicationCommitBoundaryTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.GovernedLoopToolActuationAuthorityBoundaryTests."
                "EmbodySense.Core.Startup.Tests.Capabilities.CapabilityLifecycleFactoryTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.WorkspaceToolEffectAuthorityRequestFactoryTests."
                "EmbodySense.Core.Startup.Tests.Loops.Execution.CustomLoopExecutionCancellationSignalGroupTests."
            ))
        )
    }

    if ($TestProject.BaseName -ceq "EmbodySense.Web.Tests") {
        return @(
            # The Web runtime/API collection remains serialized within each process. Each lane has
            # an immutable assembly copy plus disjoint trust, temporary, and host state.
            (New-VerificationTestLane -Name "shard-1" -IncludeFullyQualifiedName @(
                "EmbodySense.Web.Tests.WebAgentRuntimeHostTests."
            ))
            (New-VerificationTestLane -Name "shard-2" -IncludeFullyQualifiedName @(
                "EmbodySense.Web.Tests.WebSessionHubTests."
                "EmbodySense.Web.Tests.LoopApiControllerTests."
                "EmbodySense.Web.Tests.WebApiControllerTests."
                "EmbodySense.Web.Tests.CapabilityApiControllerTests."
            ))
            (New-VerificationTestLane -Name "shard-3" -IncludeFullyQualifiedName @(
                "EmbodySense.Web.Tests.LoopRunApiControllerTests."
                "EmbodySense.Web.Tests.WebApprovalCoordinatorTests."
                "EmbodySense.Web.Tests.SignalRWebClientNotifierTests."
                "EmbodySense.Web.Tests.WebSessionSecurityTests."
                "EmbodySense.Web.Tests.WebStreamEventTests."
                "EmbodySense.Web.Tests.ProgramTests."
                "EmbodySense.Web.Tests.WebRunOptionsTests."
                "EmbodySense.Web.Tests.WebClientNotifierTests."
                "EmbodySense.Web.Tests.WebConversationPublicationObserverTests."
            ))
        )
    }

    return @((New-VerificationTestLane -Name "all"))
}

function Get-VerificationCoverageLaneBindings {
    param([Parameter(Mandatory = $true)] [System.IO.FileInfo[]]$TestProjects)

    $bindings = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($testProject in $TestProjects) {
        foreach ($lane in @(Get-VerificationTestProjectLanes -TestProject $testProject)) {
            $laneName = "tests-$($testProject.BaseName)-$($lane.Name)"
            if (-not $bindings.TryAdd($laneName, $testProject.BaseName)) {
                throw "Verification coverage lane binding is declared more than once: $laneName"
            }
        }
    }

    return ,$bindings
}

function Get-VerificationCoverageLaneTestProjectName {
    param(
        [Parameter(Mandatory = $true)] [Collections.Generic.Dictionary[string, string]]$Bindings,
        [Parameter(Mandatory = $true)] [string]$LaneName
    )

    $testProjectName = $null
    if (-not $Bindings.TryGetValue($LaneName, [ref]$testProjectName)) {
        throw "Coverage lane '$LaneName' does not bind one exact checked-in test lane."
    }

    return $testProjectName
}

function Assert-VerificationCoverageLaneInventory {
    param(
        [Parameter(Mandatory = $true)] [Collections.Generic.Dictionary[string, string]]$Bindings,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$ObservedLaneNames
    )

    $expected = [Collections.Generic.HashSet[string]]::new($Bindings.Keys, [StringComparer]::Ordinal)
    $observed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($laneName in $ObservedLaneNames) {
        if (-not $observed.Add($laneName)) {
            throw "Coverage lane inventory contains a duplicate checked-in lane: $laneName"
        }
    }
    if (-not $expected.SetEquals($observed)) {
        $missing = @($expected | Where-Object { -not $observed.Contains($_) } | Sort-Object)
        $unexpected = @($observed | Where-Object { -not $expected.Contains($_) } | Sort-Object)
        throw "Coverage lane inventory does not equal the checked-in test-lane map. missing=$($missing -join ',') unexpected=$($unexpected -join ',')"
    }
}

function Get-VerificationTestLaneFilter {
    param(
        [object]$Lane,
        [string[]]$AdditionalExclusions = @()
    )

    $parts = [Collections.Generic.List[string]]::new()
    if (@($Lane.IncludeFullyQualifiedName).Count -gt 0) {
        $include = @($Lane.IncludeFullyQualifiedName | ForEach-Object { "(FullyQualifiedName~$_)" }) -join '|'
        $parts.Add("($include)")
    }

    $exclusions = [Collections.Generic.List[string]]::new()
    foreach ($exclusion in @($Lane.ExcludeFullyQualifiedName)) {
        $exclusions.Add([string]$exclusion)
    }
    foreach ($exclusion in @($AdditionalExclusions)) {
        $exclusions.Add([string]$exclusion)
    }
    foreach ($exclusion in $exclusions) {
        $parts.Add("(FullyQualifiedName!~$exclusion)")
    }

    $parts.Add("(VerificationTier!=Stress)")
    return $parts -join '&'
}
