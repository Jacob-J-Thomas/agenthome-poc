param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$BaseCommit,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$HeadCommit,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateRange(1, 4)]
    [int]$MaximumWorkers = [Math]::Min(4, [Math]::Max(1, [Environment]::ProcessorCount))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$qualificationStopwatch = [Diagnostics.Stopwatch]::StartNew()
$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsRoot = Join-Path $repoRoot "tests\QualificationResults"
$logsRoot = Join-Path $resultsRoot "Logs"
$testResultsRoot = Join-Path $resultsRoot "Tests"
$planPath = Join-Path $resultsRoot "qualification-plan.json"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$qualificationProcessHeavyWeight = 3
$qualificationCpuBoundWeight = 3

. (Join-Path $PSScriptRoot "qualification-plan.ps1")
. (Join-Path $PSScriptRoot "qualification-schedule.ps1")
. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-parallel.ps1")
. (Join-Path $PSScriptRoot "verification-temp.ps1")
$qualificationHardwareProcessorCount = [Environment]::ProcessorCount
$qualificationWorkerCount = Get-QualificationWorkerCount -MaximumWorkers $MaximumWorkers -HardwareProcessorCount $qualificationHardwareProcessorCount
$qualificationResourceCapacity = Get-QualificationResourceCapacity -WorkerCount $qualificationWorkerCount
$qualificationRunnerTemp = if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { $env:RUNNER_TEMP } elseif ($runningOnWindows) { [IO.Path]::GetTempPath() } else { "/tmp" }
$qualificationPhysicalTempRoot = Resolve-VerificationPhysicalTempRoot -RunnerTemp $qualificationRunnerTemp -SystemTempPath ([IO.Path]::GetTempPath())
$qualificationFixtureRunIdentity = [Guid]::NewGuid().ToString("N")
$qualificationFixtureRoots = [Collections.Generic.List[string]]::new()

function Assert-QualificationCommit {
    param([Parameter(Mandatory = $true)] [string]$Commit)

    & git cat-file -e "$Commit`^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Qualification commit is unavailable in the local object database: $Commit"
    }
}

function Get-QualificationBlobContent {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string[]]$Commits
    )

    foreach ($commit in $Commits) {
        $objectName = "$commit`:$Path"
        & git cat-file -e $objectName 2>$null
        if ($LASTEXITCODE -ne 0) {
            continue
        }

        $contentLines = @(& git cat-file blob $objectName 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw "Qualification could not read changed test source '$Path' from $commit."
        }

        return $contentLines -join "`n"
    }

    throw "Qualification could not find changed test source '$Path' in either side of the exact comparison edge."
}

function Test-QualificationCommitPath {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Commit
    )

    & git cat-file -e "$Commit`:$Path" 2>$null
    return $LASTEXITCODE -eq 0
}

function Add-QualificationPhase {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$FileName,
        [Parameter(Mandatory = $true)] [string[]]$Arguments,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)] [int]$EstimatedDurationSeconds,
        [Parameter(Mandatory = $true)] [int]$Weight,
        [Parameter(Mandatory = $true)] [ValidateSet("Ordinary", "CpuBound", "ProcessHeavy", "ProcessLight")] [string]$ResourceClass,
        [hashtable]$Environment,
        [string]$TrxPath
    )

    Add-VerificationParallelPhase -Name $Name -FileName $FileName -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -WorkingDirectory $repoRoot -OutputPath (Join-Path $logsRoot "$Name.log") -Environment $Environment -TrxPath $TrxPath -EstimatedDurationSeconds $EstimatedDurationSeconds -Weight $Weight -ResourceClass $ResourceClass
}

