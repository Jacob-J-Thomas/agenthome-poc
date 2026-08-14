Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
$parallelScriptPath = Join-Path $repoRoot "scripts\verification-parallel.ps1"
$artifactScriptPath = Join-Path $repoRoot "scripts\verification-artifacts.ps1"
$scheduleScriptPath = Join-Path $repoRoot "scripts\verification-schedule.ps1"
$coverageEvidenceScriptPath = Join-Path $repoRoot "scripts\verification-coverage-evidence.ps1"
$laneScriptPath = Join-Path $repoRoot "scripts\verification-test-lanes.ps1"
$powerShellExecutable = (Get-Process -Id $PID).Path
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }

    $script:assertionCount++
}

function Assert-Contains {
    param([string]$Actual, [string]$Expected, [string]$Message)

    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'. Actual: $Actual"
}

function Invoke-ExpectedFailure {
    param([scriptblock]$Action, [string]$ExpectedMessage)

    $failureMessage = $null
    try { & $Action | Out-Null } catch { $failureMessage = $_.Exception.Message }
    if ($null -eq $failureMessage) { throw "Expected the action to fail, but it completed successfully." }
    Assert-Contains -Actual $failureMessage -Expected $ExpectedMessage -Message "Failure diagnostic mismatch."
    return $failureMessage
}

. $phaseScriptPath
. $parallelScriptPath
. $artifactScriptPath
. $scheduleScriptPath
. $coverageEvidenceScriptPath
. $laneScriptPath

function Get-VirtualVerificationSchedule {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Profiles,

        [Parameter(Mandatory = $true)]
        [int]$MaximumWorkers,

        [Parameter(Mandatory = $true)]
        [int]$MaximumResourceCapacity,

        [Parameter(Mandatory = $true)]
        [int]$MaximumProcessHeavyWorkers,

        [Parameter(Mandatory = $true)]
        [int]$MaximumCpuBoundWorkers
    )

    $phases = @($Profiles | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            EstimatedDurationSeconds = $_.EstimatedDurationSeconds
            SchedulingPrioritySeconds = $_.EstimatedDurationSeconds
            Weight = $_.Weight
            EffectiveWeight = $_.Weight
            ResourceClass = $_.ResourceClass
            SchedulingDeferrals = 0
        }
    })
    $pending = [Collections.Generic.List[object]]::new()
    foreach ($phase in @(Get-VerificationParallelPhaseSchedulingOrder -Phases $phases -MaximumProcessHeavyWorkers $MaximumProcessHeavyWorkers -MaximumCpuBoundWorkers $MaximumCpuBoundWorkers)) {
        $pending.Add($phase)
    }

    $running = [Collections.Generic.List[object]]::new()
    $starts = [ordered]@{}
    $activeResourceCapacity = 0
    $activeResourceClassCounts = @{ Ordinary = 0; CpuBound = 0; ProcessHeavy = 0 }
    $elapsedSeconds = 0
    while ($pending.Count -gt 0 -or $running.Count -gt 0) {
        while ($pending.Count -gt 0 -and $running.Count -lt $MaximumWorkers -and $activeResourceCapacity -lt $MaximumResourceCapacity) {
            $availableResourceClassSlots = @{
                Ordinary = $MaximumWorkers
                CpuBound = $MaximumCpuBoundWorkers - $activeResourceClassCounts.CpuBound
                ProcessHeavy = $MaximumProcessHeavyWorkers - $activeResourceClassCounts.ProcessHeavy
            }
            $phase = Select-VerificationParallelPhase -Pending $pending -AvailableCapacity ($MaximumResourceCapacity - $activeResourceCapacity) -AvailableResourceClassSlots $availableResourceClassSlots
            if ($null -eq $phase) {
                break
            }

            $starts[$phase.Name] = $elapsedSeconds
            $activeResourceCapacity += $phase.EffectiveWeight
            $activeResourceClassCounts[$phase.ResourceClass]++
            $running.Add([pscustomobject]@{ Phase = $phase; CompletesAtSeconds = $elapsedSeconds + $phase.EstimatedDurationSeconds })
        }

        if ($running.Count -eq 0) {
            throw "Virtual verification scheduler made no progress."
        }

        $elapsedSeconds = [int](($running | Measure-Object -Property CompletesAtSeconds -Minimum).Minimum)
        foreach ($entry in @($running | Where-Object { $_.CompletesAtSeconds -eq $elapsedSeconds })) {
            $activeResourceCapacity -= $entry.Phase.EffectiveWeight
            $activeResourceClassCounts[$entry.Phase.ResourceClass]--
            [void]$running.Remove($entry)
        }
    }

    return [pscustomobject]@{ MakespanSeconds = $elapsedSeconds; Starts = $starts }
}

