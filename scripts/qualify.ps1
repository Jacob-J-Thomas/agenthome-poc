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
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-qualification-" + [Guid]::NewGuid().ToString("N"))
$qualificationResourceCapacity = [Math]::Min(4, [Math]::Max(1, [Environment]::ProcessorCount))
$qualificationProcessHeavyWeight = [Math]::Min(2, $qualificationResourceCapacity)
$qualificationCpuBoundWeight = [Math]::Min(2, $qualificationResourceCapacity)

. (Join-Path $PSScriptRoot "qualification-plan.ps1")
. (Join-Path $PSScriptRoot "verification-phase.ps1")
. (Join-Path $PSScriptRoot "verification-parallel.ps1")

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

function Get-QualificationTestFilter {
    param(
        [Parameter(Mandatory = $true)] [string]$ProjectName,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Namespaces
    )

    $clauses = [Collections.Generic.List[string]]::new()
    if ($Namespaces.Count -gt 0) {
        $namespaceClauses = @($Namespaces | ForEach-Object { "FullyQualifiedName~$_" })
        $clauses.Add("($($namespaceClauses -join '|'))")
    }
    if ($ProjectName -ceq "EmbodySense.E2ETests") {
        $clauses.Add("(FullyQualifiedName!~BrowserFlowTests)")
    }
    $clauses.Add("(VerificationTier!=Stress)")

    return $clauses -join "&"
}

function Add-QualificationPhase {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [string]$FileName,
        [Parameter(Mandatory = $true)] [string[]]$Arguments,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)] [int]$EstimatedDurationSeconds,
        [Parameter(Mandatory = $true)] [int]$Weight,
        [Parameter(Mandatory = $true)] [ValidateSet("Ordinary", "CpuBound", "ProcessHeavy")] [string]$ResourceClass,
        [hashtable]$Environment,
        [string]$TrxPath
    )

    Add-VerificationParallelPhase -Name $Name -FileName $FileName -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -WorkingDirectory $repoRoot -OutputPath (Join-Path $logsRoot "$Name.log") -Environment $Environment -TrxPath $TrxPath -EstimatedDurationSeconds $EstimatedDurationSeconds -Weight $Weight -ResourceClass $ResourceClass
}

