param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot,

    [Parameter(Mandatory = $true)]
    [string]$VerificationResultsPath,

    [Parameter(Mandatory = $true)]
    [string]$VerificationPhysicalTempRoot,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{32}$')]
    [string]$FixtureRunIdentity,

    [Parameter(Mandatory = $true)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration,

    [Parameter(Mandatory = $true)]
    [ValidateSet("Standard", "UnfilteredEvidence", "FilteredEvidence")]
    [string]$CoverageOwnershipMode,

    [switch]$SkipCoverage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$resultsRoot = [IO.Path]::GetFullPath($VerificationResultsPath)
$testsPath = Join-Path $repositoryRoot "tests"
$scriptsPath = Join-Path $repositoryRoot "scripts"
$pullRequestRunSettingsPath = Join-Path $testsPath "verification-pull-request.runsettings"
$coverageOwnershipManifestPath = Join-Path $testsPath "verification-coverage-ownership.json"
$canonicalInventoryRoot = Join-Path $resultsRoot "Inventory\Canonical"
$verificationInventoryPath = Join-Path $resultsRoot "required-execution-tests.json"
$verificationPartitionReportPath = Join-Path $resultsRoot "required-test-partition.json"
$coverageIsolationRoot = Join-Path $resultsRoot "CoverageIsolation"
$standardTestResultsRoot = Join-Path $resultsRoot "StandardTests"
$laneDefinitionPath = Join-Path $resultsRoot "required-test-lanes.json"
$planPath = Join-Path $resultsRoot "required-test-preparation.json"
$powerShellExecutable = (Get-Process -Id $PID).Path
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)

. (Join-Path $scriptsPath "verification-artifacts.ps1")
. (Join-Path $scriptsPath "verification-coverage-evidence.ps1")
. (Join-Path $scriptsPath "verification-temp.ps1")
. (Join-Path $scriptsPath "verification-test-lanes.ps1")
. (Join-Path $scriptsPath "verification-test-plan.ps1")

$testProjects = @(Get-VerificationCanonicalTestProjects -RepositoryRoot $repositoryRoot)
$coverageOwnership = Read-VerificationCoverageOwnership -ManifestPath $coverageOwnershipManifestPath -RepositoryRoot $repositoryRoot -TestProjects $testProjects
$isolations = [Collections.Generic.List[object]]::new()
foreach ($testProject in $testProjects) {
    $isolation = New-VerificationProjectCoverageIsolation -TestProject $testProject -Lanes @(Get-VerificationTestProjectLanes -TestProject $testProject) -CoverageOwnership $coverageOwnership -Configuration $Configuration -CoverageOwnershipMode $CoverageOwnershipMode -PullRequestRunSettingsPath $pullRequestRunSettingsPath -CoverageIsolationRoot $coverageIsolationRoot -StandardTestResultsRoot $standardTestResultsRoot -VerificationPhysicalTempRoot $VerificationPhysicalTempRoot -VerificationFixtureRunIdentity $FixtureRunIdentity -SkipCoverage ([bool]$SkipCoverage)
    Write-Output "VERIFY_COVERAGE_SELECTION project=$($testProject.BaseName) selected_files=$($isolation.CoverageSelection.SelectedFiles.Count) excluded_files=$($isolation.CoverageSelection.ExcludedFiles.Count) primary_roots=$($isolation.CoverageSelection.PrimaryRoots.Count)"
    $isolations.Add($isolation)
}

foreach ($isolation in $isolations) {
    $inventoryPath = Join-Path $canonicalInventoryRoot "$($isolation.Project.BaseName).json"
    $diagnosticPath = [IO.Path]::ChangeExtension($inventoryPath, ".diag.log")
    $arguments = @("-NoProfile")
    if ($runningOnWindows) { $arguments += @("-ExecutionPolicy", "Bypass") }
    $arguments += @("-File", (Join-Path $scriptsPath "write-test-inventory.ps1"), "-TestAssemblyPath", $isolation.CanonicalAssemblyPath, "-Filter", (Get-VerificationTestProjectFilter -TestProject $isolation.Project), "-OutputPath", $inventoryPath, "-DiagnosticPath", $diagnosticPath, "-WorkingDirectory", $repositoryRoot)
    Write-Output "VERIFY_TEST_PLAN_DISCOVERY_START project=$($isolation.Project.BaseName)"
    & $powerShellExecutable @arguments
    if ($LASTEXITCODE -ne 0) { throw "Canonical test discovery failed for '$($isolation.Project.BaseName)' with exit code $LASTEXITCODE." }
    Write-Output "VERIFY_TEST_PLAN_DISCOVERY_COMPLETE project=$($isolation.Project.BaseName)"
}

$laneDefinitions = @($isolations | ForEach-Object {
    $projectName = $_.Project.BaseName
    foreach ($lane in $_.Lanes) {
        [ordered]@{
            name = $lane.Name
            projectName = $projectName
            filter = $lane.Filter
        }
    }
})
[IO.File]::WriteAllText($laneDefinitionPath, ([ordered]@{ schemaVersion = 1; lanes = $laneDefinitions } | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

$partitionArguments = @("-NoProfile")
if ($runningOnWindows) { $partitionArguments += @("-ExecutionPolicy", "Bypass") }
$partitionArguments += @("-File", (Join-Path $scriptsPath "verify-test-partition.ps1"), "-CanonicalInventoryRoot", $canonicalInventoryRoot, "-LaneDefinitionPath", $laneDefinitionPath, "-ExpectedExecutionInventoryPath", $verificationInventoryPath, "-ReportPath", $verificationPartitionReportPath)
& $powerShellExecutable @partitionArguments
if ($LASTEXITCODE -ne 0) { throw "Required test partition reconciliation failed with exit code $LASTEXITCODE." }

Write-VerificationTestPreparationPlan -PlanPath $planPath -RepositoryRoot $repositoryRoot -VerificationResultsPath $resultsRoot -Configuration $Configuration -SkipCoverage ([bool]$SkipCoverage) -CoverageOwnershipMode $CoverageOwnershipMode -FixtureRunIdentity $FixtureRunIdentity -CoverageOwnership $coverageOwnership -Isolations @($isolations)
Write-Output "VERIFY_TEST_PLAN_COMPLETE schema_version=1 projects=$($isolations.Count) lanes=$($laneDefinitions.Count) path=$planPath"
