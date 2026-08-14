Set-StrictMode -Version Latest

function Get-VerificationTestProjectFilter {
    param([Parameter(Mandatory = $true)] [System.IO.FileInfo]$TestProject)

    if ($TestProject.Name -eq "EmbodySense.E2ETests.csproj") {
        return "(FullyQualifiedName!~BrowserFlowTests)&(VerificationTier!=Stress)"
    }

    return "VerificationTier!=Stress"
}

function New-VerificationProjectCoverageIsolation {
    param(
        [Parameter(Mandatory = $true)] [System.IO.FileInfo]$TestProject,
        [Parameter(Mandatory = $true)] [object[]]$Lanes,
        [Parameter(Mandatory = $true)] [object]$CoverageOwnership,
        [Parameter(Mandatory = $true)] [ValidateSet("Debug", "Release")] [string]$Configuration,
        [Parameter(Mandatory = $true)] [ValidateSet("Standard", "UnfilteredEvidence", "FilteredEvidence")] [string]$CoverageOwnershipMode,
        [Parameter(Mandatory = $true)] [string]$PullRequestRunSettingsPath,
        [Parameter(Mandatory = $true)] [string]$CoverageIsolationRoot,
        [Parameter(Mandatory = $true)] [string]$StandardTestResultsRoot,
        [Parameter(Mandatory = $true)] [string]$VerificationPhysicalTempRoot,
        [Parameter(Mandatory = $true)] [string]$VerificationFixtureRunIdentity,
        [Parameter(Mandatory = $true)] [bool]$SkipCoverage
    )

    [xml]$project = Get-Content -LiteralPath $TestProject.FullName -Raw
    $targetFrameworks = @($project.Project.PropertyGroup.TargetFramework | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($targetFrameworks.Count -ne 1) {
        throw "Coverage isolation requires one explicit target framework in $($TestProject.Name)."
    }

    $targetFramework = [string]$targetFrameworks[0]
    $sourceDirectory = Join-Path (Join-Path (Join-Path $TestProject.DirectoryName "bin") $Configuration) $targetFramework
    $testAssemblyName = "$($TestProject.BaseName).dll"
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $testAssemblyName) -PathType Leaf)) {
        throw "Coverage isolation source assembly is missing: $(Join-Path $sourceDirectory $testAssemblyName)"
    }

    $assetsPath = Join-Path $TestProject.DirectoryName "obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Coverage isolation assets are missing: $assetsPath"
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $coverletPackage = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith("coverlet.collector/", [StringComparison]::OrdinalIgnoreCase) })
    if ($coverletPackage.Count -ne 1) {
        throw "Coverage isolation requires one resolved coverlet.collector package for $($TestProject.Name)."
    }

    $collectorSource = $null
    foreach ($packageFolder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path (Join-Path (Join-Path $packageFolder $coverletPackage[0]) "build") $targetFramework
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $collectorSource = $candidate
            break
        }
    }
    if ($null -eq $collectorSource) {
        throw "Coverage isolation could not locate coverlet.collector binaries for $($TestProject.Name)."
    }

    $projectRoot = Join-Path $CoverageIsolationRoot $TestProject.BaseName
    $pristineDirectory = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot "canonical") -Configuration $Configuration -TargetFramework $targetFramework
    $collectorDirectory = Join-Path $projectRoot "Collector"
    $runSettingsPath = Join-Path $projectRoot "verification-pull-request.runsettings"
    $childCoverageRoot = Split-Path -Parent $pristineDirectory
    $childCollectorDirectory = Join-Path $childCoverageRoot "Collector"
    $childRunSettingsPath = Join-Path $childCoverageRoot "verification-pull-request.runsettings"
    $childResultsPath = Join-Path $childCoverageRoot "Results"
    $sourceManifest = @(Get-VerificationDirectoryManifest -Directory $sourceDirectory)
    $pristineManifest = @(Copy-VerifiedDirectory -SourceDirectory $sourceDirectory -DestinationDirectory $pristineDirectory -Description "$($TestProject.BaseName) pristine copy")
    [void](Copy-VerifiedDirectory -SourceDirectory $collectorSource -DestinationDirectory $collectorDirectory -Description "$($TestProject.BaseName) collector copy")
    [void](Copy-VerifiedDirectory -SourceDirectory $collectorSource -DestinationDirectory $childCollectorDirectory -Description "$($TestProject.BaseName) child collector copy")
    $coverageSelection = Get-VerificationCoverageSelection -Ownership $CoverageOwnership -TestProject $TestProject
    if ($CoverageOwnershipMode -ceq "UnfilteredEvidence") {
        Copy-Item -LiteralPath $PullRequestRunSettingsPath -Destination $runSettingsPath
    }
    else {
        Write-VerificationCoverageRunSettings -SourcePath $PullRequestRunSettingsPath -DestinationPath $runSettingsPath -Selection $coverageSelection
    }
    Copy-Item -LiteralPath $runSettingsPath -Destination $childRunSettingsPath

    $laneCopies = [Collections.Generic.List[object]]::new()
    foreach ($lane in $Lanes) {
        $laneDirectory = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot $lane.Name) -Configuration $Configuration -TargetFramework $targetFramework
        $laneManifest = @(Copy-VerifiedDirectoryFromManifest -SourceDirectory $pristineDirectory -SourceManifest $pristineManifest -DestinationDirectory $laneDirectory -Description "$($TestProject.BaseName)/$($lane.Name) lane copy")
        $laneIdentity = "$($TestProject.BaseName)-$($lane.Name)"
        $laneFixtureRoot = Get-VerificationLaneFixturePath -PhysicalTempRoot $VerificationPhysicalTempRoot -RunIdentity $VerificationFixtureRunIdentity -LaneIdentity $laneIdentity
        if (-not (Test-Path -LiteralPath $laneFixtureRoot -PathType Container)) {
            throw "Verification lane temporary path was not reserved by the parent verifier for '$laneIdentity': $laneFixtureRoot"
        }
        if (@(Get-ChildItem -LiteralPath $laneFixtureRoot -Force).Count -ne 0) {
            throw "Verification lane temporary path is not empty before admission for '$laneIdentity': $laneFixtureRoot"
        }
        $laneEnvironment = @{
            EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $laneFixtureRoot "catalog-trust"
            TEMP = $laneFixtureRoot
            TMP = $laneFixtureRoot
            TMPDIR = $laneFixtureRoot
        }
        if (-not $SkipCoverage -and $TestProject.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
            $laneEnvironment.EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $pristineDirectory
        }
        $laneCopies.Add([pscustomobject]@{
            Name = "$($TestProject.BaseName)-$($lane.Name)"
            ProjectName = $TestProject.BaseName
            ShardName = $lane.Name
            Filter = if ($TestProject.Name -eq "EmbodySense.E2ETests.csproj") { Get-VerificationTestLaneFilter -Lane $lane -AdditionalExclusions @("BrowserFlowTests") } else { Get-VerificationTestLaneFilter -Lane $lane }
            AssemblyPath = Join-Path $laneDirectory $testAssemblyName
            Directory = $laneDirectory
            Manifest = $laneManifest
            ResultsPath = Join-Path $StandardTestResultsRoot "$($TestProject.BaseName)-$($lane.Name)"
            FixtureRoot = $laneFixtureRoot
            Environment = $laneEnvironment
        })
    }

    return [pscustomobject]@{
        Project = $TestProject
        SourceDirectory = $sourceDirectory
        SourceManifest = $sourceManifest
        PristineDirectory = $pristineDirectory
        PristineManifest = $pristineManifest
        CollectorDirectory = $collectorDirectory
        RunSettingsPath = $runSettingsPath
        ChildRunSettingsPath = $childRunSettingsPath
        ChildInvocationsRoot = Join-Path $childCoverageRoot "Invocations"
        CoverageSelection = $coverageSelection
        ChildResultsPath = $childResultsPath
        CanonicalAssemblyPath = Join-Path $pristineDirectory $testAssemblyName
        Lanes = @($laneCopies)
    }
}