function Invoke-QualificationWave {
    if ($script:VerificationParallelPhases.Count -eq 0) {
        return
    }

    $workerCount = [Math]::Min($MaximumWorkers, $qualificationResourceCapacity)
    Invoke-VerificationParallelPhases -MaximumWorkers $workerCount -MaximumResourceCapacity $qualificationResourceCapacity -MaximumProcessHeavyWorkers ([Math]::Min(2, $workerCount)) -MaximumCpuBoundWorkers ([Math]::Min(1, $workerCount)) | Out-Null
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
    $focusedHelperRelevantPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($changedPath in $changedPaths) {
        $normalizedPath = ConvertTo-QualificationPath -Path $changedPath
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
                $testNamespacesByPath[$normalizedPath] = @($declaredNamespace)
            }
            else {
                $focusedHelperMapping = Get-QualificationFocusedHelperMapping -Path $normalizedPath
                $testNamespacesByPath[$normalizedPath] = if ($null -eq $focusedHelperMapping) { [string[]]::new(0) } else { [string[]]@($focusedHelperMapping.ConsumerNamespaces) }
            }
        }
    }
    $availableTestProjects = @($script:QualificationTestProjects | Where-Object { Test-QualificationCommitPath -Path $_ -Commit $HeadCommit })
    $plan = Get-QualificationPlan -ChangedPaths $changedPaths -TestNamespacesByPath $testNamespacesByPath -FocusedHelperRelevantPaths @($focusedHelperRelevantPaths) -AvailableTestProjects $availableTestProjects

    if (Test-Path -LiteralPath $resultsRoot) {
        Remove-Item -LiteralPath $resultsRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $logsRoot, $testResultsRoot, $fixtureRoot -Force | Out-Null
    $planEvidence = [ordered]@{
        schemaVersion = 1
        baseCommit = $BaseCommit.ToLowerInvariant()
        headCommit = $HeadCommit.ToLowerInvariant()
        mergeBase = $mergeBase
        configuration = $Configuration
        changedPaths = $plan.ChangedPaths
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
            }
        })
    }
    [IO.File]::WriteAllText($planPath, ($planEvidence | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output "QUALIFICATION_PLAN_JSON=$($planEvidence | ConvertTo-Json -Compress -Depth 5)"

    Reset-VerificationParallelPhaseState
    if ($plan.RequiresBuild) {
        Add-QualificationPhase -Name "build-release" -FileName "dotnet" -Arguments @("build", "EmbodySense.sln", "--configuration", $Configuration, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 240 -EstimatedDurationSeconds 120 -Weight $qualificationProcessHeavyWeight -ResourceClass "ProcessHeavy"
    }
    if ($plan.RequiresFrontend) {
        $frontendArguments = @("-NoProfile")
        if ($runningOnWindows) { $frontendArguments += @("-ExecutionPolicy", "Bypass") }
        $frontendArguments += @("-File", (Join-Path $PSScriptRoot "verify-frontend.ps1"), "-RepositoryRoot", $repoRoot, "-LogsPath", $logsRoot)
        Add-QualificationPhase -Name "frontend" -FileName $powerShellExecutable -Arguments $frontendArguments -TimeoutSeconds 240 -EstimatedDurationSeconds 60 -Weight $qualificationCpuBoundWeight -ResourceClass "CpuBound"
    }
    Invoke-QualificationWave

    if ($plan.RequiresWorkflowValidation) {
        Add-QualificationPhase -Name "workflow-format" -FileName "npx" -Arguments @("prettier", "--check", "--end-of-line", "auto", ".github/workflows/*.yml") -TimeoutSeconds 60 -EstimatedDurationSeconds 10 -Weight 1 -ResourceClass "Ordinary"
        Invoke-QualificationWave
    }

    if ($plan.RequiresVerifierContracts) {
        $contractScripts = @(
            "verify-bounded-phases.tests.ps1",
            "verify-coverage.tests.ps1",
            "verify-parallel.tests.ps1",
            "verify-preflight-overlap.tests.ps1",
            "verify-test-inventory.tests.ps1",
            "verify-watchdog.tests.ps1"
        )
        if ($runningOnWindows) {
            $contractScripts += "verify-sdk-diagnostics.tests.ps1"
        }
        foreach ($contractScript in $contractScripts) {
            $contractArguments = @("-NoProfile")
            if ($runningOnWindows) { $contractArguments += @("-ExecutionPolicy", "Bypass") }
            $contractArguments += @("-File", (Join-Path $repoRoot "tests\scripts\$contractScript"))
            Add-QualificationPhase -Name "contract-$([IO.Path]::GetFileNameWithoutExtension($contractScript))" -FileName $powerShellExecutable -Arguments $contractArguments -TimeoutSeconds 90 -EstimatedDurationSeconds 30 -Weight $qualificationProcessHeavyWeight -ResourceClass "ProcessHeavy"
        }
        Invoke-QualificationWave
    }

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

    Reset-VerificationParallelPhaseState
    foreach ($testSelection in $plan.TestSelections) {
        $testProject = $testSelection.Project
        $projectName = [IO.Path]::GetFileNameWithoutExtension($testProject)
        $projectResultsRoot = Join-Path $testResultsRoot $projectName
        $trxPath = Join-Path $projectResultsRoot "$projectName.trx"
        $projectFixtureRoot = Join-Path $fixtureRoot $projectName
        New-Item -ItemType Directory -Path $projectFixtureRoot -Force | Out-Null
        $testFilter = Get-QualificationTestFilter -ProjectName $projectName -Namespaces @($testSelection.Namespaces)
        $testEnvironment = @{
            EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT = Join-Path $projectFixtureRoot "catalog-trust"
            TEMP = $projectFixtureRoot
            TMP = $projectFixtureRoot
            TMPDIR = $projectFixtureRoot
        }
        Add-QualificationPhase -Name "tests-$projectName" -FileName "dotnet" -Arguments @("test", $testProject, "--configuration", $Configuration, "--no-build", "--no-restore", "--settings", "tests/verification-stress.runsettings", "--filter", $testFilter, "--logger", "trx;LogFileName=$projectName.trx", "--results-directory", $projectResultsRoot, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 180 -EstimatedDurationSeconds 90 -Weight $qualificationProcessHeavyWeight -ResourceClass "ProcessHeavy" -Environment $testEnvironment -TrxPath $trxPath
    }
    $integrationSelection = @($plan.TestSelections | Where-Object { $_.Project -ceq "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj" })
    $integrationRunsUnfiltered = $integrationSelection.Count -eq 1 -and @($integrationSelection[0].Namespaces).Count -eq 0
    $integrationProjectAvailable = $availableTestProjects -ccontains "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj"
    if ($plan.RequiresArchitecture -and $integrationProjectAvailable -and -not $integrationRunsUnfiltered) {
        $architectureResultsRoot = Join-Path $testResultsRoot "Architecture"
        Add-QualificationPhase -Name "tests-architecture" -FileName "dotnet" -Arguments @("test", "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj", "--configuration", $Configuration, "--no-build", "--no-restore", "--settings", "tests/verification-stress.runsettings", "--filter", "FullyQualifiedName~Architecture&VerificationTier!=Stress", "--logger", "trx;LogFileName=Architecture.trx", "--results-directory", $architectureResultsRoot, "/p:RestoreIgnoreFailedSources=true") -TimeoutSeconds 120 -EstimatedDurationSeconds 20 -Weight $qualificationProcessHeavyWeight -ResourceClass "ProcessHeavy" -TrxPath (Join-Path $architectureResultsRoot "Architecture.trx")
    }
    Invoke-QualificationWave

    if ($plan.RequiresCSharpFormat) {
        $existingCSharpFiles = @($plan.ChangedCSharpFiles | Where-Object { Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf })
        if ($existingCSharpFiles.Count -gt 0) {
            $formatArguments = @("format", "whitespace", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--verbosity", "minimal", "--include") + $existingCSharpFiles
            Add-QualificationPhase -Name "format-changed-whitespace" -FileName "dotnet" -Arguments $formatArguments -TimeoutSeconds 90 -EstimatedDurationSeconds 30 -Weight $qualificationCpuBoundWeight -ResourceClass "CpuBound"
            Invoke-QualificationWave
            $styleArguments = @("format", "style", "EmbodySense.sln", "--verify-no-changes", "--no-restore", "--severity", "warn", "--diagnostics", "IDE1006", "--verbosity", "minimal", "--include") + $existingCSharpFiles
            Add-QualificationPhase -Name "format-changed-style" -FileName "dotnet" -Arguments $styleArguments -TimeoutSeconds 90 -EstimatedDurationSeconds 45 -Weight $qualificationCpuBoundWeight -ResourceClass "CpuBound"
            Invoke-QualificationWave
        }
    }

    Add-QualificationPhase -Name "git-diff-check" -FileName "git" -Arguments @("diff", "--check", "$mergeBase..$HeadCommit") -TimeoutSeconds 30 -EstimatedDurationSeconds 5 -Weight 1 -ResourceClass "Ordinary"
    Invoke-QualificationWave
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

$qualificationStopwatch.Stop()
$elapsedText = $qualificationStopwatch.Elapsed.TotalSeconds.ToString("0.###", [Globalization.CultureInfo]::InvariantCulture)
Write-Output "VERIFY_COMPLETE schema_version=1 status=passed elapsed_seconds=$elapsedText"