function Invoke-QualificationWave {
    if ($script:VerificationParallelPhases.Count -eq 0) {
        return
    }

    Invoke-VerificationParallelPhases -MaximumWorkers $qualificationWorkerCount -MaximumResourceCapacity $qualificationResourceCapacity -MaximumProcessHeavyWorkers ([Math]::Min(2, $qualificationWorkerCount)) -MaximumCpuBoundWorkers ([Math]::Min(1, $qualificationWorkerCount)) | Out-Null
    Reset-VerificationParallelPhaseState
}

Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot "verify-sdk.ps1") -GlobalJsonPath (Join-Path $repoRoot "global.json") -RepositoryRoot $repoRoot
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    Assert-QualificationCommit -Commit $BaseCommit
    Assert-QualificationCommit -Commit $HeadCommit
    $mergeBase = (& git merge-base $BaseCommit $HeadCommit).Trim()
    if ($LASTEXITCODE -ne 0 -or $mergeBase -notmatch '^[0-9a-f]{40}$') {
        throw "Qualification could not resolve one exact merge base for $BaseCommit and $HeadCommit."
    }

    $changedPaths = @(& git diff --no-renames --name-only --diff-filter=ACMRDTUXB "$mergeBase..$HeadCommit")
    if ($LASTEXITCODE -ne 0) {
        throw "Qualification could not enumerate the exact changed paths."
    }
    $testNamespacesByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $testClassesByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $focusedHelperRelevantPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $focusedImplementationFallbackPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $validatedFocusedImplementationPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($changedPath in $changedPaths) {
        $normalizedPath = ConvertTo-QualificationPath -Path $changedPath
        $focusedImplementationMappingsForChangedPath = [Collections.Generic.List[object]]::new()
        $focusedImplementationMappingPathsForChangedPath = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($focusedImplementationMapping in @(Get-QualificationFocusedImplementationMappingsForPath -Path $normalizedPath)) {
            if ($focusedImplementationMappingPathsForChangedPath.Add($focusedImplementationMapping.Path)) {
                $focusedImplementationMappingsForChangedPath.Add($focusedImplementationMapping)
            }
        }
        if ($normalizedPath.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase)) {
            foreach ($focusedImplementationMapping in @($script:QualificationFocusedImplementationMappings | Where-Object { $_.Kind -ceq "PublicConstantContract" })) {
                if ($focusedImplementationMappingPathsForChangedPath.Contains($focusedImplementationMapping.Path)) {
                    continue
                }

                $containsContractReference = $false
                foreach ($commit in @($HeadCommit, $mergeBase)) {
                    if (-not (Test-QualificationCommitPath -Path $normalizedPath -Commit $commit)) {
                        continue
                    }

                    $edgeContent = Get-QualificationBlobContent -Path $normalizedPath -Commits @($commit)
                    if ((Test-QualificationContainsIdentifierReference -Content $edgeContent -Identifier $focusedImplementationMapping.TypeName) -or (Test-QualificationContainsIdentifierReference -Content $edgeContent -Identifier $focusedImplementationMapping.MemberName)) {
                        $containsContractReference = $true
                        break
                    }
                }
                if ($containsContractReference) {
                    [void]$focusedImplementationMappingPathsForChangedPath.Add($focusedImplementationMapping.Path)
                    $focusedImplementationMappingsForChangedPath.Add($focusedImplementationMapping)
                    [void]$focusedHelperRelevantPaths.Add($normalizedPath)
                }
            }
        }

        foreach ($focusedImplementationMapping in $focusedImplementationMappingsForChangedPath) {
            if (-not $validatedFocusedImplementationPaths.Add($focusedImplementationMapping.Path)) {
                continue
            }

            $mappedImplementationPath = ConvertTo-QualificationPath -Path $focusedImplementationMapping.Path
            $focusedImplementationEligible = $true
            switch ($focusedImplementationMapping.Kind) {
                "InternalSealed" {
                    $existingEdgeCount = 0
                    foreach ($commit in @($HeadCommit, $mergeBase)) {
                        if (-not (Test-QualificationCommitPath -Path $mappedImplementationPath -Commit $commit)) {
                            continue
                        }

                        $existingEdgeCount++
                        $implementationContent = Get-QualificationBlobContent -Path $mappedImplementationPath -Commits @($commit)
                        if (-not (Test-QualificationFocusedImplementationSource -Content $implementationContent)) {
                            throw "Focused implementation mapping '$mappedImplementationPath' is only valid for one syntax-valid top-level internal sealed non-partial type on every existing side of the exact edge."
                        }
                    }
                    if ($existingEdgeCount -eq 0) {
                        throw "Focused implementation mapping '$mappedImplementationPath' has no source on either side of the exact edge."
                    }
                }
                "PrivateMethod" {
                    if (-not (Test-QualificationCommitPath -Path $mappedImplementationPath -Commit $HeadCommit) -or -not (Test-QualificationCommitPath -Path $mappedImplementationPath -Commit $mergeBase)) {
                        $focusedImplementationEligible = $false
                    }
                    else {
                        $baseImplementationContent = Get-QualificationBlobContent -Path $mappedImplementationPath -Commits @($mergeBase)
                        $headImplementationContent = Get-QualificationBlobContent -Path $mappedImplementationPath -Commits @($HeadCommit)
                        if (-not (Test-QualificationFocusedPrivateMethodEdge -BaseContent $baseImplementationContent -HeadContent $headImplementationContent -TypeName $focusedImplementationMapping.TypeName -MemberName $focusedImplementationMapping.MemberName)) {
                            $focusedImplementationEligible = $false
                        }
                    }
                }
                "PublicConstantContract" {
                    if (-not (Test-QualificationCommitPath -Path $mappedImplementationPath -Commit $HeadCommit)) {
                        throw "Focused public-constant mapping '$mappedImplementationPath' requires the contract on the exact head."
                    }

                    foreach ($commit in @($HeadCommit, $mergeBase)) {
                        if (-not (Test-QualificationCommitPath -Path $mappedImplementationPath -Commit $commit)) {
                            continue
                        }

                        $implementationContent = Get-QualificationBlobContent -Path $mappedImplementationPath -Commits @($commit)
                        if (-not (Test-QualificationPublicConstantContractSource -Content $implementationContent -TypeName $focusedImplementationMapping.TypeName -MemberName $focusedImplementationMapping.MemberName)) {
                            throw "Focused public-constant mapping '$mappedImplementationPath' requires one public static class containing only bounded integer constant '$($focusedImplementationMapping.MemberName)'."
                        }
                    }

                    $actualReferencePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    foreach ($identifier in @($focusedImplementationMapping.TypeName, $focusedImplementationMapping.MemberName)) {
                        foreach ($referencePath in @(Get-QualificationExactIdentifierReferencePaths -RepositoryRoot $repoRoot -Commit $HeadCommit -Identifier $identifier)) {
                            [void]$actualReferencePaths.Add($referencePath)
                        }
                    }
                    $expectedReferencePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    foreach ($referencePath in @($focusedImplementationMapping.ReferencePaths)) {
                        $normalizedReferencePath = ConvertTo-QualificationPath -Path $referencePath
                        if (-not $expectedReferencePaths.Add($normalizedReferencePath)) {
                            throw "Focused public-constant mapping '$mappedImplementationPath' repeats reference path '$normalizedReferencePath'."
                        }
                    }
                    if ((@($actualReferencePaths | Sort-Object) -join "|") -cne (@($expectedReferencePaths | Sort-Object) -join "|")) {
                        throw "Focused public-constant mapping '$mappedImplementationPath' has reference drift. Expected $(@($expectedReferencePaths | Sort-Object) -join ', '); found $(@($actualReferencePaths | Sort-Object) -join ', ')."
                    }
                }
                default {
                    throw "Focused implementation mapping '$mappedImplementationPath' has unsupported kind '$($focusedImplementationMapping.Kind)'."
                }
            }

            if (-not $focusedImplementationEligible) {
                [void]$focusedImplementationFallbackPaths.Add($mappedImplementationPath)
                Write-Output "QUALIFICATION_FOCUSED_FALLBACK path=$mappedImplementationPath reason=exact-edge-not-eligible"
                continue
            }

            foreach ($testMapping in @($focusedImplementationMapping.Tests)) {
                $mappedTestPath = ConvertTo-QualificationPath -Path $testMapping.Path
                if (-not (Test-QualificationCommitPath -Path $mappedTestPath -Commit $HeadCommit)) {
                    throw "Focused implementation mapping '$mappedImplementationPath' names missing exact-head test source '$mappedTestPath'."
                }

                $mappedTestContent = Get-QualificationBlobContent -Path $mappedTestPath -Commits @($HeadCommit)
                $mappedTestClasses = @(Get-QualificationDirectXunitTestClasses -Path $mappedTestPath -Content $mappedTestContent)
                if ($mappedTestClasses.Count -ne 1 -or $mappedTestClasses[0] -cne $testMapping.Class) {
                    throw "Focused implementation mapping '$mappedImplementationPath' expected exact-head test class '$($testMapping.Class)' in '$mappedTestPath' but found '$($mappedTestClasses -join ', ')'."
                }

                $mappedExternalConsumerPaths = @(Get-QualificationExternalTestClassConsumerPaths -RepositoryRoot $repoRoot -Commit $HeadCommit -Path $mappedTestPath -TestClass $mappedTestClasses[0])
                if ($mappedExternalConsumerPaths.Count -gt 0) {
                    throw "Focused implementation mapping '$mappedImplementationPath' cannot select test class '$($mappedTestClasses[0])' because exact-head test sources consume it: $($mappedExternalConsumerPaths -join ', ')."
                }
            }
        }

        $testProject = Get-QualificationTestProject -Path $normalizedPath
        if ($null -ne $testProject) {
            if ($normalizedPath.EndsWith(".csproj", [StringComparison]::OrdinalIgnoreCase) -or $null -ne (Get-QualificationFocusedHelperMapping -Path $normalizedPath)) {
                [void]$focusedHelperRelevantPaths.Add($normalizedPath)
            }
            if ($normalizedPath.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase)) {
                foreach ($commit in @($HeadCommit, $mergeBase)) {
                    if (-not (Test-QualificationCommitPath -Path $normalizedPath -Commit $commit)) {
                        continue
                    }
                    $edgeContent = Get-QualificationBlobContent -Path $normalizedPath -Commits @($commit)
                    if (Test-QualificationContainsFocusedHelperReference -Content $edgeContent) {
                        [void]$focusedHelperRelevantPaths.Add($normalizedPath)
                        break
                    }
                }
            }
        }
        if (Test-QualificationFilterableTestSource -Path $normalizedPath) {
            if (-not (Test-QualificationCommitPath -Path $normalizedPath -Commit $HeadCommit)) {
                $testNamespacesByPath[$normalizedPath] = [string[]]::new(0)
                continue
            }

            $content = Get-QualificationBlobContent -Path $normalizedPath -Commits @($HeadCommit)
            $declaredNamespace = Get-QualificationDeclaredTestNamespace -Path $normalizedPath -Content $content
            if (Test-QualificationContainsDirectXunitTest -Content $content) {
                $directTestClasses = @(Get-QualificationDirectXunitTestClasses -Path $normalizedPath -Content $content)
                $externalConsumerPaths = @(Get-QualificationExternalTestClassConsumerPaths -RepositoryRoot $repoRoot -Commit $HeadCommit -Path $normalizedPath -TestClass $directTestClasses[0])
                if ($externalConsumerPaths.Count -gt 0) {
                    $testNamespacesByPath[$normalizedPath] = [string[]]::new(0)
                }
                else {
                    $testClassesByPath[$normalizedPath] = $directTestClasses
                }
            }
            else {
                $focusedHelperMapping = Get-QualificationFocusedHelperMapping -Path $normalizedPath
                if ($null -eq $focusedHelperMapping) {
                    $testNamespacesByPath[$normalizedPath] = [string[]]::new(0)
                }
                elseif (@($focusedHelperMapping.ConsumerClasses).Count -gt 0) {
                    $testClassesByPath[$normalizedPath] = [string[]]@($focusedHelperMapping.ConsumerClasses)
                }
                else {
                    $testNamespacesByPath[$normalizedPath] = [string[]]@($focusedHelperMapping.ConsumerNamespaces)
                }
            }
        }
    }
    $availableTestProjects = @($script:QualificationTestProjects | Where-Object { Test-QualificationCommitPath -Path $_ -Commit $HeadCommit })
    $plan = Get-QualificationPlan -ChangedPaths $changedPaths -TestNamespacesByPath $testNamespacesByPath -TestClassesByPath $testClassesByPath -FocusedHelperRelevantPaths @($focusedHelperRelevantPaths) -FocusedImplementationFallbackPaths @($focusedImplementationFallbackPaths) -AvailableTestProjects $availableTestProjects

    if (Test-Path -LiteralPath $resultsRoot) {
        Remove-Item -LiteralPath $resultsRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $logsRoot, $testResultsRoot -Force | Out-Null
    $planEvidence = [ordered]@{
        schemaVersion = 1
        baseCommit = $BaseCommit.ToLowerInvariant()
        headCommit = $HeadCommit.ToLowerInvariant()
        mergeBase = $mergeBase
        configuration = $Configuration
        changedPaths = $plan.ChangedPaths
        focusedImplementationFallbackPaths = $plan.FocusedImplementationFallbackPaths
        requiresBuild = $plan.RequiresBuild
        requiresFrontend = $plan.RequiresFrontend
        requiresWorkflowValidation = $plan.RequiresWorkflowValidation
        requiresVerifierContracts = $plan.RequiresVerifierContracts
        requiresCSharpFormat = $plan.RequiresCSharpFormat
        requiresArchitecture = $plan.RequiresArchitecture
        requiresDrawioValidation = $plan.RequiresDrawioValidation
        testProjects = $plan.TestProjects
        testSelections = @($plan.TestSelections | ForEach-Object {
            [ordered]@{
                project = $_.Project
                namespaces = @($_.Namespaces)
                classes = @($_.Classes)
            }
        })
    }
    [IO.File]::WriteAllText($planPath, ($planEvidence | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output "QUALIFICATION_PLAN_JSON=$($planEvidence | ConvertTo-Json -Compress -Depth 5)"

    Reset-VerificationParallelPhaseState
    if ($plan.RequiresBuild) {
        Add-QualificationPhase -Name "build-release" -FileName "dotnet" -Arguments @("build", "EmbodySense.sln", "--configuration", $Configuration, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 240 -EstimatedDurationSeconds 150 -Weight $qualificationProcessHeavyWeight -ResourceClass "ProcessHeavy"
    }
    if ($plan.RequiresFrontend) {
        $frontendArguments = @("-NoProfile")
        if ($runningOnWindows) { $frontendArguments += @("-ExecutionPolicy", "Bypass") }
        $frontendArguments += @("-File", (Join-Path $PSScriptRoot "verify-frontend.ps1"), "-RepositoryRoot", $repoRoot, "-LogsPath", $logsRoot)
        Add-QualificationPhase -Name "frontend" -FileName $powerShellExecutable -Arguments $frontendArguments -TimeoutSeconds 240 -EstimatedDurationSeconds 60 -Weight $qualificationCpuBoundWeight -ResourceClass "CpuBound"
    }

    Invoke-QualificationWave

    if ($plan.RequiresDrawioValidation) {
        foreach ($drawioPath in @($plan.ChangedPaths | Where-Object { $_.EndsWith(".drawio", [StringComparison]::OrdinalIgnoreCase) })) {
            if (-not (Test-QualificationCommitPath -Path $drawioPath -Commit $HeadCommit)) {
                continue
            }
            try {
                [void][xml](Get-QualificationBlobContent -Path $drawioPath -Commits @($HeadCommit))
            }
            catch {
                throw "Qualification rejected malformed draw.io XML '$drawioPath': $($_.Exception.Message)"
            }
        }
    }
    $exclusiveQualificationTests = [Collections.Generic.List[object]]::new()
    $sharedQualificationTests = [Collections.Generic.List[object]]::new()
    foreach ($testSelection in $plan.TestSelections) {
        $testProject = $testSelection.Project
        $projectName = [IO.Path]::GetFileNameWithoutExtension($testProject)
        $projectResultsRoot = Join-Path $testResultsRoot $projectName
        $trxPath = Join-Path $projectResultsRoot "$projectName.trx"
        $projectFixtureRoot = Get-VerificationLaneFixturePath -PhysicalTempRoot $qualificationPhysicalTempRoot -RunIdentity $qualificationFixtureRunIdentity -LaneIdentity $projectName
        if (Test-Path -LiteralPath $projectFixtureRoot) {
            throw "Qualification lane temporary path collision for '$projectName': $projectFixtureRoot"
        }
        New-Item -ItemType Directory -Path $projectFixtureRoot | Out-Null
        $qualificationFixtureRoots.Add($projectFixtureRoot)
        $testFilter = Get-QualificationTestFilter -ProjectName $projectName -Namespaces @($testSelection.Namespaces) -Classes @($testSelection.Classes)
        $testEnvironment = @{
            EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $projectFixtureRoot "catalog-trust"
            TEMP = $projectFixtureRoot
            TMP = $projectFixtureRoot
            TMPDIR = $projectFixtureRoot
        }
        $testScheduleProfile = Get-QualificationTestScheduleProfile -ProjectName $projectName -ResourceCapacity $qualificationResourceCapacity
        if ($testScheduleProfile.Isolation -ceq "Exclusive") {
            $exclusiveQualificationTests.Add([pscustomobject]@{ Name = "tests-$projectName"; FileName = "dotnet"; Arguments = @("test", $testProject, "--configuration", $Configuration, "--no-build", "--no-restore", "--settings", "tests/verification-stress.runsettings", "--filter", $testFilter, "--logger", "trx;LogFileName=$projectName.trx", "--results-directory", $projectResultsRoot, "/p:RestoreIgnoreFailedSources=true"); Profile = $testScheduleProfile; Environment = $testEnvironment; TrxPath = $trxPath })
            continue
        }
        $sharedQualificationTests.Add([pscustomobject]@{ Name = "tests-$projectName"; FileName = "dotnet"; Arguments = @("test", $testProject, "--configuration", $Configuration, "--no-build", "--no-restore", "--settings", "tests/verification-stress.runsettings", "--filter", $testFilter, "--logger", "trx;LogFileName=$projectName.trx", "--results-directory", $projectResultsRoot, "/p:RestoreIgnoreFailedSources=true"); Profile = $testScheduleProfile; Environment = $testEnvironment; TrxPath = $trxPath })
    }
    foreach ($exclusiveTest in @($exclusiveQualificationTests | Sort-Object @{ Expression = { $_.Profile.ExclusiveOrder }; Ascending = $true }, @{ Expression = { $_.Profile.EstimatedDurationSeconds }; Descending = $true })) {
        Add-QualificationPhase -Name $exclusiveTest.Name -FileName $exclusiveTest.FileName -Arguments $exclusiveTest.Arguments -TimeoutSeconds $exclusiveTest.Profile.TimeoutSeconds -EstimatedDurationSeconds $exclusiveTest.Profile.EstimatedDurationSeconds -Weight $exclusiveTest.Profile.Weight -ResourceClass $exclusiveTest.Profile.ResourceClass -Environment $exclusiveTest.Environment -TrxPath $exclusiveTest.TrxPath
        Invoke-QualificationWave
    }
    foreach ($sharedTest in $sharedQualificationTests) {
        Add-QualificationPhase -Name $sharedTest.Name -FileName $sharedTest.FileName -Arguments $sharedTest.Arguments -TimeoutSeconds $sharedTest.Profile.TimeoutSeconds -EstimatedDurationSeconds $sharedTest.Profile.EstimatedDurationSeconds -Weight $sharedTest.Profile.Weight -ResourceClass $sharedTest.Profile.ResourceClass -Environment $sharedTest.Environment -TrxPath $sharedTest.TrxPath
    }

    $exclusiveQualificationContracts = [Collections.Generic.List[object]]::new()
    if ($plan.RequiresVerifierContracts) {
        $contractScripts = @(
            "verify-bounded-phases.tests.ps1",
            "verify-coverage.tests.ps1",
            "verify-parallel.tests.ps1",
            "verify-preflight-overlap.tests.ps1",
            "verify-test-inventory.tests.ps1",
            "verify-watchdog.tests.ps1",
            "verify-promotion-fan-in.tests.ps1"
        )
        if ($runningOnWindows) {
            $contractScripts += "verify-sdk-diagnostics.tests.ps1"
        }
        foreach ($contractScript in $contractScripts) {
            $contractArguments = @("-NoProfile")
            if ($runningOnWindows) { $contractArguments += @("-ExecutionPolicy", "Bypass") }
            $contractArguments += @("-File", (Join-Path $repoRoot "tests\scripts\$contractScript"))
            $contractScheduleProfile = Get-QualificationContractScheduleProfile -ScriptName $contractScript
            if ($contractScheduleProfile.Isolation -ceq "Exclusive") {
                $exclusiveQualificationContracts.Add([pscustomobject]@{ ScriptName = $contractScript; Arguments = $contractArguments; Profile = $contractScheduleProfile })
                continue
            }
            Add-QualificationPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds $contractScheduleProfile.TimeoutSeconds -EstimatedDurationSeconds $contractScheduleProfile.EstimatedDurationSeconds -Weight $contractScheduleProfile.Weight -ResourceClass $contractScheduleProfile.ResourceClass
        }
    }
    if ($plan.RequiresWorkflowValidation) {
        Add-QualificationPhase -Name "github-yaml-format" -FileName "npx" -Arguments @("prettier", "--check", "--end-of-line", "auto", ".github/workflows/*.{yml,yaml}", ".github/dependabot.yml") -TimeoutSeconds 60 -EstimatedDurationSeconds 10 -Weight 1 -ResourceClass "Ordinary"
    }

    $integrationSelection = @($plan.TestSelections | Where-Object { $_.Project -ceq "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj" })
    $integrationRunsUnfiltered = $integrationSelection.Count -eq 1 -and @($integrationSelection[0].Namespaces).Count -eq 0 -and @($integrationSelection[0].Classes).Count -eq 0
    $integrationProjectAvailable = $availableTestProjects -ccontains "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
    if ($plan.RequiresArchitecture -and $integrationProjectAvailable -and -not $integrationRunsUnfiltered) {
        $architectureResultsRoot = Join-Path $testResultsRoot "Architecture"
        Add-QualificationPhase -Name "tests-architecture" -FileName "dotnet" -Arguments @("test", "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj", "--configuration", $Configuration, "--no-build", "--no-restore", "--settings", "tests/verification-stress.runsettings", "--filter", "FullyQualifiedName~Architecture&VerificationTier!=Stress", "--logger", "trx;LogFileName=Architecture.trx", "--results-directory", $architectureResultsRoot, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 120 -EstimatedDurationSeconds 20 -Weight $qualificationProcessHeavyWeight -ResourceClass "ProcessHeavy" -TrxPath (Join-Path $architectureResultsRoot "Architecture.trx")
    }
    if ($plan.RequiresCSharpFormat) {
        $existingCSharpFiles = @($plan.ChangedCSharpFiles | Where-Object { Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf })
        if ($existingCSharpFiles.Count -gt 0) {
            $formatArguments = @("format", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal", "--include") + $existingCSharpFiles
            Add-QualificationPhase -Name "format-changed" -FileName "dotnet" -Arguments $formatArguments -TimeoutSeconds 150 -EstimatedDurationSeconds 120 -Weight $qualificationCpuBoundWeight -ResourceClass "CpuBound"
        }
    }

    Add-QualificationPhase -Name "git-diff-check" -FileName "git" -Arguments @("diff", "--check", "$mergeBase..$HeadCommit") -TimeoutSeconds 30 -EstimatedDurationSeconds 5 -Weight 1 -ResourceClass "Ordinary"
    Invoke-QualificationWave
    foreach ($exclusiveContract in $exclusiveQualificationContracts) {
        $profile = $exclusiveContract.Profile
        Add-QualificationPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($exclusiveContract.ScriptName))" -FileName $powerShellExecutable -Arguments $exclusiveContract.Arguments -TimeoutSeconds $profile.TimeoutSeconds -EstimatedDurationSeconds $profile.EstimatedDurationSeconds -Weight $profile.Weight -ResourceClass $profile.ResourceClass
        Invoke-QualificationWave
    }
}
finally {
    Pop-Location
    foreach ($qualificationFixtureRoot in $qualificationFixtureRoots) {
        if (Test-Path -LiteralPath $qualificationFixtureRoot) {
            Remove-Item -LiteralPath $qualificationFixtureRoot -Recurse -Force
        }
    }
}

$qualificationStopwatch.Stop()
$elapsedText = $qualificationStopwatch.Elapsed.TotalSeconds.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
Write-Output "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=$elapsedText"