function ConvertTo-VerificationTestPlanManifest {
    param([Parameter(Mandatory = $true)] [object[]]$Manifest)

    return @($Manifest | Sort-Object RelativePath -CaseSensitive | ForEach-Object {
        [ordered]@{
            relativePath = [string]$_.RelativePath
            length = [long]$_.Length
            sha256 = [string]$_.Sha256
        }
    })
}

function Write-VerificationTestPreparationPlan {
    param(
        [Parameter(Mandatory = $true)] [string]$PlanPath,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [string]$VerificationResultsPath,
        [Parameter(Mandatory = $true)] [ValidateSet("Debug", "Release")] [string]$Configuration,
        [Parameter(Mandatory = $true)] [bool]$SkipCoverage,
        [Parameter(Mandatory = $true)] [ValidateSet("Standard", "UnfilteredEvidence", "FilteredEvidence")] [string]$CoverageOwnershipMode,
        [Parameter(Mandatory = $true)] [string]$FixtureRunIdentity,
        [Parameter(Mandatory = $true)] [object]$CoverageOwnership,
        [Parameter(Mandatory = $true)] [object[]]$Isolations
    )

    $fullPlanPath = [IO.Path]::GetFullPath($PlanPath)
    if (Test-Path -LiteralPath $fullPlanPath) {
        throw "Verification test preparation plan already exists: $fullPlanPath"
    }
    $records = @($Isolations | ForEach-Object {
        $isolation = $_
        [ordered]@{
            projectName = [string]$isolation.Project.BaseName
            projectPath = [string]$isolation.Project.FullName
            sourceDirectory = [string]$isolation.SourceDirectory
            sourceManifest = @(ConvertTo-VerificationTestPlanManifest -Manifest $isolation.SourceManifest)
            pristineDirectory = [string]$isolation.PristineDirectory
            pristineManifest = @(ConvertTo-VerificationTestPlanManifest -Manifest $isolation.PristineManifest)
            collectorDirectory = [string]$isolation.CollectorDirectory
            runSettingsPath = [string]$isolation.RunSettingsPath
            childRunSettingsPath = [string]$isolation.ChildRunSettingsPath
            childInvocationsRoot = [string]$isolation.ChildInvocationsRoot
            childResultsPath = [string]$isolation.ChildResultsPath
            canonicalAssemblyPath = [string]$isolation.CanonicalAssemblyPath
            coverageSelection = [ordered]@{
                selectedFileCount = [int]$isolation.CoverageSelection.SelectedFiles.Count
                excludedFileCount = [int]$isolation.CoverageSelection.ExcludedFiles.Count
                primaryRootCount = [int]$isolation.CoverageSelection.PrimaryRoots.Count
                includedPackageCount = [int]$isolation.CoverageSelection.IncludedPackages.Count
            }
            lanes = @($isolation.Lanes | ForEach-Object {
                $lane = $_
                [ordered]@{
                    name = [string]$lane.Name
                    projectName = [string]$lane.ProjectName
                    shardName = [string]$lane.ShardName
                    filter = [string]$lane.Filter
                    assemblyPath = [string]$lane.AssemblyPath
                    directory = [string]$lane.Directory
                    resultsPath = [string]$lane.ResultsPath
                    fixtureRoot = [string]$lane.FixtureRoot
                    environment = @($lane.Environment.GetEnumerator() | Sort-Object Key -CaseSensitive | ForEach-Object {
                        [ordered]@{ name = [string]$_.Key; value = [string]$_.Value }
                    })
                }
            })
        }
    })
    $plan = [ordered]@{
        schemaVersion = 1
        repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
        verificationResultsPath = [IO.Path]::GetFullPath($VerificationResultsPath)
        configuration = $Configuration
        skipCoverage = $SkipCoverage
        coverageOwnershipMode = $CoverageOwnershipMode
        fixtureRunIdentity = $FixtureRunIdentity
        ownership = [ordered]@{
            collectorVersion = [string]$CoverageOwnership.CollectorVersion
            runSettingsSha256 = [string]$CoverageOwnership.RunSettingsSha256
            ownershipSha256 = [string]$CoverageOwnership.OwnershipSha256
            sourceFileCount = [int]$CoverageOwnership.ProductionFiles.Count
            testProjectCount = [int]$CoverageOwnership.TestProjectNames.Count
        }
        isolations = $records
    }
    $json = $plan | ConvertTo-Json -Depth 10
    $temporaryPath = "$fullPlanPath.$([Guid]::NewGuid().ToString("N")).tmp"
    try {
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false, $true))
        Move-Item -LiteralPath $temporaryPath -Destination $fullPlanPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}