$requiredGateProfiles = @(Get-VerificationRequiredGateScheduleProfiles)
Assert-True -Condition ((Get-VerificationRequiredGateResourceCapacity) -eq 12) -Message "Required gates must retain the explicit twelve-unit logical resource capacity."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumProcessHeavyWorkers) -eq 4) -Message "Required gates must admit at most four exact immutable process-heavy shards."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumCpuBoundWorkers) -eq 1) -Message "Required gates must admit at most one CPU-bound format gate."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 8 -HardwareProcessorCount 10) -eq 4) -Message "A larger host must retain the checked-in four-process required-gate ceiling."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 6 -HardwareProcessorCount 4) -eq 4) -Message "A hosted four-core runner must admit exactly four physical required-gate workers."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 4 -HardwareProcessorCount 10) -eq 4) -Message "A lower explicit worker request must remain authoritative below the required-gate ceiling."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 4 -HardwareProcessorCount 4) -eq 4) -Message "A hosted four-core request must not be expanded beyond its explicit four-worker bound."
Assert-True -Condition ((Get-VerificationRequiredGateMaximumWorkers -MaximumTestWorkers 6 -HardwareProcessorCount 2) -eq 2) -Message "A smaller host must reduce required-gate workers to its physical processor count."
Assert-True -Condition ($requiredGateProfiles.Count -eq 16) -Message "The exact seven unsharded assembly lanes, seven Persistence/Startup shards, one combined formatter, and git-diff gate must have checked-in profiles."
Assert-True -Condition (@($requiredGateProfiles | Group-Object Name -CaseSensitive | Where-Object Count -ne 1).Count -eq 0) -Message "Required gate scheduling profiles must have exact unique names."
Assert-VerificationRequiredGateSchedule -Phases $requiredGateProfiles
$expectedRequiredGateNames = @(
    "format-csharp"
    "git-diff-check"
    "tests-EmbodySense.Cli.Command.Tests-all"
    "tests-EmbodySense.Core.Application.Tests-all"
    "tests-EmbodySense.Core.Clients.Tests-all"
    "tests-EmbodySense.Core.Common.Tests-all"
    "tests-EmbodySense.Core.Persistence.Tests-shard-1"
    "tests-EmbodySense.Core.Persistence.Tests-shard-2"
    "tests-EmbodySense.Core.Persistence.Tests-shard-3"
    "tests-EmbodySense.Core.Persistence.Tests-shard-4"
    "tests-EmbodySense.Core.Startup.Tests-runtime"
    "tests-EmbodySense.Core.Startup.Tests-shard-1"
    "tests-EmbodySense.Core.Startup.Tests-shard-2"
    "tests-EmbodySense.E2ETests-all"
    "tests-EmbodySense.IntegrationTests-all"
    "tests-EmbodySense.Web.Tests-all"
)
Assert-True -Condition ((@($requiredGateProfiles.Name | Sort-Object) -join "`n") -ceq (@($expectedRequiredGateNames | Sort-Object) -join "`n")) -Message "Required-gate profiles must equal the canonical class-sharded test catalog plus the combined formatter and git-diff exactly."
$declaredRequiredGateNames = [Collections.Generic.List[string]]::new()
$declaredRequiredGateNames.Add("format-csharp")
$declaredRequiredGateNames.Add("git-diff-check")
$testProjects = @(Get-VerificationCanonicalTestProjects -RepositoryRoot $repoRoot)
$allTestProjects = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "tests") -Recurse -Filter "*.csproj" -File | Sort-Object FullName)
$expectedHelperProjects = @(@("EmbodySense.CancellationHost.csproj", "EmbodySense.Tests.Support.csproj") | Sort-Object -CaseSensitive)
$actualHelperProjects = @($allTestProjects | Where-Object { $testProjects.FullName -cnotcontains $_.FullName } | ForEach-Object Name | Sort-Object -CaseSensitive)
Assert-True -Condition (($actualHelperProjects -join "`n") -ceq ($expectedHelperProjects -join "`n")) -Message "Canonical coverage test-project discovery must exclude only the exact helper-project catalog."
$coverageOwnership = Read-VerificationCoverageOwnership -ManifestPath (Join-Path $repoRoot "tests/verification-coverage-ownership.json") -RepositoryRoot $repoRoot -TestProjects $testProjects
$coverageLaneBindings = Get-VerificationCoverageLaneBindings -TestProjects $testProjects
$expectedCoverageLaneNames = @($expectedRequiredGateNames | Where-Object { $_.StartsWith("tests-", [StringComparison]::Ordinal) } | Sort-Object -CaseSensitive)
Assert-True -Condition ($coverageLaneBindings.Count -eq 14) -Message "Coverage evidence must bind every exact checked-in shard and unsharded test lane."
Assert-True -Condition ((@($coverageLaneBindings.Keys | Sort-Object -CaseSensitive) -join "`n") -ceq ($expectedCoverageLaneNames -join "`n")) -Message "Coverage lane bindings must equal the required-gate test profiles exactly."
Assert-True -Condition ((Get-VerificationCoverageLaneTestProjectName -Bindings $coverageLaneBindings -LaneName "tests-EmbodySense.Core.Persistence.Tests-shard-4") -ceq "EmbodySense.Core.Persistence.Tests") -Message "A Persistence shard must bind its exact owning test project."
Assert-True -Condition ((Get-VerificationCoverageLaneTestProjectName -Bindings $coverageLaneBindings -LaneName "tests-EmbodySense.Core.Startup.Tests-runtime") -ceq "EmbodySense.Core.Startup.Tests") -Message "The serialized Startup runtime shard must bind its exact owning test project."
Invoke-ExpectedFailure -ExpectedMessage "does not bind one exact checked-in test lane" -Action {
    Get-VerificationCoverageLaneTestProjectName -Bindings $coverageLaneBindings -LaneName "tests-EmbodySense.Core.Persistence.Tests-invented"
} | Out-Null
Assert-VerificationCoverageLaneInventory -Bindings $coverageLaneBindings -ObservedLaneNames $expectedCoverageLaneNames
Invoke-ExpectedFailure -ExpectedMessage "missing=tests-EmbodySense.Core.Persistence.Tests-shard-4" -Action {
    Assert-VerificationCoverageLaneInventory -Bindings $coverageLaneBindings -ObservedLaneNames @($expectedCoverageLaneNames | Where-Object { $_ -cne "tests-EmbodySense.Core.Persistence.Tests-shard-4" })
} | Out-Null
Invoke-ExpectedFailure -ExpectedMessage "unexpected=tests-EmbodySense.Core.Persistence.Tests-invented" -Action {
    Assert-VerificationCoverageLaneInventory -Bindings $coverageLaneBindings -ObservedLaneNames @($expectedCoverageLaneNames + "tests-EmbodySense.Core.Persistence.Tests-invented")
} | Out-Null
$coverageVerifierScript = Get-Content -LiteralPath (Join-Path $repoRoot "scripts/verify-coverage.ps1") -Raw
Assert-Contains -Actual $coverageVerifierScript -Expected "Get-VerificationCoverageLaneBindings -TestProjects `$canonicalTestProjects" -Message "Coverage reduction must bind reports to the exact checked-in lane map."
Assert-Contains -Actual $coverageVerifierScript -Expected "Assert-VerificationCoverageLaneInventory -Bindings `$coverageLaneBindings -ObservedLaneNames @(`$laneNames)" -Message "Coverage reduction must require every checked-in lane report exactly once."
$sourceProjects = @(Get-ChildItem -Path (Join-Path $repoRoot "src") -Directory -Recurse | Where-Object {
    Test-Path -LiteralPath (Join-Path $_.FullName "$($_.Name).csproj") -PathType Leaf
} | Sort-Object FullName)
Assert-True -Condition ($coverageOwnership.Owners.Count -eq $sourceProjects.Count) -Message "Coverage ownership must assign every production project to one primary test lane."
Assert-True -Condition ($coverageOwnership.TestProjectNames.Count -eq $testProjects.Count) -Message "Coverage ownership must classify every canonical test project."
$actualProductionFiles = @($sourceProjects | ForEach-Object {
    Get-ChildItem -LiteralPath $_.FullName -Recurse -Filter "*.cs" -File | Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } | ForEach-Object {
        [IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\', '/')
    }
} | Sort-Object -CaseSensitive)
Assert-True -Condition (($actualProductionFiles -join "`n") -ceq (@($coverageOwnership.ProductionFiles) -join "`n")) -Message "Coverage ownership must derive its complete production source inventory from the exact current tree, including zero-hit files."
Assert-True -Condition ($coverageOwnership.OwnershipSha256 -cmatch '^[0-9a-f]{64}$') -Message "Coverage ownership must expose one deterministic structural digest for auditable equivalence evidence."
Assert-True -Condition ($coverageOwnership.CollectorVersion -ceq "10.0.1") -Message "Coverage ownership must bind the one exact resolved collector version."
$lineSetProbe = Get-VerificationCoverageLineSetEvidence -Ownership $coverageOwnership -RepositoryRoot $repoRoot -Lines @(
    [pscustomobject]@{ Package = "EmbodySense.Core.Clients"; File = (Join-Path $repoRoot "src/EmbodySense.Core.Clients/obj/Release/net10.0/System.Text.RegularExpressions.Generator/RegexGenerator.g.cs"); Line = 9; Hits = 3 }
    [pscustomobject]@{ Package = "EmbodySense.Core.Common"; File = (Join-Path $repoRoot "src/EmbodySense.Core.Common/Authority/AuthorityActorId.cs"); Line = 7; Hits = 0 }
)
Assert-True -Condition ($lineSetProbe.CoverableLineCount -eq 2 -and $lineSetProbe.CoverableLineSha256 -ceq "e7ca37bc7ee071add7faaed9d5960f272b475a5c999c51c6ddffce29572b86b3") -Message "Coverage evidence must preserve the canonical package/path/line denominator serializer, including generated production source."
Assert-True -Condition ($lineSetProbe.HitLineCount -eq 1 -and $lineSetProbe.HitLineSha256 -ceq "135bc1d2e0a09fed310d906e70c8774e7af9a00a079d7378b2d943bc55149e30") -Message "Coverage evidence must preserve the canonical package/path/line hit serializer, including generated production source."
$coverageSettingsProbeRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-coverage-ownership-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $coverageSettingsProbeRoot | Out-Null
try {
    $canonicalRunSettingsPath = Join-Path $repoRoot "tests/verification-pull-request.runsettings"
    $canonicalRunSettingsHash = (Get-FileHash -LiteralPath $canonicalRunSettingsPath -Algorithm SHA256).Hash
    foreach ($testProject in $testProjects) {
        $selection = Get-VerificationCoverageSelection -Ownership $coverageOwnership -TestProject $testProject
        Assert-True -Condition ($selection.SelectedFiles.Count -gt 0) -Message "Coverage lane '$($testProject.BaseName)' must retain at least one authenticated production source file."
        Assert-True -Condition ($selection.IncludedPackages.Count -gt 0) -Message "Coverage lane '$($testProject.BaseName)' must instrument at least one exact production assembly."
        Assert-True -Condition (@($selection.IncludeAssemblyPatterns | Where-Object { $_ -cnotmatch '^\[EmbodySense(?:\.[A-Za-z0-9]+)+\]\*$' }).Count -eq 0) -Message "Coverage lane '$($testProject.BaseName)' must encode only exact production assembly includes."
        Assert-True -Condition (($selection.SelectedFiles.Count + $selection.ExcludedFiles.Count) -eq $coverageOwnership.ProductionFiles.Count) -Message "Coverage lane '$($testProject.BaseName)' must partition the exact production source inventory."
        Assert-True -Condition (@(Compare-Object -ReferenceObject $coverageOwnership.ProductionFiles -DifferenceObject @($selection.SelectedFiles + $selection.ExcludedFiles) -CaseSensitive).Count -eq 0) -Message "Coverage lane '$($testProject.BaseName)' selection and exclusion sets must cover the exact current source inventory."
        foreach ($primaryRoot in @($selection.PrimaryRoots)) {
            $missingPrimaryFiles = @($coverageOwnership.ProductionFiles | Where-Object {
                $_.StartsWith("$primaryRoot/", [StringComparison]::Ordinal) -and $selection.SelectedFiles -cnotcontains $_
            })
            Assert-True -Condition ($missingPrimaryFiles.Count -eq 0) -Message "Coverage lane '$($testProject.BaseName)' must instrument every file, including zero-hit files, in primary source root '$primaryRoot'."
        }
        if ($coverageOwnership.ExceptionsByTestProject.ContainsKey($testProject.BaseName)) {
            Assert-True -Condition (@($coverageOwnership.ExceptionsByTestProject[$testProject.BaseName] | Where-Object { $selection.SelectedFiles -cnotcontains $_ }).Count -eq 0) -Message "Coverage lane '$($testProject.BaseName)' must retain every authenticated cross-owner exception file."
        }

        $generatedRunSettingsPath = Join-Path $coverageSettingsProbeRoot "$($testProject.BaseName).runsettings"
        Write-VerificationCoverageRunSettings -SourcePath $canonicalRunSettingsPath -DestinationPath $generatedRunSettingsPath -Selection $selection
        [xml]$generatedRunSettings = Get-Content -LiteralPath $generatedRunSettingsPath -Raw
        $configuration = $generatedRunSettings.RunSettings.DataCollectionRunSettings.DataCollectors.DataCollector.Configuration
        Assert-True -Condition ([string]$configuration.SingleHit -ceq "true") -Message "Coverage lane '$($testProject.BaseName)' must retain canonical single-hit collection."
        $includeNodes = @($configuration.SelectNodes("Include"))
        Assert-True -Condition ($includeNodes.Count -eq 1) -Message "Coverage lane '$($testProject.BaseName)' must generate one exact Coverlet assembly include list."
        $actualIncludes = @(([string]$includeNodes[0].InnerText).Split(',', [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -CaseSensitive)
        $expectedIncludes = @($selection.IncludeAssemblyPatterns | Sort-Object -CaseSensitive)
        Assert-True -Condition (($actualIncludes -join "`n") -ceq ($expectedIncludes -join "`n")) -Message "Coverage lane '$($testProject.BaseName)' generated settings must exactly encode its primary and exception-owner assemblies."
        $expectedExcludeNodeCount = if ($selection.ExcludeByFilePatterns.Count -eq 0) { 0 } else { 1 }
        $excludeNodes = @($configuration.SelectNodes("ExcludeByFile"))
        Assert-True -Condition ($excludeNodes.Count -eq $expectedExcludeNodeCount) -Message "Coverage lane '$($testProject.BaseName)' must generate only the necessary exact Coverlet exclusion list."
        $actualExclusions = if ($expectedExcludeNodeCount -eq 0) { @() } else { @(([string]$excludeNodes[0].InnerText).Split(',', [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -CaseSensitive) }
        $expectedExclusions = @($selection.ExcludeByFilePatterns | Sort-Object -CaseSensitive)
        Assert-True -Condition (($actualExclusions -join "`n") -ceq ($expectedExclusions -join "`n")) -Message "Coverage lane '$($testProject.BaseName)' generated settings must exactly encode the computed exclusion complement."
        Assert-True -Condition (@($actualExclusions | Where-Object { $_ -match '/(?:bin|obj)/' }).Count -eq 0) -Message "Coverage lane '$($testProject.BaseName)' cannot exclude production bin/obj source that contributes to the existing denominator."
    }
    Assert-True -Condition ((Get-FileHash -LiteralPath $canonicalRunSettingsPath -Algorithm SHA256).Hash -ceq $canonicalRunSettingsHash) -Message "Lane-specific generation cannot mutate the canonical coverage settings input."
}
finally {
    if (Test-Path -LiteralPath $coverageSettingsProbeRoot) { Remove-Item -LiteralPath $coverageSettingsProbeRoot -Recurse -Force }
}

$binaryEvidenceProbeRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-binary-evidence-" + [Guid]::NewGuid().ToString("N"))
$binaryEvidenceRoot = Join-Path $binaryEvidenceProbeRoot "canonical"
New-Item -ItemType Directory -Path (Join-Path $binaryEvidenceRoot "symbols") -Force | Out-Null
try {
    $binaryPath = Join-Path $binaryEvidenceRoot "assembly.dll"
    $symbolsPath = Join-Path $binaryEvidenceRoot "symbols/assembly.pdb"
    [IO.File]::WriteAllBytes($binaryPath, [byte[]](1, 2, 3, 4))
    [IO.File]::WriteAllBytes($symbolsPath, [byte[]](5, 6, 7, 8))
    $binaryEntries = @(
        [pscustomobject][ordered]@{ path = "assembly.dll"; length = [long]4; sha256 = (Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        [pscustomobject][ordered]@{ path = "symbols/assembly.pdb"; length = [long]4; sha256 = (Get-FileHash -LiteralPath $symbolsPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    )
    $binaryEvidence = Read-VerificationCoverageBinaryInventoryEvidence -Entries $binaryEntries -BinaryRoot $binaryEvidenceRoot -ResultsRoot $binaryEvidenceProbeRoot -Description "Binary evidence probe"
    Assert-True -Condition ($binaryEvidence.Count -eq 2 -and $binaryEvidence.Sha256 -cmatch '^[0-9a-f]{64}$') -Message "Binary equivalence evidence must authenticate the complete recursive DLL/PDB inventory."

    $traversalEntries = @(
        [pscustomobject][ordered]@{ path = "sub/../../assembly.dll"; length = [long]4; sha256 = $binaryEntries[0].sha256 }
        $binaryEntries[1]
    )
    $null = Invoke-ExpectedFailure -ExpectedMessage "invalid, unsafe, or duplicate DLL/PDB entry" -Action {
        Read-VerificationCoverageBinaryInventoryEvidence -Entries $traversalEntries -BinaryRoot $binaryEvidenceRoot -ResultsRoot $binaryEvidenceProbeRoot -Description "Traversal probe"
    }
    $null = Invoke-ExpectedFailure -ExpectedMessage "does not exactly equal the actual recursive DLL/PDB inventory" -Action {
        Read-VerificationCoverageBinaryInventoryEvidence -Entries @($binaryEntries[0]) -BinaryRoot $binaryEvidenceRoot -ResultsRoot $binaryEvidenceProbeRoot -Description "Omission probe"
    }

    [IO.File]::WriteAllBytes((Join-Path $binaryEvidenceRoot "unexpected.dll"), [byte[]](9, 10, 11, 12))
    $null = Invoke-ExpectedFailure -ExpectedMessage "does not exactly equal the actual recursive DLL/PDB inventory" -Action {
        Read-VerificationCoverageBinaryInventoryEvidence -Entries $binaryEntries -BinaryRoot $binaryEvidenceRoot -ResultsRoot $binaryEvidenceProbeRoot -Description "Extra binary probe"
    }
}
finally {
    if (Test-Path -LiteralPath $binaryEvidenceProbeRoot) { Remove-Item -LiteralPath $binaryEvidenceProbeRoot -Recurse -Force }
}

$sourceProjectProbeRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-source-project-evidence-" + [Guid]::NewGuid().ToString("N"))
$sourcePackage = "EmbodySense.Core.OwnershipProbe"
$testPackage = "$sourcePackage.Tests"
$sourcePackageRoot = Join-Path $sourceProjectProbeRoot "src/$sourcePackage"
$testPackageRoot = Join-Path $sourceProjectProbeRoot "tests/$testPackage"
New-Item -ItemType Directory -Path $sourcePackageRoot, $testPackageRoot -Force | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $sourcePackageRoot "$sourcePackage.csproj"), '<Project Sdk="Microsoft.NET.Sdk" />', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sourcePackageRoot "OwnedSource.cs"), 'namespace EmbodySense.Core.OwnershipProbe;', [Text.UTF8Encoding]::new($false))
    $testProjectPath = Join-Path $testPackageRoot "$testPackage.csproj"
    [IO.File]::WriteAllText($testProjectPath, '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="coverlet.collector" Version="10.0.1" /></ItemGroup></Project>', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $sourceProjectProbeRoot "tests/verification-pull-request.runsettings"), '<RunSettings />', [Text.UTF8Encoding]::new($false))
    $sourceProjectManifestPath = Join-Path $sourceProjectProbeRoot "tests/verification-coverage-ownership.json"
    $sourceProjectManifest = [ordered]@{
        schemaVersion = 1
        exceptions = [ordered]@{}
        owners = @([ordered]@{ package = $sourcePackage; sourceRoot = "src/$sourcePackage"; testProject = $testPackage })
    }
    [IO.File]::WriteAllText($sourceProjectManifestPath, ($sourceProjectManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $probeTestProjects = [IO.FileInfo[]]@((Get-Item -LiteralPath $testProjectPath))
    $sourceProjectOwnership = Read-VerificationCoverageOwnership -ManifestPath $sourceProjectManifestPath -RepositoryRoot $sourceProjectProbeRoot -TestProjects $probeTestProjects
    Assert-True -Condition ($sourceProjectOwnership.Owners.Count -eq 1) -Message "Canonical production project inventory must admit one exact owned source project."

    [IO.File]::WriteAllText((Join-Path $sourcePackageRoot "Unexpected.csproj"), '<Project Sdk="Microsoft.NET.Sdk" />', [Text.UTF8Encoding]::new($false))
    $null = Invoke-ExpectedFailure -ExpectedMessage "must use one owned canonical src/<package>/<package>.csproj path" -Action {
        Read-VerificationCoverageOwnership -ManifestPath $sourceProjectManifestPath -RepositoryRoot $sourceProjectProbeRoot -TestProjects $probeTestProjects
    }
}
finally {
    if (Test-Path -LiteralPath $sourceProjectProbeRoot) { Remove-Item -LiteralPath $sourceProjectProbeRoot -Recurse -Force }
}

try {
    Get-VerificationCoverageSelection -Ownership $coverageOwnership -TestProject ([IO.FileInfo]::new((Join-Path $repoRoot "tests/Unclassified.Tests.csproj"))) | Out-Null
    throw "Expected unclassified coverage lane rejection."
}
catch {
    Assert-Contains -Actual $_.Exception.Message -Expected "unclassified test project" -Message "A new test project without explicit coverage ownership must fail closed."
}
foreach ($testProject in $testProjects) {
    foreach ($lane in @(Get-VerificationTestProjectLanes -TestProject $testProject)) {
        $declaredRequiredGateNames.Add("tests-$($testProject.BaseName)-$($lane.Name)")
    }
}
$declaredRequiredGateProfiles = @($declaredRequiredGateNames | ForEach-Object { Get-VerificationRequiredGateScheduleProfile -Name $_ })
Assert-VerificationRequiredGateSchedule -Phases $declaredRequiredGateProfiles
Assert-True -Condition ($declaredRequiredGateProfiles.Count -eq $declaredRequiredGateNames.Count) -Message "Every dynamically declared required gate must resolve to one checked-in profile."
Assert-True -Condition ($declaredRequiredGateProfiles.Count -eq $requiredGateProfiles.Count) -Message "The checked-in scheduling catalog cannot retain stale profiles for gates outside the current plan."
foreach ($processHeavyGateName in @(
    "tests-EmbodySense.Core.Persistence.Tests-shard-1"
    "tests-EmbodySense.Core.Persistence.Tests-shard-2"
    "tests-EmbodySense.Core.Persistence.Tests-shard-3"
    "tests-EmbodySense.Core.Persistence.Tests-shard-4"
    "tests-EmbodySense.Core.Startup.Tests-runtime"
    "tests-EmbodySense.Core.Startup.Tests-shard-1"
    "tests-EmbodySense.Core.Startup.Tests-shard-2"
    "tests-EmbodySense.IntegrationTests-all"
    "tests-EmbodySense.Web.Tests-all"
)) {
    $processHeavyProfile = Get-VerificationRequiredGateScheduleProfile -Name $processHeavyGateName
    Assert-True -Condition ($processHeavyProfile.Weight -eq 3 -and $processHeavyProfile.ResourceClass -ceq "ProcessHeavy") -Message "A coverage-instrumented process-heavy gate '$processHeavyGateName' must retain its bounded logical weight."
}
$formatProfile = Get-VerificationRequiredGateScheduleProfile -Name "format-csharp"
Assert-True -Condition ($formatProfile.EstimatedDurationSeconds -eq 100 -and $formatProfile.Weight -eq 6 -and $formatProfile.ResourceClass -ceq "CpuBound") -Message "The combined formatter must retain one bounded solution load and an explicit six-unit reservation."

$requiredGateVirtualSchedule = Get-VirtualVerificationSchedule -Profiles $requiredGateProfiles -MaximumWorkers 4 -MaximumResourceCapacity 12 -MaximumProcessHeavyWorkers 4 -MaximumCpuBoundWorkers 1
Assert-True -Condition ($requiredGateVirtualSchedule.MakespanSeconds -eq 425) -Message "The conservative checked-in profiles must retain the exact four-shard virtual schedule; this is not authoritative whole-run timing proof. Actual estimate: $($requiredGateVirtualSchedule.MakespanSeconds)."
foreach ($initialHeavyGateName in @("tests-EmbodySense.Web.Tests-all", "tests-EmbodySense.Core.Startup.Tests-runtime", "tests-EmbodySense.IntegrationTests-all", "tests-EmbodySense.Core.Persistence.Tests-shard-1")) {
    Assert-True -Condition ($requiredGateVirtualSchedule.Starts[$initialHeavyGateName] -eq 0) -Message "The four longest admitted heavy lanes must start at virtual second zero."
}
$initialResourceCapacity = ($requiredGateProfiles | Where-Object { $requiredGateVirtualSchedule.Starts[$_.Name] -eq 0 } | Measure-Object -Property Weight -Sum).Sum
Assert-True -Condition ($initialResourceCapacity -eq 12) -Message "Four exact process-heavy lanes must fill all twelve logical units without admitting a fifth outer process."
Assert-True -Condition ($requiredGateVirtualSchedule.Starts["format-csharp"] -gt 0) -Message "The combined formatter must wait until two three-unit heavy reservations have drained."
Assert-True -Condition ($requiredGateVirtualSchedule.Starts["tests-EmbodySense.Core.Persistence.Tests-shard-2"] -eq 115) -Message "The next balanced Persistence shard must immediately reuse the first released heavy slot."
$processHeavyProfiles = @($requiredGateProfiles | Where-Object ResourceClass -CEQ "ProcessHeavy")
foreach ($startSecond in @($processHeavyProfiles | ForEach-Object { $requiredGateVirtualSchedule.Starts[$_.Name] } | Sort-Object -Unique)) {
    $activeProcessHeavy = @($processHeavyProfiles | Where-Object {
        $phaseStart = $requiredGateVirtualSchedule.Starts[$_.Name]
        $phaseStart -le $startSecond -and ($phaseStart + $_.EstimatedDurationSeconds) -gt $startSecond
    })
    Assert-True -Condition ($activeProcessHeavy.Count -le 4) -Message "The virtual required-gate schedule must never overlap more than four exact immutable process-heavy lanes."
}
Assert-True -Condition ($requiredGateVirtualSchedule.Starts["format-csharp"] -eq 325) -Message "The combined format gate must start at the exact conservative two-slot opening, not oversubscribe the four-heavy wave."

$counterexamplePhases = @(
    [pscustomobject]@{ Name = "long-ordinary"; EstimatedDurationSeconds = 100; SchedulingPrioritySeconds = 100; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "long-heavy"; EstimatedDurationSeconds = 90; SchedulingPrioritySeconds = 90; ResourceClass = "ProcessHeavy" }
    [pscustomobject]@{ Name = "short-cpu"; EstimatedDurationSeconds = 5; SchedulingPrioritySeconds = 5; ResourceClass = "CpuBound" }
)
$counterexampleOrder = @(Get-VerificationParallelPhaseSchedulingOrder -Phases $counterexamplePhases -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
Assert-True -Condition ($counterexampleOrder[0].Name -ceq "long-ordinary" -and $counterexampleOrder[1].Name -ceq "long-heavy" -and $counterexampleOrder[2].Name -ceq "short-cpu") -Message "A singleton CPU class with no backlog cannot jump ahead of longer ordinary or process-heavy work unconditionally."
Assert-True -Condition ($counterexampleOrder[2].SchedulingPrioritySeconds -eq 5) -Message "A singleton class's static priority must equal its initial backlog rather than an unconditional class boost."

$priorityTiePhases = @(
    [pscustomobject]@{ Name = "ordinary-long"; EstimatedDurationSeconds = 100; SchedulingPrioritySeconds = 100; ResourceClass = "Ordinary" }
    [pscustomobject]@{ Name = "cpu-zulu"; EstimatedDurationSeconds = 50; SchedulingPrioritySeconds = 50; ResourceClass = "CpuBound" }
    [pscustomobject]@{ Name = "cpu-alpha"; EstimatedDurationSeconds = 50; SchedulingPrioritySeconds = 50; ResourceClass = "CpuBound" }
)
$priorityTieOrder = @(Get-VerificationParallelPhaseSchedulingOrder -Phases $priorityTiePhases -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
Assert-True -Condition ($priorityTieOrder[0].Name -ceq "ordinary-long" -and $priorityTieOrder[1].Name -ceq "cpu-alpha" -and $priorityTieOrder[2].Name -ceq "cpu-zulu") -Message "Scheduling priority ties must fall back to duration and then exact name deterministically."
Assert-True -Condition ($priorityTieOrder[1].SchedulingPrioritySeconds -eq 100 -and $priorityTieOrder[2].SchedulingPrioritySeconds -eq 100) -Message "Every phase in a singleton-limited class must receive the same initial static backlog priority."
try {
    Get-VerificationRequiredGateScheduleProfile -Name "tests-unprofiled-gate" | Out-Null
    throw "Expected missing scheduling profile failure."
}
catch {
    Assert-Contains -Actual $_.Exception.Message -Expected "must have exactly one checked-in scheduling profile" -Message "A new gate without a checked-in duration/resource profile must fail closed."
}

try {
    Assert-VerificationRequiredGateSchedule -Phases @($declaredRequiredGateProfiles | Select-Object -Skip 1)
    throw "Expected stale scheduling profile failure."
}
catch {
    Assert-Contains -Actual $_.Exception.Message -Expected "unexpected_profiles=[" -Message "A profile without a current declared gate must fail closed."
}

$scenarioRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-parallel-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
try {
    $probePath = Join-Path $scenarioRoot "probe.ps1"
    @'
param([string]$Name, [int]$DelayMilliseconds, [int]$ExitCode, [string]$OrderPath, [string]$SynchronizationRoot, [int]$ExpectedConcurrent)
if (-not [string]::IsNullOrWhiteSpace($OrderPath) -and $OrderPath -cne "-") { Add-Content -LiteralPath $OrderPath -Value $Name }
if (-not [string]::IsNullOrWhiteSpace($SynchronizationRoot) -and $SynchronizationRoot -cne "-" -and $ExpectedConcurrent -gt 0) {
    New-Item -ItemType Directory -Path $SynchronizationRoot -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $SynchronizationRoot "$Name.ready") -Value "ready" -Encoding UTF8
    $synchronizationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while (@(Get-ChildItem -LiteralPath $SynchronizationRoot -Filter "*.ready" -File).Count -lt $ExpectedConcurrent) {
        if ([DateTimeOffset]::UtcNow -ge $synchronizationDeadline) { exit 41 }
        Start-Sleep -Milliseconds 10
    }
}
Start-Sleep -Milliseconds $DelayMilliseconds
Write-Output "probe=$Name"
Write-Output "environment=$env:VERIFY_PARALLEL_PROBE"
Write-Output "physical_temp=$([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))"
exit $ExitCode
'@ | Set-Content -LiteralPath $probePath -Encoding UTF8

    $baseArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $baseArguments += @("-ExecutionPolicy", "Bypass")
    }
    $baseArguments += @("-File", $probePath)
    $sixUnitCapacity = 6
    $synchronizationRoot = Join-Path $scenarioRoot "overlap"
    Reset-VerificationParallelPhaseState
    foreach ($name in @("first", "second", "third", "fourth", "fifth", "sixth")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($baseArguments + @($name, "50", "0", "-", $synchronizationRoot, $sixUnitCapacity.ToString([Globalization.CultureInfo]::InvariantCulture))) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log")
    }
    $results = @(Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity)

    Assert-True -Condition ($results.Count -eq 6) -Message "Every successful parallel phase must be aggregated."
    Assert-True -Condition (@(Get-ChildItem -LiteralPath $synchronizationRoot -Filter "*.ready" -File).Count -eq 6) -Message "Six ordinary probes must be able to pack the explicit six-unit logical capacity."
    Assert-Contains -Actual (Get-Content -Raw (Join-Path $scenarioRoot "first.log")) -Expected "probe=first" -Message "Each phase must retain isolated output."

    $weightedProbePath = Join-Path $scenarioRoot "weighted-probe.ps1"
    @'
param([string]$Name, [string]$ActiveRoot, [int]$ExpectedConcurrent, [int]$MaximumExpectedConcurrent)
$activePath = Join-Path $ActiveRoot "$Name.active"
try {
    Set-Content -LiteralPath $activePath -Value "active" -Encoding UTF8
    $synchronizationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    while (@(Get-ChildItem -LiteralPath $ActiveRoot -Filter "*.active" -File).Count -lt $ExpectedConcurrent) {
        if ([DateTimeOffset]::UtcNow -ge $synchronizationDeadline) { exit 42 }
        Start-Sleep -Milliseconds 10
    }
    $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds(250)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $activeCount = @(Get-ChildItem -LiteralPath $ActiveRoot -Filter "*.active" -File).Count
        if ($activeCount -gt $MaximumExpectedConcurrent) {
            Write-Output "overcommitted=$activeCount"
            exit 43
        }
        Start-Sleep -Milliseconds 10
    }
    Write-Output "weighted_probe=$Name"
}
finally {
    Remove-Item -LiteralPath $activePath -Force -ErrorAction SilentlyContinue
}
'@ | Set-Content -LiteralPath $weightedProbePath -Encoding UTF8
    $weightedArguments = @("-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $weightedArguments += @("-ExecutionPolicy", "Bypass")
    }
    $weightedArguments += @("-File", $weightedProbePath)
    $activeRoot = Join-Path $scenarioRoot "weighted-active"
    New-Item -ItemType Directory -Path $activeRoot | Out-Null
    $heavyWeight = 3
    $expectedHeavyConcurrency = [Math]::Floor($sixUnitCapacity / $heavyWeight)
    Reset-VerificationParallelPhaseState
    foreach ($name in @("heavy-first", "heavy-second", "heavy-third", "heavy-fourth")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $activeRoot, $expectedHeavyConcurrency.ToString([Globalization.CultureInfo]::InvariantCulture), $expectedHeavyConcurrency.ToString([Globalization.CultureInfo]::InvariantCulture))) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log") -Weight $heavyWeight -ResourceClass ProcessHeavy
    }
    $weightedResults = @(Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity)
    Assert-True -Condition ($weightedResults.Count -eq 4) -Message "Every weighted phase must be scheduled and aggregated."
    Assert-True -Condition (@($weightedResults | Where-Object { $_.Weight -eq $heavyWeight -and $_.EffectiveWeight -eq $heavyWeight -and $_.ResourceClass -ceq "ProcessHeavy" }).Count -eq 4) -Message "Weighted result evidence must preserve the declared process-heavy posture without adapting its weight downward."

    $physicalHeavyRoot = Join-Path $scenarioRoot "physical-heavy-active"
    New-Item -ItemType Directory -Path $physicalHeavyRoot | Out-Null
    Reset-VerificationParallelPhaseState
    foreach ($name in @("physical-heavy-first", "physical-heavy-second")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $physicalHeavyRoot, "2", "2")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log") -Weight 3 -ResourceClass ProcessHeavy
    }
    $physicalHeavyResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 4 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
    Assert-True -Condition ($physicalHeavyResults.Count -eq 2) -Message "Two evidence-weighted process-heavy phases must overlap within eight logical units and the four-process ceiling."

    $exclusiveRoot = Join-Path $scenarioRoot "full-capacity-active"
    New-Item -ItemType Directory -Path $exclusiveRoot | Out-Null
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "full-capacity-format" -FileName $powerShellExecutable -Arguments ($weightedArguments + @("full-capacity-format", $exclusiveRoot, "1", "1")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "full-capacity-format.log") -EstimatedDurationSeconds 2 -Weight 4 -ResourceClass CpuBound
    Add-VerificationParallelPhase -Name "excluded-ordinary" -FileName $powerShellExecutable -Arguments ($weightedArguments + @("excluded-ordinary", $exclusiveRoot, "1", "1")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "excluded-ordinary.log")
    $exclusiveResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 4 -MaximumResourceCapacity 4 -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
    Assert-True -Condition ($exclusiveResults.Count -eq 2) -Message "A full-capacity CPU phase must exclude ordinary work and then allow the plan to drain."

    foreach ($resourceClassScenario in @(
        [pscustomobject]@{ Name = "process-heavy"; ResourceClass = "ProcessHeavy"; Weight = 3; Maximum = 2; Count = 4 },
        [pscustomobject]@{ Name = "cpu-bound"; ResourceClass = "CpuBound"; Weight = 2; Maximum = 1; Count = 3 }
    )) {
        $resourceClassRoot = Join-Path $scenarioRoot "$($resourceClassScenario.Name)-active"
        New-Item -ItemType Directory -Path $resourceClassRoot | Out-Null
        Reset-VerificationParallelPhaseState
        foreach ($index in 1..$resourceClassScenario.Count) {
            $name = "$($resourceClassScenario.Name)-$index"
            Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $resourceClassRoot, $resourceClassScenario.Maximum.ToString([Globalization.CultureInfo]::InvariantCulture), $resourceClassScenario.Maximum.ToString([Globalization.CultureInfo]::InvariantCulture))) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log") -Weight $resourceClassScenario.Weight -ResourceClass $resourceClassScenario.ResourceClass
        }

        $resourceClassResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 6 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 2 -MaximumCpuBoundWorkers 1)
        Assert-True -Condition ($resourceClassResults.Count -eq $resourceClassScenario.Count) -Message "The $($resourceClassScenario.Name) concurrency-limit proof must drain every phase."
    }

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "one-worker-heavy" -FileName $powerShellExecutable -Arguments ($baseArguments + @("one-worker-heavy", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "one-worker-heavy.log") -Weight 3 -ResourceClass ProcessHeavy
    $oneWorkerResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 1 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 1 -MaximumCpuBoundWorkers 1)
    Assert-True -Condition ($oneWorkerResults.Count -eq 1 -and $oneWorkerResults[0].ResourceClass -ceq "ProcessHeavy") -Message "One-worker hosts must preserve process-heavy execution after effective class-limit capping."

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "invalid-resource-limit" -FileName $powerShellExecutable -Arguments ($baseArguments + @("invalid-resource-limit", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "invalid-resource-limit.log")
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers 2 -MaximumResourceCapacity 8 -MaximumProcessHeavyWorkers 3 | Out-Null
        throw "Expected invalid resource-class concurrency limit failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "resource-class limits cannot exceed the maximum worker count" -Message "Resource-class concurrency limits must fail closed when they exceed the worker ceiling."
    }

    $workerBoundRoot = Join-Path $scenarioRoot "worker-bound-active"
    New-Item -ItemType Directory -Path $workerBoundRoot | Out-Null
    $workerBound = 2
    Reset-VerificationParallelPhaseState
    foreach ($name in @("worker-first", "worker-second", "worker-third", "worker-fourth")) {
        Add-VerificationParallelPhase -Name $name -FileName $powerShellExecutable -Arguments ($weightedArguments + @($name, $workerBoundRoot, $workerBound.ToString([Globalization.CultureInfo]::InvariantCulture), $workerBound.ToString([Globalization.CultureInfo]::InvariantCulture))) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "$name.log")
    }
    $workerBoundResults = @(Invoke-VerificationParallelPhases -MaximumWorkers $workerBound -MaximumResourceCapacity $sixUnitCapacity)
    Assert-True -Condition ($workerBoundResults.Count -eq 4) -Message "A worker ceiling below logical capacity must still drain every admitted phase."
    Assert-True -Condition (@($workerBoundResults | Where-Object { $_.Weight -eq 1 }).Count -eq 4) -Message "The worker-ceiling proof must use ordinary phases that logical capacity alone would admit together."

    $fairPending = [Collections.Generic.List[object]]::new()
    $fairPending.Add([pscustomobject]@{ Name = "heavy"; EffectiveWeight = 3; SchedulingDeferrals = 0 })
    $fairPending.Add([pscustomobject]@{ Name = "ordinary-one"; EffectiveWeight = 1; SchedulingDeferrals = 0 })
    $fairPending.Add([pscustomobject]@{ Name = "ordinary-two"; EffectiveWeight = 1; SchedulingDeferrals = 0 })
    $backfill = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($backfill.Name -ceq "ordinary-one") -Message "A fitting ordinary phase must backfill capacity behind a temporarily blocked heavy phase."
    $reserved = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($null -eq $reserved) -Message "A bypassed heavy phase must reserve the next fitting opportunity instead of starving behind ordinary work."
    $heavy = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 3
    Assert-True -Condition ($heavy.Name -ceq "heavy") -Message "A previously bypassed heavy phase must run as soon as its required capacity is available."
    $lastOrdinary = Select-VerificationParallelPhase -Pending $fairPending -AvailableCapacity 1
    Assert-True -Condition ($lastOrdinary.Name -ceq "ordinary-two" -and $fairPending.Count -eq 0) -Message "Fair reservation cannot lose or strand the remaining ordinary phase."

    $classLimitedPending = [Collections.Generic.List[object]]::new()
    $classLimitedPending.Add([pscustomobject]@{ Name = "saturated-heavy"; EffectiveWeight = 3; ResourceClass = "ProcessHeavy"; SchedulingDeferrals = 0 })
    $classLimitedPending.Add([pscustomobject]@{ Name = "ordinary-backfill"; EffectiveWeight = 1; ResourceClass = "Ordinary"; SchedulingDeferrals = 0 })
    $classBackfill = Select-VerificationParallelPhase -Pending $classLimitedPending -AvailableCapacity 5 -AvailableResourceClassSlots @{ Ordinary = 6; CpuBound = 1; ProcessHeavy = 0 }
    Assert-True -Condition ($classBackfill.Name -ceq "ordinary-backfill") -Message "A saturated resource class cannot reserve capacity needed by an admissible ordinary phase."
    Assert-True -Condition ($classLimitedPending[0].SchedulingDeferrals -eq 0) -Message "Class saturation must not count as capacity backfill against fairness accounting."

    $oversizedOutput = Join-Path $scenarioRoot "oversized.log"
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "oversized" -FileName $powerShellExecutable -Arguments ($baseArguments + @("oversized", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $oversizedOutput -Weight 7 -ResourceClass ProcessHeavy
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity | Out-Null
        throw "Expected oversized resource declaration failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "beyond logical resource capacity 6" -Message "A phase cannot weaken its declared weight by adapting it down to available capacity."
    }
    Assert-True -Condition (-not (Test-Path -LiteralPath $oversizedOutput)) -Message "An oversized phase must fail before execution."

    foreach ($underweightedClass in @("CpuBound", "ProcessHeavy")) {
        $underweightedOutput = Join-Path $scenarioRoot "underweighted-$underweightedClass.log"
        Reset-VerificationParallelPhaseState
        Add-VerificationParallelPhase -Name "underweighted-$underweightedClass" -FileName $powerShellExecutable -Arguments ($baseArguments + @("underweighted-$underweightedClass", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $underweightedOutput -Weight 1 -ResourceClass $underweightedClass
        try {
            Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity | Out-Null
            throw "Expected underweighted resource-class failure."
        }
        catch {
            Assert-Contains -Actual $_.Exception.Message -Expected "resource classes are underweighted" -Message "$underweightedClass phases must retain minimum logical-capacity protection."
        }
        Assert-True -Condition (-not (Test-Path -LiteralPath $underweightedOutput)) -Message "An underweighted $underweightedClass phase must fail before execution."
    }

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "dependency-prerequisite" -FileName $powerShellExecutable -Arguments ($baseArguments + @("dependency-prerequisite", "50", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "dependency-prerequisite.log")
    Add-VerificationParallelPhase -Name "dependency-dependent" -FileName $powerShellExecutable -Arguments ($baseArguments + @("dependency-dependent", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "dependency-dependent.log") -DependsOn @("dependency-prerequisite") -EstimatedDurationSeconds 100
    $dependencyResults = @(Invoke-VerificationParallelPhases -MaximumWorkers 2 -MaximumResourceCapacity 2)
    Assert-True -Condition ($dependencyResults.Count -eq 2 -and (Test-Path -LiteralPath (Join-Path $scenarioRoot "dependency-dependent.log"))) -Message "A successful prerequisite must admit its dependent phase even when the dependent has higher scheduling priority."

    Reset-VerificationParallelPhaseState
    $unknownDependencyOutput = Join-Path $scenarioRoot "unknown-dependency.log"
    Add-VerificationParallelPhase -Name "unknown-dependency" -FileName $powerShellExecutable -Arguments ($baseArguments + @("unknown-dependency", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $unknownDependencyOutput -DependsOn @("missing-prerequisite")
    $null = Invoke-ExpectedFailure -ExpectedMessage "depends on unknown phase 'missing-prerequisite'" -Action {
        Invoke-VerificationParallelPhases -MaximumWorkers 2 -MaximumResourceCapacity 2
    }
    Assert-True -Condition (-not (Test-Path -LiteralPath $unknownDependencyOutput)) -Message "An unknown dependency must fail before any child starts."

    Reset-VerificationParallelPhaseState
    $cycleFirstOutput = Join-Path $scenarioRoot "cycle-first.log"
    $cycleSecondOutput = Join-Path $scenarioRoot "cycle-second.log"
    Add-VerificationParallelPhase -Name "cycle-first" -FileName $powerShellExecutable -Arguments ($baseArguments + @("cycle-first", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $cycleFirstOutput -DependsOn @("cycle-second")
    Add-VerificationParallelPhase -Name "cycle-second" -FileName $powerShellExecutable -Arguments ($baseArguments + @("cycle-second", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $cycleSecondOutput -DependsOn @("cycle-first")
    $null = Invoke-ExpectedFailure -ExpectedMessage "dependencies contain a cycle" -Action {
        Invoke-VerificationParallelPhases -MaximumWorkers 2 -MaximumResourceCapacity 2
    }
    Assert-True -Condition (-not (Test-Path -LiteralPath $cycleFirstOutput) -and -not (Test-Path -LiteralPath $cycleSecondOutput)) -Message "A cyclic dependency graph must fail before any child starts."

    Reset-VerificationParallelPhaseState
    $failedDependencyOutput = Join-Path $scenarioRoot "failed-dependency.log"
    Add-VerificationParallelPhase -Name "failed-prerequisite" -FileName $powerShellExecutable -Arguments ($baseArguments + @("failed-prerequisite", "10", "17")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "failed-prerequisite.log")
    Add-VerificationParallelPhase -Name "failed-dependency" -FileName $powerShellExecutable -Arguments ($baseArguments + @("failed-dependency", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath $failedDependencyOutput -DependsOn @("failed-prerequisite")
    $null = Invoke-ExpectedFailure -ExpectedMessage "prerequisite phases did not pass: failed-prerequisite" -Action {
        Invoke-VerificationParallelPhases -MaximumWorkers 2 -MaximumResourceCapacity 2
    }
    Assert-True -Condition (-not (Test-Path -LiteralPath $failedDependencyOutput)) -Message "A failed prerequisite must prevent dependent process admission."

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "failure" -FileName $powerShellExecutable -Arguments ($baseArguments + @("failure", "50", "17")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "failure.log") -Weight 3 -ResourceClass ProcessHeavy
    Add-VerificationParallelPhase -Name "success" -FileName $powerShellExecutable -Arguments ($baseArguments + @("success", "300", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "success.log") -Weight 3 -ResourceClass ProcessHeavy
    try {
        Invoke-VerificationParallelPhases -MaximumWorkers $sixUnitCapacity -MaximumResourceCapacity $sixUnitCapacity | Out-Null
        throw "Expected aggregate failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "'failure' exited with code 17" -Message "Nonzero child exits must fail the aggregate with the exact phase."
    }

    Assert-Contains -Actual (Get-Content -Raw (Join-Path $scenarioRoot "success.log")) -Expected "probe=success" -Message "The harness must drain already-running peers before reporting failure."

    $laneTempRoot = Join-Path $scenarioRoot "lane-temp"
    New-Item -ItemType Directory -Path $laneTempRoot | Out-Null
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "environment" -FileName $powerShellExecutable -Arguments ($baseArguments + @("environment", "10", "0", "-")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "environment.log") -Environment @{ VERIFY_PARALLEL_PROBE = "scoped-child"; TEMP = $laneTempRoot; TMP = $laneTempRoot; TMPDIR = $laneTempRoot }
    Invoke-VerificationParallelPhases -MaximumResourceCapacity 1 | Out-Null
    $environmentLog = Get-Content -Raw (Join-Path $scenarioRoot "environment.log")
    Assert-Contains -Actual $environmentLog -Expected "environment=scoped-child" -Message "Per-phase environment overrides must reach only the child ProcessStartInfo."
    Assert-Contains -Actual $environmentLog -Expected "physical_temp=$([IO.Path]::GetFullPath($laneTempRoot))" -Message "A lane's .NET process and descendants must resolve the isolated fixture root as physical temporary storage."
    Assert-True -Condition ([string]::IsNullOrEmpty($env:VERIFY_PARALLEL_PROBE)) -Message "Per-phase environment overrides cannot mutate the verifier process environment."

    $orderPath = Join-Path $scenarioRoot "priority-order.txt"
    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "low" -FileName $powerShellExecutable -Arguments ($baseArguments + @("low", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "low.log") -EstimatedDurationSeconds 1
    Add-VerificationParallelPhase -Name "tie-zulu" -FileName $powerShellExecutable -Arguments ($baseArguments + @("tie-zulu", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "tie-zulu.log") -EstimatedDurationSeconds 50
    Add-VerificationParallelPhase -Name "tie-alpha" -FileName $powerShellExecutable -Arguments ($baseArguments + @("tie-alpha", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "tie-alpha.log") -EstimatedDurationSeconds 50
    Add-VerificationParallelPhase -Name "high" -FileName $powerShellExecutable -Arguments ($baseArguments + @("high", "10", "0", $orderPath)) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "high.log") -EstimatedDurationSeconds 100
    Invoke-VerificationParallelPhases -MaximumResourceCapacity 1 | Out-Null
    $order = @(Get-Content -LiteralPath $orderPath)
    Assert-True -Condition ($order.Count -eq 4 -and $order[0] -ceq "high" -and $order[1] -ceq "tie-alpha" -and $order[2] -ceq "tie-zulu" -and $order[3] -ceq "low") -Message "Ordinary phases must retain longest-estimate ordering, with exact-name ordering for deterministic ties."

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "timeout" -FileName $powerShellExecutable -Arguments ($baseArguments + @("timeout", "5000", "0")) -TimeoutSeconds 1 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "timeout.log")
    try {
        Invoke-VerificationParallelPhases -MaximumResourceCapacity 1 | Out-Null
        throw "Expected aggregate timeout."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "'timeout' timed out" -Message "Timeouts must kill the child tree and fail the aggregate."
    }

    Reset-VerificationParallelPhaseState
    Add-VerificationParallelPhase -Name "duplicate" -FileName $powerShellExecutable -Arguments ($baseArguments + @("one", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "one.log")
    try {
        Add-VerificationParallelPhase -Name "duplicate" -FileName $powerShellExecutable -Arguments ($baseArguments + @("two", "10", "0")) -TimeoutSeconds 10 -WorkingDirectory $scenarioRoot -OutputPath (Join-Path $scenarioRoot "two.log")
        throw "Expected duplicate declaration failure."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "declared more than once" -Message "Duplicate phase identities must fail before execution."
    }

    $artifactSource = Join-Path $scenarioRoot "artifact-source"
    $artifactCopy = Join-Path $scenarioRoot "artifact-copy"
    New-Item -ItemType Directory -Path (Join-Path $artifactSource "nested") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $artifactSource "nested\assembly.dll") -Value "immutable" -Encoding UTF8
    $artifactManifest = @(Copy-VerifiedDirectory -SourceDirectory $artifactSource -DestinationDirectory $artifactCopy -Description "contract artifact")
    Assert-VerificationDirectoryManifest -Expected $artifactManifest -Directory $artifactCopy -Description "unchanged contract artifact"
    Set-Content -LiteralPath (Join-Path $artifactCopy "nested\assembly.dll") -Value "substituted" -Encoding UTF8
    try {
        Assert-VerificationDirectoryManifest -Expected $artifactManifest -Directory $artifactCopy -Description "mutated contract artifact"
        throw "Expected immutable artifact verification to fail."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "failed immutable artifact verification" -Message "Artifact mutation or substitution must fail closed."
    }

    try {
        Copy-VerifiedDirectory -SourceDirectory $artifactSource -DestinationDirectory $artifactCopy -Description "stale destination" | Out-Null
        throw "Expected stale artifact destination rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "could substitute stale artifacts" -Message "Pre-existing lane output cannot be reused as a stale substitute."
    }

    $manifestCopy = Join-Path $scenarioRoot "manifest-copy"
    $manifestCopyResult = @(Copy-VerifiedDirectoryFromManifest -SourceDirectory $artifactSource -SourceManifest $artifactManifest -DestinationDirectory $manifestCopy -Description "authenticated manifest copy")
    Assert-VerificationDirectoryManifest -Expected $artifactManifest -Directory $manifestCopy -Description "authenticated manifest copy"
    Assert-True -Condition ($manifestCopyResult.Count -eq $artifactManifest.Count) -Message "A manifest-backed copy must retain every authenticated source entry exactly once."

    $staleManifestDestination = Join-Path $scenarioRoot "stale-manifest-destination"
    New-Item -ItemType Directory -Path $staleManifestDestination | Out-Null
    try {
        Copy-VerifiedDirectoryFromManifest -SourceDirectory $artifactSource -SourceManifest $artifactManifest -DestinationDirectory $staleManifestDestination -Description "stale manifest destination" | Out-Null
        throw "Expected stale manifest-backed destination rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "could substitute stale artifacts" -Message "A manifest-backed copy cannot reuse a pre-existing destination."
    }

    Set-Content -LiteralPath (Join-Path $artifactSource "nested\assembly.dll") -Value "source-mutated-after-manifest" -Encoding UTF8
    try {
        Copy-VerifiedDirectoryFromManifest -SourceDirectory $artifactSource -SourceManifest $artifactManifest -DestinationDirectory (Join-Path $scenarioRoot "mutated-source-copy") -Description "mutated manifest source" | Out-Null
        throw "Expected stale source manifest rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "failed immutable artifact verification" -Message "A source mutation after manifest capture must fail the manifest-backed copy closed."
    }

    $isolatedOutput = Get-VerificationIsolatedOutputPath -IsolationRoot (Join-Path $scenarioRoot "project\lane") -Configuration "Release" -TargetFramework "net10.0"
    $isolatedSegments = $isolatedOutput.Replace('\', '/').Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    $expectedSuffix = @("project", "lane", "bin", "Release", "net10.0")
    Assert-True -Condition (@(Compare-Object -ReferenceObject $expectedSuffix -DifferenceObject $isolatedSegments[($isolatedSegments.Count - $expectedSuffix.Count)..($isolatedSegments.Count - 1)] -CaseSensitive).Count -eq 0) -Message "Isolated lanes must preserve the bin/<Configuration>/<TargetFramework> AppContext suffix used by helper-host tests."

    try {
        Get-VerificationIsolatedOutputPath -IsolationRoot $scenarioRoot -Configuration "Release/escape" -TargetFramework "net10.0" | Out-Null
        throw "Expected unsafe topology segment rejection."
    }
    catch {
        Assert-Contains -Actual $_.Exception.Message -Expected "not a single safe path segment" -Message "Isolated topology segments must fail closed instead of escaping their lane."
    }
}
finally {
    if (Test-Path -LiteralPath $scenarioRoot) {
        Remove-Item -LiteralPath $scenarioRoot -Recurse -Force
    }
}

Write-Output "Parallel verifier contract tests passed ($assertionCount assertions)."