function Test-VerificationTestPlanSamePath {
    param([Parameter(Mandatory = $true)] [string]$Left, [Parameter(Mandatory = $true)] [string]$Right)

    $comparison = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    return [IO.Path]::GetFullPath($Left).Equals([IO.Path]::GetFullPath($Right), $comparison)
}

function Assert-VerificationTestPlanPath {
    param([Parameter(Mandatory = $true)] [string]$Actual, [Parameter(Mandatory = $true)] [string]$Expected, [Parameter(Mandatory = $true)] [string]$Description)

    if (-not (Test-VerificationTestPlanSamePath -Left $Actual -Right $Expected)) {
        throw "$Description does not match its exact preparation topology. Expected '$([IO.Path]::GetFullPath($Expected))'; actual '$([IO.Path]::GetFullPath($Actual))'."
    }
}

function ConvertFrom-VerificationTestPlanManifest {
    param([Parameter(Mandatory = $true)] [object[]]$Entries, [Parameter(Mandatory = $true)] [string]$Description)

    if ($Entries.Count -eq 0) { throw "$Description is empty." }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $records = [Collections.Generic.List[object]]::new()
    foreach ($entry in $Entries) {
        Assert-VerificationCoverageOwnershipProperties -Value $entry -Expected @("length", "relativePath", "sha256") -Description "$Description entry"
        $relativePath = [string]$entry.relativePath
        $segments = @($relativePath.Split('/'))
        if ([string]::IsNullOrWhiteSpace($relativePath) -or $relativePath.Contains('\', [StringComparison]::Ordinal) -or [IO.Path]::IsPathRooted($relativePath) -or $segments.Count -eq 0 -or @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -ceq "." -or $_ -ceq ".." }).Count -ne 0 -or -not $paths.Add($relativePath)) {
            throw "$Description contains an unsafe or duplicate relative path: '$relativePath'."
        }
        if ($entry.length -isnot [long] -or [long]$entry.length -lt 0 -or [string]$entry.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw "$Description contains invalid length or SHA-256 evidence for '$relativePath'."
        }
        $records.Add([pscustomobject]@{ RelativePath = $relativePath; Length = [long]$entry.length; Sha256 = [string]$entry.sha256 })
    }
    return @($records)
}

function Read-VerificationTestPreparationPlan {
    param(
        [Parameter(Mandatory = $true)] [string]$PlanPath,
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [string]$VerificationResultsPath,
        [Parameter(Mandatory = $true)] [string]$CoverageIsolationRoot,
        [Parameter(Mandatory = $true)] [string]$StandardTestResultsRoot,
        [Parameter(Mandatory = $true)] [string]$VerificationPhysicalTempRoot,
        [Parameter(Mandatory = $true)] [string]$FixtureRunIdentity,
        [Parameter(Mandatory = $true)] [ValidateSet("Debug", "Release")] [string]$Configuration,
        [Parameter(Mandatory = $true)] [bool]$SkipCoverage,
        [Parameter(Mandatory = $true)] [ValidateSet("Standard", "UnfilteredEvidence", "FilteredEvidence")] [string]$CoverageOwnershipMode,
        [Parameter(Mandatory = $true)] [object]$CoverageOwnership,
        [Parameter(Mandatory = $true)] [System.IO.FileInfo[]]$TestProjects
    )

    $fullPlanPath = [IO.Path]::GetFullPath($PlanPath)
    if (-not (Test-Path -LiteralPath $fullPlanPath -PathType Leaf)) { throw "Verification test preparation plan is missing: $fullPlanPath" }
    $document = $null
    try {
        $json = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($fullPlanPath))
        $document = [Text.Json.JsonDocument]::Parse($json)
        Assert-VerificationCoverageOwnershipJsonPropertiesUnique -Element $document.RootElement -Path '$'
        $plan = ConvertFrom-Json -InputObject $json
    }
    catch {
        throw "Verification test preparation plan is corrupt: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $document) { $document.Dispose() }
    }

    Assert-VerificationCoverageOwnershipProperties -Value $plan -Expected @("configuration", "coverageOwnershipMode", "fixtureRunIdentity", "isolations", "ownership", "repositoryRoot", "schemaVersion", "skipCoverage", "verificationResultsPath") -Description "Verification test preparation plan"
    if ($plan.schemaVersion -isnot [long] -or [long]$plan.schemaVersion -ne 1 -or $plan.skipCoverage -isnot [bool] -or [bool]$plan.skipCoverage -ne $SkipCoverage -or [string]$plan.configuration -cne $Configuration -or [string]$plan.coverageOwnershipMode -cne $CoverageOwnershipMode -or [string]$plan.fixtureRunIdentity -cne $FixtureRunIdentity) {
        throw "Verification test preparation plan does not match its exact schema, configuration, coverage mode, or run identity."
    }
    Assert-VerificationTestPlanPath -Actual ([string]$plan.repositoryRoot) -Expected $RepositoryRoot -Description "Verification test preparation repository root"
    Assert-VerificationTestPlanPath -Actual ([string]$plan.verificationResultsPath) -Expected $VerificationResultsPath -Description "Verification test preparation results root"
    Assert-VerificationCoverageOwnershipProperties -Value $plan.ownership -Expected @("collectorVersion", "ownershipSha256", "runSettingsSha256", "sourceFileCount", "testProjectCount") -Description "Verification test preparation ownership"
    if ([string]$plan.ownership.collectorVersion -cne [string]$CoverageOwnership.CollectorVersion -or [string]$plan.ownership.ownershipSha256 -cne [string]$CoverageOwnership.OwnershipSha256 -or [string]$plan.ownership.runSettingsSha256 -cne [string]$CoverageOwnership.RunSettingsSha256 -or [long]$plan.ownership.sourceFileCount -ne [long]$CoverageOwnership.ProductionFiles.Count -or [long]$plan.ownership.testProjectCount -ne [long]$TestProjects.Count) {
        throw "Verification test preparation ownership evidence does not match the current canonical manifest."
    }

    $planIsolations = @($plan.isolations)
    if ($planIsolations.Count -ne $TestProjects.Count) {
        throw "Verification test preparation plan contains $($planIsolations.Count) projects; expected $($TestProjects.Count)."
    }
    $isolations = [Collections.Generic.List[object]]::new()
    for ($projectIndex = 0; $projectIndex -lt $TestProjects.Count; $projectIndex++) {
        $testProject = $TestProjects[$projectIndex]
        $record = $planIsolations[$projectIndex]
        Assert-VerificationCoverageOwnershipProperties -Value $record -Expected @("canonicalAssemblyPath", "childInvocationsRoot", "childResultsPath", "childRunSettingsPath", "collectorDirectory", "coverageSelection", "lanes", "pristineDirectory", "pristineManifest", "projectName", "projectPath", "runSettingsPath", "sourceDirectory", "sourceManifest") -Description "Verification test preparation project"
        if ([string]$record.projectName -cne $testProject.BaseName) { throw "Verification test preparation project order or identity is invalid at index $projectIndex." }
        Assert-VerificationTestPlanPath -Actual ([string]$record.projectPath) -Expected $testProject.FullName -Description "Verification test project path"

        [xml]$project = Get-Content -LiteralPath $testProject.FullName -Raw
        $targetFrameworks = @($project.Project.PropertyGroup.TargetFramework | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        if ($targetFrameworks.Count -ne 1) { throw "Verification test preparation requires one explicit target framework in $($testProject.Name)." }
        $targetFramework = [string]$targetFrameworks[0]
        $sourceDirectory = Join-Path (Join-Path (Join-Path $testProject.DirectoryName "bin") $Configuration) $targetFramework
        $projectRoot = Join-Path $CoverageIsolationRoot $testProject.BaseName
        $pristineDirectory = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot "canonical") -Configuration $Configuration -TargetFramework $targetFramework
        $collectorDirectory = Join-Path $projectRoot "Collector"
        $runSettingsPath = Join-Path $projectRoot "verification-pull-request.runsettings"
        $childCoverageRoot = Split-Path -Parent $pristineDirectory
        $childRunSettingsPath = Join-Path $childCoverageRoot "verification-pull-request.runsettings"
        $childInvocationsRoot = Join-Path $childCoverageRoot "Invocations"
        $childResultsPath = Join-Path $childCoverageRoot "Results"
        $canonicalAssemblyPath = Join-Path $pristineDirectory "$($testProject.BaseName).dll"
        Assert-VerificationTestPlanPath -Actual ([string]$record.sourceDirectory) -Expected $sourceDirectory -Description "$($testProject.BaseName) source directory"
        Assert-VerificationTestPlanPath -Actual ([string]$record.pristineDirectory) -Expected $pristineDirectory -Description "$($testProject.BaseName) pristine directory"
        Assert-VerificationTestPlanPath -Actual ([string]$record.collectorDirectory) -Expected $collectorDirectory -Description "$($testProject.BaseName) collector directory"
        Assert-VerificationTestPlanPath -Actual ([string]$record.runSettingsPath) -Expected $runSettingsPath -Description "$($testProject.BaseName) runsettings"
        Assert-VerificationTestPlanPath -Actual ([string]$record.childRunSettingsPath) -Expected $childRunSettingsPath -Description "$($testProject.BaseName) child runsettings"
        Assert-VerificationTestPlanPath -Actual ([string]$record.childInvocationsRoot) -Expected $childInvocationsRoot -Description "$($testProject.BaseName) child invocations root"
        Assert-VerificationTestPlanPath -Actual ([string]$record.childResultsPath) -Expected $childResultsPath -Description "$($testProject.BaseName) child results root"
        Assert-VerificationTestPlanPath -Actual ([string]$record.canonicalAssemblyPath) -Expected $canonicalAssemblyPath -Description "$($testProject.BaseName) canonical assembly"
        foreach ($requiredPath in @($sourceDirectory, $pristineDirectory, $collectorDirectory)) {
            if (-not (Test-Path -LiteralPath $requiredPath -PathType Container)) { throw "Verification test preparation artifact directory is missing: $requiredPath" }
        }
        foreach ($requiredPath in @($runSettingsPath, $childRunSettingsPath, $canonicalAssemblyPath)) {
            if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Verification test preparation artifact is missing: $requiredPath" }
        }
        $runSettingsHash = (Get-FileHash -LiteralPath $runSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $childRunSettingsHash = (Get-FileHash -LiteralPath $childRunSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($runSettingsHash -cne $childRunSettingsHash) { throw "Verification test preparation parent and child runsettings do not byte-match for '$($testProject.BaseName)'." }

        $sourceManifest = @(ConvertFrom-VerificationTestPlanManifest -Entries @($record.sourceManifest) -Description "$($testProject.BaseName) source manifest")
        $pristineManifest = @(ConvertFrom-VerificationTestPlanManifest -Entries @($record.pristineManifest) -Description "$($testProject.BaseName) pristine manifest")
        $coverageSelection = Get-VerificationCoverageSelection -Ownership $CoverageOwnership -TestProject $testProject
        Assert-VerificationCoverageOwnershipProperties -Value $record.coverageSelection -Expected @("excludedFileCount", "includedPackageCount", "primaryRootCount", "selectedFileCount") -Description "$($testProject.BaseName) coverage selection"
        if ([long]$record.coverageSelection.selectedFileCount -ne $coverageSelection.SelectedFiles.Count -or [long]$record.coverageSelection.excludedFileCount -ne $coverageSelection.ExcludedFiles.Count -or [long]$record.coverageSelection.primaryRootCount -ne $coverageSelection.PrimaryRoots.Count -or [long]$record.coverageSelection.includedPackageCount -ne $coverageSelection.IncludedPackages.Count) {
            throw "Verification test preparation coverage selection does not match the current ownership map for '$($testProject.BaseName)'."
        }

        $expectedLanes = @(Get-VerificationTestProjectLanes -TestProject $testProject)
        $laneRecords = @($record.lanes)
        if ($laneRecords.Count -ne $expectedLanes.Count) { throw "Verification test preparation has the wrong lane count for '$($testProject.BaseName)'." }
        $lanes = [Collections.Generic.List[object]]::new()
        for ($laneIndex = 0; $laneIndex -lt $expectedLanes.Count; $laneIndex++) {
            $expectedLane = $expectedLanes[$laneIndex]
            $laneRecord = $laneRecords[$laneIndex]
            Assert-VerificationCoverageOwnershipProperties -Value $laneRecord -Expected @("assemblyPath", "directory", "environment", "filter", "fixtureRoot", "name", "projectName", "resultsPath", "shardName") -Description "Verification test preparation lane"
            $laneIdentity = "$($testProject.BaseName)-$($expectedLane.Name)"
            $expectedFilter = if ($testProject.Name -eq "EmbodySense.E2ETests.csproj") { Get-VerificationTestLaneFilter -Lane $expectedLane -AdditionalExclusions @("BrowserFlowTests") } else { Get-VerificationTestLaneFilter -Lane $expectedLane }
            if ([string]$laneRecord.name -cne $laneIdentity -or [string]$laneRecord.projectName -cne $testProject.BaseName -or [string]$laneRecord.shardName -cne $expectedLane.Name -or [string]$laneRecord.filter -cne $expectedFilter) {
                throw "Verification test preparation lane identity, order, or filter is invalid for '$laneIdentity'."
            }
            $laneDirectory = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $projectRoot $expectedLane.Name) -Configuration $Configuration -TargetFramework $targetFramework
            $laneAssemblyPath = Join-Path $laneDirectory "$($testProject.BaseName).dll"
            $laneResultsPath = Join-Path $StandardTestResultsRoot $laneIdentity
            $laneFixtureRoot = Get-VerificationLaneFixturePath -PhysicalTempRoot $VerificationPhysicalTempRoot -RunIdentity $FixtureRunIdentity -LaneIdentity $laneIdentity
            Assert-VerificationTestPlanPath -Actual ([string]$laneRecord.directory) -Expected $laneDirectory -Description "$laneIdentity directory"
            Assert-VerificationTestPlanPath -Actual ([string]$laneRecord.assemblyPath) -Expected $laneAssemblyPath -Description "$laneIdentity assembly"
            Assert-VerificationTestPlanPath -Actual ([string]$laneRecord.resultsPath) -Expected $laneResultsPath -Description "$laneIdentity results root"
            Assert-VerificationTestPlanPath -Actual ([string]$laneRecord.fixtureRoot) -Expected $laneFixtureRoot -Description "$laneIdentity fixture root"
            if (-not (Test-Path -LiteralPath $laneDirectory -PathType Container) -or -not (Test-Path -LiteralPath $laneAssemblyPath -PathType Leaf) -or -not (Test-Path -LiteralPath $laneFixtureRoot -PathType Container)) {
                throw "Verification test preparation lane artifact or reserved fixture root is missing for '$laneIdentity'."
            }
            if (@(Get-ChildItem -LiteralPath $laneFixtureRoot -Force).Count -ne 0) {
                throw "Verification test preparation lane fixture root is not empty before execution for '$laneIdentity'."
            }

            $environment = @{}
            foreach ($entry in @($laneRecord.environment)) {
                Assert-VerificationCoverageOwnershipProperties -Value $entry -Expected @("name", "value") -Description "$laneIdentity environment entry"
                $environmentName = [string]$entry.name
                if ($environment.ContainsKey($environmentName)) { throw "Verification test preparation lane environment contains a duplicate key for '$laneIdentity'." }
                $environment.Add($environmentName, [string]$entry.value)
            }
            $expectedEnvironment = @{
                EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $laneFixtureRoot "catalog-trust"
                TEMP = $laneFixtureRoot
                TMP = $laneFixtureRoot
                TMPDIR = $laneFixtureRoot
            }
            if (-not $SkipCoverage -and $testProject.Name -eq "EmbodySense.Core.Persistence.Tests.csproj") {
                $expectedEnvironment.EMBODYSENSE_COVERAGE_CHILD_ASSEMBLY_DIRECTORY = $pristineDirectory
            }
            if ($environment.Count -ne $expectedEnvironment.Count -or @($expectedEnvironment.Keys | Where-Object { -not $environment.ContainsKey($_) -or [string]$environment[$_] -cne [string]$expectedEnvironment[$_] }).Count -ne 0) {
                throw "Verification test preparation lane environment is not exact for '$laneIdentity'."
            }
            $lanes.Add([pscustomobject]@{
                Name = $laneIdentity
                ProjectName = $testProject.BaseName
                ShardName = $expectedLane.Name
                Filter = $expectedFilter
                AssemblyPath = $laneAssemblyPath
                Directory = $laneDirectory
                Manifest = $pristineManifest
                ResultsPath = $laneResultsPath
                FixtureRoot = $laneFixtureRoot
                Environment = $environment
            })
        }
        $isolations.Add([pscustomobject]@{
            Project = $testProject
            SourceDirectory = $sourceDirectory
            SourceManifest = $sourceManifest
            PristineDirectory = $pristineDirectory
            PristineManifest = $pristineManifest
            CollectorDirectory = $collectorDirectory
            RunSettingsPath = $runSettingsPath
            ChildRunSettingsPath = $childRunSettingsPath
            ChildInvocationsRoot = $childInvocationsRoot
            CoverageSelection = $coverageSelection
            ChildResultsPath = $childResultsPath
            CanonicalAssemblyPath = $canonicalAssemblyPath
            Lanes = @($lanes)
        })
    }
    return @($isolations)
}
