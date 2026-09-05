param(
    [string]$SolutionArtifactRoot = "",

    [string]$StaticArtifactRoot = "",

    [string]$NestedArtifactRoot = "",

    [string]$ExpectedHead = "",

    [string]$ExpectedRunId = "",

    [string]$ExpectedRunAttempt = "",

    [ValidateSet("success", "failure", "cancelled", "skipped")]
    [string]$SolutionResult = "success",

    [ValidateSet("success", "failure", "cancelled", "skipped")]
    [string]$StaticResult = "success",

    [ValidateSet("success", "failure", "cancelled", "skipped")]
    [string]$NestedResult = "success",

    [switch]$NoRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-FanInCondition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw $Message
    }
}

function Read-FanInJsonFile {
    param([string]$Path, [string]$Description)

    Assert-FanInCondition -Condition (Test-Path -LiteralPath $Path -PathType Leaf) -Message "$Description is missing: $Path"
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "$Description is corrupt: $Path. $($_.Exception.Message)"
    }
}

function Get-FanInSingleFile {
    param([string]$Root, [string]$Name, [string]$Description)

    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter $Name -File)
    Assert-FanInCondition -Condition ($files.Count -eq 1) -Message "$Description must contain exactly one '$Name'; found $($files.Count)."
    return $files[0]
}

function Assert-FanInComponentIdentity {
    param(
        [object]$Evidence,
        [string]$Component,
        [string]$ExpectedRoot,
        [string]$ExpectedHead,
        [string]$ExpectedRunId,
        [string]$ExpectedRunAttempt
    )

    Assert-FanInCondition -Condition ($Evidence.schemaVersion -eq 1 -and [string]$Evidence.component -ceq $Component) -Message "Component evidence schema or component identity is invalid for '$Component'."
    Assert-FanInCondition -Condition ([string]$Evidence.repositoryHead -ceq $ExpectedHead) -Message "Component '$Component' repository SHA does not match the reviewed head."
    Assert-FanInCondition -Condition ([string]$Evidence.githubRunId -ceq $ExpectedRunId -and [string]$Evidence.githubRunAttempt -ceq $ExpectedRunAttempt) -Message "Component '$Component' workflow run identity does not match the fan-in run."
}

function Assert-FanInArtifactManifest {
    param([string]$ResultsRoot, [object]$Evidence)

    $manifestFile = Get-FanInSingleFile -Root $ResultsRoot -Name "verification-component-manifest.json" -Description "Component artifact"
    $manifestHash = (Get-FileHash -LiteralPath $manifestFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-FanInCondition -Condition ($manifestHash -ceq [string]$Evidence.manifestSha256) -Message "Component artifact manifest SHA does not match its authenticated evidence."
    $manifest = Read-FanInJsonFile -Path $manifestFile.FullName -Description "Component artifact manifest"
    Assert-FanInCondition -Condition ($manifest.schemaVersion -eq 1) -Message "Component artifact manifest schema is unsupported."

    $entries = @($manifest.files)
    Assert-FanInCondition -Condition ($entries.Count -gt 0) -Message "Component artifact manifest contains no authenticated files."
    $entryPaths = @($entries | ForEach-Object { [string]$_.path })
    Assert-FanInCondition -Condition (@($entryPaths | Group-Object | Where-Object Count -gt 1).Count -eq 0) -Message "Component artifact manifest contains duplicate file paths."
    foreach ($entry in $entries) {
        $relativePath = [string]$entry.path
        Assert-FanInCondition -Condition (-not [IO.Path]::IsPathRooted($relativePath) -and $relativePath -notmatch '(^|/|\\)\.\.($|/|\\)') -Message "Component artifact manifest contains an unsafe path: $relativePath"
        $path = Join-Path $ResultsRoot ($relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        Assert-FanInCondition -Condition (Test-Path -LiteralPath $path -PathType Leaf) -Message "Authenticated component file is missing: $relativePath"
        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-FanInCondition -Condition ([int64]$entry.length -eq $file.Length -and $hash -ceq [string]$entry.sha256) -Message "Authenticated component file changed: $relativePath"
    }
}

function Assert-FanInWatchdogEvidence {
    param(
        [string]$ResultsRoot,
        [string]$Component,
        [string]$ExpectedHead,
        [string]$ExpectedRunId,
        [string]$ExpectedRunAttempt
    )

    $watchdogEvidenceFile = Get-FanInSingleFile -Root $ResultsRoot -Name "verification-watchdog-evidence.json" -Description "Watchdog evidence"
    $watchdogEvidence = Read-FanInJsonFile -Path $watchdogEvidenceFile.FullName -Description "Watchdog evidence"
    $expectedProperties = @("schemaVersion", "component", "mode", "repositoryHead", "githubRunId", "githubRunAttempt", "deadlineSeconds", "elapsedSeconds", "exitCode", "completionMarkerCount", "status", "watchdogLogSha256", "componentEvidenceSha256", "componentManifestSha256")
    $actualProperties = @($watchdogEvidence.PSObject.Properties.Name | Sort-Object)
    Assert-FanInCondition -Condition ((@($expectedProperties | Sort-Object) -join "|") -ceq ($actualProperties -join "|")) -Message "Watchdog evidence schema is not exact."
    $expectedDeadlineSeconds = if ($Component -ceq "solution") { 1500 } elseif ($Component -ceq "static-contracts" -or $Component -ceq "nested-process") { 600 } else { throw "Unsupported fan-in component: $Component" }
    $elapsedSeconds = [double]$watchdogEvidence.elapsedSeconds
    Assert-FanInCondition -Condition ($watchdogEvidence.schemaVersion -eq 1 -and [string]$watchdogEvidence.component -ceq $Component -and [string]$watchdogEvidence.mode -ceq "promotion" -and [string]$watchdogEvidence.repositoryHead -ceq $ExpectedHead -and [string]$watchdogEvidence.githubRunId -ceq $ExpectedRunId -and [string]$watchdogEvidence.githubRunAttempt -ceq $ExpectedRunAttempt -and [int]$watchdogEvidence.deadlineSeconds -eq $expectedDeadlineSeconds -and $elapsedSeconds -ge 0 -and $elapsedSeconds -le $expectedDeadlineSeconds -and [int]$watchdogEvidence.exitCode -eq 0 -and [int]$watchdogEvidence.completionMarkerCount -eq 1 -and [string]$watchdogEvidence.status -ceq "passed") -Message "Watchdog evidence identity, status, or measured bounds are invalid for '$Component'."

    $watchdogFile = Get-FanInSingleFile -Root $ResultsRoot -Name "watchdog.log" -Description "Component watchdog evidence"
    $componentEvidenceFile = Get-FanInSingleFile -Root $ResultsRoot -Name "verification-component-evidence.json" -Description "Component evidence"
    $componentManifestFile = Get-FanInSingleFile -Root $ResultsRoot -Name "verification-component-manifest.json" -Description "Component manifest"
    Assert-FanInCondition -Condition ((Get-FileHash -LiteralPath $watchdogFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq [string]$watchdogEvidence.watchdogLogSha256) -Message "Watchdog evidence does not authenticate watchdog.log."
    Assert-FanInCondition -Condition ((Get-FileHash -LiteralPath $componentEvidenceFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq [string]$watchdogEvidence.componentEvidenceSha256) -Message "Watchdog evidence does not authenticate component evidence."
    Assert-FanInCondition -Condition ((Get-FileHash -LiteralPath $componentManifestFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq [string]$watchdogEvidence.componentManifestSha256) -Message "Watchdog evidence does not authenticate component manifest."
}

function Assert-FanInReceiptClosedWorld {
    param([string]$ResultsRoot, [object]$Manifest)

    $manifestPaths = @($Manifest.files | ForEach-Object { [string]$_.path })
    $allowedPaths = @($manifestPaths + @("verification-component-evidence.json", "verification-component-manifest.json", "verification-watchdog-evidence.json", "watchdog.log")) | Sort-Object -Unique
    $actualPaths = @(Get-ChildItem -LiteralPath $ResultsRoot -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($ResultsRoot, $_.FullName).Replace([IO.Path]::DirectorySeparatorChar, "/") } | Sort-Object -Unique)
    $unexpectedPaths = @($actualPaths | Where-Object { $allowedPaths -notcontains $_ })
    $missingPaths = @($allowedPaths | Where-Object { $actualPaths -notcontains $_ })
    Assert-FanInCondition -Condition ($unexpectedPaths.Count -eq 0 -and $missingPaths.Count -eq 0 -and $actualPaths.Count -eq $allowedPaths.Count) -Message "Component receipt is not a closed world. unexpected=[$($unexpectedPaths -join ',')] missing=[$($missingPaths -join ',')]"
}

function Assert-FanInCompletionEvidence {
    param([string]$ResultsRoot, [string]$Component)

    $watchdogFile = Get-FanInSingleFile -Root $ResultsRoot -Name "watchdog.log" -Description "Component watchdog evidence"
    $text = Get-Content -LiteralPath $watchdogFile.FullName -Raw
    $markers = @([regex]::Matches($text, '(?m)^VERIFY_COMPLETE[^\r\n]*') | ForEach-Object Value)
    $pattern = "^VERIFY_COMPLETE schema_version=1 component=$Component status=passed elapsed_seconds=[0-9]+(?:\.[0-9]+)?\r?$"
    Assert-FanInCondition -Condition ($markers.Count -eq 1 -and $markers[0] -match $pattern) -Message "Component '$Component' must contain exactly one identity-bearing terminal marker."
}

function Assert-FanInPhaseCompletions {
    param([string]$ResultsRoot, [string[]]$Names)

    $watchdogFile = Get-FanInSingleFile -Root $ResultsRoot -Name "watchdog.log" -Description "Component watchdog evidence"
    $text = Get-Content -LiteralPath $watchdogFile.FullName -Raw
    foreach ($name in $Names) {
        $pattern = '(?m)^VERIFY_PHASE_COMPLETE name=' + [regex]::Escape($name) + ' elapsed_seconds=[0-9]+(?:\.[0-9]+)? completed_at_utc=\S+\r?$'
        $matches = @([regex]::Matches($text, $pattern))
        Assert-FanInCondition -Condition ($matches.Count -eq 1) -Message "Component watchdog evidence must contain exactly one successful phase: $name"
    }
}

function Get-FanInSourceOwnedLaneDefinitions {
    param([ValidateSet("Solution", "NestedProcess")] [string]$Component)

    . (Join-Path $PSScriptRoot "verification-test-lanes.ps1")
    $testsRoot = Join-Path (Split-Path -Parent $PSScriptRoot) "tests"
    $projects = @(Get-ChildItem -LiteralPath $testsRoot -Recurse -Filter "*.csproj" | Where-Object {
        $_.Name -ne "EmbodySense.CancellationHost.csproj" -and
        $_.Name -ne "EmbodySense.E2EBrowserHost.csproj" -and
        $_.Name -ne "EmbodySense.HumanInputContinuationHost.csproj" -and
        $_.Name -ne "EmbodySense.Tests.Support.csproj"
    } | Sort-Object FullName)
    if ($Component -ceq "NestedProcess") {
        $projects = @($projects | Where-Object { $_.Name -ceq "EmbodySense.Core.Startup.Tests.csproj" })
    }

    $definitions = [Collections.Generic.List[object]]::new()
    foreach ($project in $projects) {
        $lanes = @(Get-VerificationTestProjectLanes -TestProject $project -NestedProcessOnly:($Component -ceq "NestedProcess") -SolutionCoreOnly:($Component -ceq "Solution"))
        foreach ($lane in $lanes) {
            $additionalExclusions = @(if ($project.Name -ceq "EmbodySense.E2ETests.csproj") { "BrowserFlowTests" })
            $definitions.Add([pscustomobject]@{ name = "$($project.BaseName)-$($lane.Name)"; projectName = $project.BaseName; filter = Get-VerificationTestLaneFilter -Lane $lane -AdditionalExclusions $additionalExclusions })
        }
    }
    return @($definitions)
}

function Assert-FanInSolutionEvidence {
    param([string]$ResultsRoot, [object]$Evidence)

    $laneDefinitions = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-test-lanes.json") -Description "Required-test lane definitions"
    $lanes = @($laneDefinitions.lanes)
    Assert-FanInCondition -Condition ($laneDefinitions.schemaVersion -eq 1 -and $lanes.Count -eq 9 -and @($lanes | Group-Object name | Where-Object Count -ne 1).Count -eq 0 -and @($lanes | Where-Object { [string]$_.name -ceq "EmbodySense.Core.Startup.Tests-nested-process" }).Count -eq 0) -Message "Solution evidence must contain nine unique non-nested required-test lanes."
    Assert-FanInCondition -Condition ([int]$Evidence.laneCount -eq 9) -Message "Solution component evidence lane count is not nine."
    $sourceLanes = @(Get-FanInSourceOwnedLaneDefinitions -Component "Solution")
    Assert-FanInCondition -Condition ($sourceLanes.Count -eq 9) -Message "Solution source-owned lane partition is not exactly nine lanes."
    $sourceLaneByName = @{}
    foreach ($sourceLane in $sourceLanes) { $sourceLaneByName[$sourceLane.name] = $sourceLane }
    foreach ($lane in $lanes) {
        Assert-FanInCondition -Condition ($sourceLaneByName.ContainsKey([string]$lane.name) -and [string]$sourceLaneByName[[string]$lane.name].projectName -ceq [string]$lane.projectName -and [string]$sourceLaneByName[[string]$lane.name].filter -ceq [string]$lane.filter) -Message "Solution required-test lane is not the exact source-owned partition: $($lane.name)"
    }
    $manifest = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "verification-component-manifest.json") -Description "Solution component manifest"
    $manifestPaths = @($manifest.files | ForEach-Object { [string]$_.path })
    foreach ($requiredPath in @("required-test-lanes.json", "required-test-partition.json", "required-execution-tests.json", "required-test-report.json", "coverage-manifest.json", "coverage-summary.json")) {
        Assert-FanInCondition -Condition ($manifestPaths -contains $requiredPath) -Message "Solution component manifest omitted required control evidence: $requiredPath"
    }
    $solutionTrxPaths = @($manifestPaths | Where-Object { $_ -match '\.trx$' })
    Assert-FanInCondition -Condition ($solutionTrxPaths.Count -eq 9 -and @($manifestPaths | Where-Object { $_ -match '\.cobertura\.xml$' }).Count -gt 0 -and $manifestPaths.Count -gt 15) -Message "Solution component manifest must contain six control reports, nine TRX reports, and authenticated coverage reports."

    $partition = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-test-partition.json") -Description "Required-test partition report"
    Assert-FanInCondition -Condition ($partition.schemaVersion -eq 1 -and [int]$partition.laneDefinitionCount -eq 9 -and [int]$partition.canonicalInventoryCount -eq 9 -and [int]$partition.canonicalTestCount -gt 0 -and [int]$partition.laneTestCount -eq [int]$partition.canonicalTestCount -and @($partition.emptyLanes).Count -eq 0 -and @($partition.missing).Count -eq 0 -and @($partition.unexpected).Count -eq 0 -and @($partition.overlap).Count -eq 0 -and @($partition.duplicateCanonical).Count -eq 0 -and @($partition.duplicateExecutionIds).Count -eq 0) -Message "Solution partition reconciliation is incomplete or non-clean."

    $executionInventory = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-execution-tests.json") -Description "Required execution inventory"
    Assert-FanInCondition -Condition ($executionInventory.schemaVersion -eq 1 -and [int]$executionInventory.totalTests -eq [int]$partition.laneTestCount -and @($executionInventory.tests).Count -eq [int]$executionInventory.totalTests) -Message "Solution execution inventory is incomplete."
    $inventoryReport = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-test-report.json") -Description "Required-test inventory report"
    Assert-FanInCondition -Condition ($inventoryReport.schemaVersion -eq 1 -and [int]$inventoryReport.expectedCount -gt 0 -and [int]$inventoryReport.executedCount -ge [int]$inventoryReport.uniqueExecutedCount -and [int]$inventoryReport.expectedCount -eq [int]$inventoryReport.uniqueExecutedCount -and @($inventoryReport.missing).Count -eq 0 -and @($inventoryReport.unexpected).Count -eq 0 -and @($inventoryReport.crossReportOverlap).Count -eq 0 -and @($inventoryReport.duplicateExecutionId).Count -eq 0 -and @($inventoryReport.nonPassing).Count -eq 0) -Message "Solution execution inventory reconciliation is incomplete or non-clean."

    $coverageManifest = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "coverage-manifest.json") -Description "Coverage manifest"
    Assert-FanInCondition -Condition ($coverageManifest.schemaVersion -eq 1 -and [int]$coverageManifest.laneReportCount -eq 9 -and [int]$coverageManifest.childReportCount -ge 0 -and [int]$coverageManifest.aliasReportCount -ge 0 -and @($coverageManifest.reports).Count -eq ([int]$coverageManifest.laneReportCount + [int]$coverageManifest.childReportCount) -and @($coverageManifest.aliases).Count -eq [int]$coverageManifest.aliasReportCount) -Message "Solution coverage manifest counts are incomplete."
    $coverageSummary = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "coverage-summary.json") -Description "Coverage summary"
    Assert-FanInCondition -Condition ($coverageSummary.schemaVersion -eq 1 -and [double]$coverageSummary.threshold -ge 0.9 -and @($coverageSummary.reports).Count -gt 0 -and @($coverageSummary.packages).Count -gt 0 -and @($coverageSummary.failures).Count -eq 0) -Message "Solution coverage summary is incomplete or contains failures."
    Assert-FanInCondition -Condition ([bool]$Evidence.inventoryComplete -and [bool]$Evidence.coverageComplete) -Message "Solution component evidence does not authenticate inventory and coverage completion."
}

function Assert-FanInNestedEvidence {
    param([string]$ResultsRoot, [object]$Evidence)

    Assert-FanInCondition -Condition ([int]$Evidence.laneCount -eq 1 -and [bool]$Evidence.inventoryComplete -and [bool]$Evidence.coverageComplete) -Message "Nested-process component evidence is incomplete."
    $laneDefinitions = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-test-lanes.json") -Description "Nested required-test lane definitions"
    $lanes = @($laneDefinitions.lanes)
    $expectedLaneName = "EmbodySense.Core.Startup.Tests-nested-process"
    $nestedFilter = [string]$lanes[0].filter
    Assert-FanInCondition -Condition ($laneDefinitions.schemaVersion -eq 1 -and $lanes.Count -eq 1 -and [string]$lanes[0].name -ceq $expectedLaneName -and [string]$lanes[0].projectName -ceq "EmbodySense.Core.Startup.Tests" -and @([regex]::Matches($nestedFilter, "FullyQualifiedName=")).Count -eq 5 -and $nestedFilter -match "VerificationTier!=Stress") -Message "Nested-process lane ownership is not the exact source-owned Startup fixture lane."
    $sourceLanes = @(Get-FanInSourceOwnedLaneDefinitions -Component "NestedProcess")
    Assert-FanInCondition -Condition ($sourceLanes.Count -eq 1 -and $sourceLanes[0].name -ceq $expectedLaneName -and $sourceLanes[0].projectName -ceq [string]$lanes[0].projectName -and $sourceLanes[0].filter -ceq $nestedFilter) -Message "Nested-process lane filter is not the exact source-owned Startup fixture partition."
    $manifest = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "verification-component-manifest.json") -Description "Nested component manifest"
    $manifestPaths = @($manifest.files | ForEach-Object { [string]$_.path })
    foreach ($requiredPath in @("required-test-lanes.json", "required-test-partition.json", "required-execution-tests.json", "required-test-report.json", "coverage-manifest.json", "coverage-summary.json")) {
        Assert-FanInCondition -Condition ($manifestPaths -contains $requiredPath) -Message "Nested component manifest omitted required control evidence: $requiredPath"
    }
    $nestedTrxPaths = @($manifestPaths | Where-Object { $_ -match '\.trx$' })
    $nestedCoveragePaths = @($manifestPaths | Where-Object { $_ -match '\.cobertura\.xml$' })
    Assert-FanInCondition -Condition ($nestedTrxPaths.Count -eq 1 -and $nestedCoveragePaths.Count -gt 0 -and $manifestPaths.Count -gt 7) -Message "Nested component manifest must contain one TRX and authenticated coverage reports."

    $partition = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-test-partition.json") -Description "Nested required-test partition report"
    Assert-FanInCondition -Condition ($partition.schemaVersion -eq 1 -and [int]$partition.laneDefinitionCount -eq 1 -and [int]$partition.canonicalInventoryCount -eq 1 -and [int]$partition.canonicalTestCount -eq 5 -and [int]$partition.laneTestCount -eq [int]$partition.canonicalTestCount -and @($partition.emptyLanes).Count -eq 0 -and @($partition.missing).Count -eq 0 -and @($partition.unexpected).Count -eq 0 -and @($partition.overlap).Count -eq 0 -and @($partition.duplicateCanonical).Count -eq 0 -and @($partition.duplicateExecutionIds).Count -eq 0) -Message "Nested-process partition reconciliation is incomplete or non-clean."
    $executionInventory = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-execution-tests.json") -Description "Nested required execution inventory"
    Assert-FanInCondition -Condition ($executionInventory.schemaVersion -eq 1 -and [int]$executionInventory.totalTests -eq [int]$partition.laneTestCount -and @($executionInventory.tests).Count -eq [int]$executionInventory.totalTests) -Message "Nested required execution inventory is incomplete."
    $inventoryReport = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-test-report.json") -Description "Nested required-test inventory report"
    Assert-FanInCondition -Condition ($inventoryReport.schemaVersion -eq 1 -and [int]$inventoryReport.expectedCount -eq 5 -and [int]$inventoryReport.executedCount -eq 5 -and [int]$inventoryReport.expectedCount -eq [int]$inventoryReport.uniqueExecutedCount -and @($inventoryReport.missing).Count -eq 0 -and @($inventoryReport.unexpected).Count -eq 0 -and @($inventoryReport.crossReportOverlap).Count -eq 0 -and @($inventoryReport.duplicateExecutionId).Count -eq 0 -and @($inventoryReport.nonPassing).Count -eq 0) -Message "Nested required-test inventory reconciliation is incomplete or non-clean."
    $coverageManifest = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "coverage-manifest.json") -Description "Nested coverage manifest"
    Assert-FanInCondition -Condition ($coverageManifest.schemaVersion -eq 1 -and [int]$coverageManifest.laneReportCount -eq 1 -and [int]$coverageManifest.childReportCount -ge 0 -and [int]$coverageManifest.aliasReportCount -ge 0 -and @($coverageManifest.reports).Count -eq ([int]$coverageManifest.laneReportCount + [int]$coverageManifest.childReportCount) -and @($coverageManifest.aliases).Count -eq [int]$coverageManifest.aliasReportCount) -Message "Nested coverage manifest counts are incomplete."
    $coverageSummary = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "coverage-summary.json") -Description "Nested coverage summary"
    Assert-FanInCondition -Condition ($coverageSummary.schemaVersion -eq 1 -and [double]$coverageSummary.threshold -ge 0 -and @($coverageSummary.reports).Count -gt 0 -and @($coverageSummary.failures).Count -eq 0) -Message "Nested collect-only coverage summary is incomplete or contains report failures."
}

function Assert-FanInStaticEvidence {
    param([string]$ResultsRoot, [object]$Evidence)

    $contractNames = @("verify-sdk-diagnostics.tests.ps1", "verify-preflight-overlap.tests.ps1", "verify-coverage.tests.ps1", "verify-bounded-phases.tests.ps1", "verify-parallel.tests.ps1", "verify-test-inventory.tests.ps1", "verify-watchdog.tests.ps1", "verify-promotion-fan-in.tests.ps1")
    foreach ($contractName in $contractNames) {
        $contractFile = Get-FanInSingleFile -Root (Join-Path $ResultsRoot "Logs") -Name "$contractName.log" -Description "Static contract evidence"
    }
    foreach ($logName in @("frontend-preflight.log", "format-whitespace.log", "format-naming-style.log", "git-diff-check.log")) {
        $log = Get-FanInSingleFile -Root (Join-Path $ResultsRoot "Logs") -Name $logName -Description "Static phase evidence"
    }
    $manifest = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "verification-component-manifest.json") -Description "Static component manifest"
    $manifestPaths = @($manifest.files | ForEach-Object { [string]$_.path })
    foreach ($requiredPath in @("Logs/verify-sdk-diagnostics.tests.ps1.log", "Logs/verify-preflight-overlap.tests.ps1.log", "Logs/verify-coverage.tests.ps1.log", "Logs/verify-bounded-phases.tests.ps1.log", "Logs/verify-parallel.tests.ps1.log", "Logs/verify-test-inventory.tests.ps1.log", "Logs/verify-watchdog.tests.ps1.log", "Logs/verify-promotion-fan-in.tests.ps1.log", "Logs/frontend-preflight.log", "Logs/restore-static.log", "Logs/format-whitespace.log", "Logs/format-naming-style.log", "Logs/git-diff-check.log")) {
        Assert-FanInCondition -Condition ($manifestPaths -contains $requiredPath) -Message "Static component manifest omitted required evidence: $requiredPath"
    }
    Assert-FanInCondition -Condition ($manifestPaths.Count -eq 13) -Message "Static component manifest must contain exactly eight contract logs and five static phase logs."
    $phaseNames = @($contractNames | ForEach-Object { "contract-$([IO.Path]::GetFileNameWithoutExtension($_))" }) + @("frontend-preflight", "restore-static", "format-whitespace", "format-naming-style", "git-diff-check")
    Assert-FanInPhaseCompletions -ResultsRoot $ResultsRoot -Names $phaseNames
    Assert-FanInCondition -Condition ([int]$Evidence.staticContractCount -eq 8 -and [bool]$Evidence.frontendComplete -and [bool]$Evidence.formatComplete -and [bool]$Evidence.diffComplete) -Message "Static component evidence is incomplete."
}

function Read-FanInComponent {
    param([string]$ArtifactRoot, [string]$Component, [string]$ExpectedHead, [string]$ExpectedRunId, [string]$ExpectedRunAttempt)

    $evidenceFile = Get-FanInSingleFile -Root $ArtifactRoot -Name "verification-component-evidence.json" -Description "Component evidence"
    $resultsRoot = $evidenceFile.DirectoryName
    $evidence = Read-FanInJsonFile -Path $evidenceFile.FullName -Description "Component evidence"
    Assert-FanInComponentIdentity -Evidence $evidence -Component $Component -ExpectedRoot $resultsRoot -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt
    Assert-FanInArtifactManifest -ResultsRoot $resultsRoot -Evidence $evidence
    Assert-FanInWatchdogEvidence -ResultsRoot $resultsRoot -Component $Component -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt
    $componentManifest = Read-FanInJsonFile -Path (Join-Path $resultsRoot "verification-component-manifest.json") -Description "Component manifest"
    Assert-FanInReceiptClosedWorld -ResultsRoot $resultsRoot -Manifest $componentManifest
    Assert-FanInCompletionEvidence -ResultsRoot $resultsRoot -Component $Component
    if ($Component -ceq "solution") {
        Assert-FanInSolutionEvidence -ResultsRoot $resultsRoot -Evidence $evidence
    }
    elseif ($Component -ceq "nested-process") {
        Assert-FanInNestedEvidence -ResultsRoot $resultsRoot -Evidence $evidence
    }
    else {
        Assert-FanInStaticEvidence -ResultsRoot $resultsRoot -Evidence $evidence
    }

    return [pscustomobject]@{
        ResultsRoot = $resultsRoot
        Evidence = $evidence
        Manifest = $componentManifest
    }
}

function Get-FanInCoverageArtifactRelativePath {
    param([string]$Path, [string]$DeclaredResultsRoot)

    $normalizedPath = $Path.Replace('\', '/')
    $normalizedRoot = $DeclaredResultsRoot.Replace('\', '/').TrimEnd('/')
    if ($normalizedPath.StartsWith($normalizedRoot + '/', [StringComparison]::OrdinalIgnoreCase)) {
        return $normalizedPath.Substring($normalizedRoot.Length + 1)
    }

    throw "Coverage report path cannot be mapped to its declared VerificationResults root: $Path"
}

function Get-FanInArtifactManifestEntry {
    param([object[]]$Entries, [string]$RelativePath, [string]$Description)

    $normalized = $RelativePath.Replace('\', '/')
    $matches = @($Entries | Where-Object {
        $candidate = ([string]$_.path).Replace('\', '/')
        $candidate -ceq $normalized
    })
    Assert-FanInCondition -Condition ($matches.Count -eq 1) -Message "$Description is not represented exactly once in the component artifact manifest: $RelativePath"
    return $matches[0]
}

function Get-FanInCoverageArtifactFile {
    param(
        [string]$ResultsRoot,
        [object[]]$ArtifactEntries,
        [string]$CoveragePath,
        [string]$DeclaredResultsRoot,
        [string]$Description
    )

    $relativePath = Get-FanInCoverageArtifactRelativePath -Path $CoveragePath -DeclaredResultsRoot $DeclaredResultsRoot
    $entry = Get-FanInArtifactManifestEntry -Entries $ArtifactEntries -RelativePath $relativePath -Description $Description
    $artifactPath = Join-Path $ResultsRoot ([string]$entry.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
    Assert-FanInCondition -Condition (Test-Path -LiteralPath $artifactPath -PathType Leaf) -Message "$Description is missing from the downloaded component receipt: $CoveragePath"
    return [pscustomobject]@{ Path = $artifactPath; Entry = $entry; RelativePath = $relativePath }
}

function Read-FanInCoverageXml {
    param([string]$Path, [string]$Description)

    try {
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = $null
        try {
            $reader = [Xml.XmlReader]::Create($Path, $settings)
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
            return $document
        }
        finally {
            if ($null -ne $reader) { $reader.Dispose() }
        }
    }
    catch {
        throw "$Description is malformed XML: $Path. $($_.Exception.Message)"
    }
}

function Resolve-FanInCoverageFilePath {
    param([string]$FileName, [string]$PackageName, [string]$RepositoryRoot)

    $normalized = $FileName.Replace('\', '/')
    $packageRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path $RepositoryRoot "src") $PackageName)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $sourceRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot "src")).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $resolvedPath = $null
    $sourceMarker = "src/"
    $sourceIndex = $normalized.IndexOf($sourceMarker, [StringComparison]::OrdinalIgnoreCase)
    if ($sourceIndex -ge 0) {
        $resolvedPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ($normalized.Substring($sourceIndex).Replace('/', [IO.Path]::DirectorySeparatorChar))))
    }
    else {
        $packagePrefix = $PackageName + "/"
        if ($normalized.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            $resolvedPath = [IO.Path]::GetFullPath((Join-Path $packageRoot $normalized.Substring($packagePrefix.Length)))
        }
    }

    # The pinned SDK reports the regex generator's virtual source in Cobertura but
    # does not emit that file to disk. Retain its authenticated line keys and hits.
    $generatedRegexPath = Join-Path $packageRoot "obj/Release/net10.0/System.Text.RegularExpressions.Generator/System.Text.RegularExpressions.Generator.RegexGenerator/RegexGenerator.g.cs"
    $isGeneratedRegexSource = $null -ne $resolvedPath -and $resolvedPath.Equals($generatedRegexPath, [StringComparison]::OrdinalIgnoreCase)
    if ($null -eq $resolvedPath -or (-not $resolvedPath.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and $resolvedPath -cne $packageRoot) -or (-not $resolvedPath.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and $resolvedPath -cne $sourceRoot) -or (-not $isGeneratedRegexSource -and -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf))) {
        throw "Coverage report file path does not identify an existing source file beneath src/${PackageName}: $FileName"
    }
    return $resolvedPath
}

function Invoke-FanInCoverageAggregate {
    param(
        [object[]]$Components,
        [string]$RepositoryRoot
    )

    $sourceDirectories = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "src") -Directory -Recurse | Where-Object { Test-Path (Join-Path $_.FullName ($_.Name + ".csproj")) } | Sort-Object Name)
    $expectedPackages = @($sourceDirectories | ForEach-Object Name)
    Assert-FanInCondition -Condition ($expectedPackages.Count -gt 0) -Message "Coverage aggregation cannot establish the canonical production-package inventory."
    $lineMaps = @{}
    foreach ($package in $expectedPackages) { $lineMaps[$package] = @{} }
    $reportCount = 0
    $canonicalReportPaths = @{}

    foreach ($component in $Components) {
        $resultsRoot = [string]$component.ResultsRoot
        $manifestEntries = @($component.Manifest.files)
        $coverageManifest = Read-FanInJsonFile -Path (Join-Path $resultsRoot "coverage-manifest.json") -Description "Component coverage manifest"
        $declaredResultsRoot = [string]$coverageManifest.resultsRoot
        Assert-FanInCondition -Condition ($coverageManifest.schemaVersion -eq 1 -and -not [string]::IsNullOrWhiteSpace($declaredResultsRoot)) -Message "Component coverage manifest has an invalid declared results root."
        $reports = @($coverageManifest.reports)
        $aliases = @($coverageManifest.aliases)
        Assert-FanInCondition -Condition ($reports.Count -eq ([int]$coverageManifest.laneReportCount + [int]$coverageManifest.childReportCount) -and $aliases.Count -eq [int]$coverageManifest.aliasReportCount) -Message "Component coverage manifest report counts are inconsistent."
        $laneDocument = Read-FanInJsonFile -Path (Join-Path $resultsRoot "required-test-lanes.json") -Description "Component source-owned lane definitions"
        $sourceLaneNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $sourceProjectNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($lane in @($laneDocument.lanes)) {
            Assert-FanInCondition -Condition (-not [string]::IsNullOrWhiteSpace([string]$lane.name) -and -not [string]::IsNullOrWhiteSpace([string]$lane.projectName)) -Message "Component source-owned lane definitions contain an incomplete lane identity."
            [void]$sourceLaneNames.Add("tests-$($lane.name)")
            [void]$sourceProjectNames.Add([string]$lane.projectName)
        }
        Assert-FanInCondition -Condition ([int]$coverageManifest.laneReportCount -eq $sourceLaneNames.Count) -Message "Component coverage manifest lane count does not match its source-owned lane definition."

        $artifactByCoveragePath = @{}
        $laneReportNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($report in $reports) {
            $coveragePath = [string]$report.path
            Assert-FanInCondition -Condition (-not [string]::IsNullOrWhiteSpace($coveragePath) -and $coveragePath.EndsWith("coverage.cobertura.xml", [StringComparison]::OrdinalIgnoreCase)) -Message "Component coverage manifest contains an invalid report path."
            $reportKind = [string]$report.kind
            if ($reportKind -ceq "lane") {
                $laneName = [string]$report.laneName
                Assert-FanInCondition -Condition ($sourceLaneNames.Contains($laneName) -and $laneReportNames.Add($laneName) -and -not [string]::IsNullOrWhiteSpace([string]$report.laneResultsRoot) -and -not [string]::IsNullOrWhiteSpace([string]$report.trxPath) -and -not [string]::IsNullOrWhiteSpace([string]$report.deploymentRoot)) -Message "Component coverage manifest contains a lane report that is not bound to one source-owned lane."
            }
            elseif ($reportKind -ceq "child") {
                Assert-FanInCondition -Condition ($sourceProjectNames.Contains([string]$report.projectName) -and -not [string]::IsNullOrWhiteSpace([string]$report.childResultsRoot)) -Message "Component coverage manifest contains a child-process report that is not bound to one selected project."
            }
            else {
                throw "Component coverage manifest contains an unsupported report kind."
            }
            $artifact = Get-FanInCoverageArtifactFile -ResultsRoot $resultsRoot -ArtifactEntries $manifestEntries -CoveragePath $coveragePath -DeclaredResultsRoot $declaredResultsRoot -Description "Coverage report"
            if ($reportKind -ceq "lane") {
                $trxArtifact = Get-FanInCoverageArtifactFile -ResultsRoot $resultsRoot -ArtifactEntries $manifestEntries -CoveragePath ([string]$report.trxPath) -DeclaredResultsRoot $declaredResultsRoot -Description "Coverage lane TRX"
                Assert-FanInCondition -Condition (("tests-" + [IO.Path]::GetFileNameWithoutExtension($trxArtifact.Path)) -ceq [string]$report.laneName) -Message "Coverage lane report TRX filename is not bound to its source-owned lane: $coveragePath"
            }
            Assert-FanInCondition -Condition ([int64]$report.length -eq (Get-Item -LiteralPath $artifact.Path).Length -and [string]$report.sha256 -ceq (Get-FileHash -LiteralPath $artifact.Path -Algorithm SHA256).Hash.ToLowerInvariant()) -Message "Coverage report hash or length is not authenticated: $coveragePath"
            $artifactByCoveragePath[$coveragePath] = $artifact
            $canonicalKey = $artifact.RelativePath.ToUpperInvariant()
            Assert-FanInCondition -Condition (-not $canonicalReportPaths.ContainsKey($canonicalKey)) -Message "Coverage aggregate contains a duplicate canonical report path: $coveragePath"
            $canonicalReportPaths[$canonicalKey] = $true
            $document = Read-FanInCoverageXml -Path $artifact.Path -Description "Coverage report"
            Assert-FanInCondition -Condition ($null -ne $document.DocumentElement -and $document.DocumentElement.LocalName -ceq "coverage") -Message "Coverage report has an invalid document root: $coveragePath"
            $packageNodes = @($document.SelectNodes("/coverage/packages/package"))
            foreach ($packageNode in $packageNodes) {
                $packageName = [string]$packageNode.name
                if (-not $lineMaps.ContainsKey($packageName)) { continue }
                foreach ($classNode in @($packageNode.SelectNodes("classes/class"))) {
                    $filePath = Resolve-FanInCoverageFilePath -FileName ([string]$classNode.filename) -PackageName $packageName -RepositoryRoot $RepositoryRoot
                    $fileKey = $filePath.ToUpperInvariant()
                    if (-not $lineMaps[$packageName].ContainsKey($fileKey)) { $lineMaps[$packageName][$fileKey] = @{} }
                    foreach ($lineNode in @($classNode.SelectNodes("lines/line"))) {
                        $lineNumber = 0
                        $hits = 0
                        if (-not [int]::TryParse([string]$lineNode.number, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$lineNumber) -or -not [int]::TryParse([string]$lineNode.hits, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$hits) -or $lineNumber -lt 0 -or $hits -lt 0) {
                            throw "Coverage report contains an invalid line number or hit count: $coveragePath"
                        }
                        if (-not $lineMaps[$packageName][$fileKey].ContainsKey($lineNumber) -or $hits -gt $lineMaps[$packageName][$fileKey][$lineNumber]) {
                            $lineMaps[$packageName][$fileKey][$lineNumber] = $hits
                        }
                    }
                }
            }
            $reportCount++
        }
        Assert-FanInCondition -Condition ($laneReportNames.Count -eq [int]$coverageManifest.laneReportCount) -Message "Component coverage manifest does not authenticate one canonical report for every source-owned lane."

        foreach ($alias in $aliases) {
            $aliasPath = [string]$alias.path
            $canonicalPath = [string]$alias.canonicalPath
            Assert-FanInCondition -Condition ($artifactByCoveragePath.ContainsKey($canonicalPath)) -Message "Coverage alias does not reference an authenticated canonical report: $aliasPath"
            $aliasArtifact = Get-FanInCoverageArtifactFile -ResultsRoot $resultsRoot -ArtifactEntries $manifestEntries -CoveragePath $aliasPath -DeclaredResultsRoot $declaredResultsRoot -Description "Coverage staging alias"
            $canonicalArtifact = $artifactByCoveragePath[$canonicalPath]
            Assert-FanInCondition -Condition ([int64]$alias.length -eq (Get-Item -LiteralPath $aliasArtifact.Path).Length -and [string]$alias.sha256 -ceq (Get-FileHash -LiteralPath $aliasArtifact.Path -Algorithm SHA256).Hash.ToLowerInvariant() -and [IO.File]::ReadAllBytes($aliasArtifact.Path).Length -eq [IO.File]::ReadAllBytes($canonicalArtifact.Path).Length -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($aliasArtifact.Path)) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($canonicalArtifact.Path))) -Message "Coverage staging alias is not an authenticated byte-identical copy: $aliasPath"
        }
    }

    Assert-FanInCondition -Condition ($reportCount -gt 0) -Message "Coverage aggregate received no authenticated reports."
    $summaries = [Collections.Generic.List[object]]::new()
    foreach ($packageName in $expectedPackages) {
        $packageFiles = $lineMaps[$packageName]
        $totalLines = 0
        $coveredLines = 0
        foreach ($fileLines in $packageFiles.Values) {
            $totalLines += $fileLines.Count
            $coveredLines += @($fileLines.Values | Where-Object { $_ -gt 0 }).Count
        }
        Assert-FanInCondition -Condition ($totalLines -gt 0) -Message "Coverage aggregate has no executable lines for production package '$packageName'."
        $lineRate = $coveredLines / $totalLines
        Assert-FanInCondition -Condition ($lineRate -ge 0.90) -Message "Combined coverage for production package '$packageName' is $([Math]::Round($lineRate * 100, 2))%, below the unchanged 90% floor."
        $summaries.Add([ordered]@{ package = $packageName; coveredLines = $coveredLines; totalLines = $totalLines; lineRate = [Math]::Round($lineRate, 8); percent = [Math]::Round($lineRate * 100, 2) })
    }
    return [pscustomobject]@{ ReportCount = $reportCount; PackageCount = $summaries.Count; Packages = @($summaries) }
}

function Read-FanInTrxResults {
    param([string]$ResultsRoot, [string]$Description)

    $files = @(Get-ChildItem -LiteralPath $ResultsRoot -Recurse -Filter "*.trx" -File | Sort-Object FullName)
    Assert-FanInCondition -Condition ($files.Count -gt 0) -Message "$Description contains no TRX reports."
    $results = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        try { $trx = Read-FanInCoverageXml -Path $file.FullName -Description "$Description TRX" } catch { throw "$Description TRX is corrupt: $($file.FullName). $($_.Exception.Message)" }
        $namespace = [Xml.XmlNamespaceManager]::new($trx.NameTable)
        $namespace.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        $nodes = @($trx.SelectNodes("/t:TestRun/t:Results/t:UnitTestResult", $namespace))
        Assert-FanInCondition -Condition ($nodes.Count -gt 0) -Message "$Description TRX contains no test results: $($file.FullName)"
        foreach ($node in $nodes) {
            $testId = [Guid]::Empty
            $executionId = [Guid]::Empty
            Assert-FanInCondition -Condition ([Guid]::TryParse([string]$node.testId, [ref]$testId) -and [Guid]::TryParse([string]$node.executionId, [ref]$executionId)) -Message "$Description TRX contains an invalid test or execution ID: $($file.FullName)"
            $results.Add([pscustomobject]@{ TestId = $testId.ToString("D"); ExecutionId = $executionId.ToString("D"); Outcome = [string]$node.outcome; Report = $file.FullName; Lane = [IO.Path]::GetFileNameWithoutExtension($file.Name) })
        }
    }
    return @($results)
}

function Invoke-FanInInventoryAggregate {
    param([object[]]$Components)

    $lanes = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $expectedById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $actualResults = [Collections.Generic.List[object]]::new()
    foreach ($component in $Components) {
        $resultsRoot = [string]$component.ResultsRoot
        $laneDocument = Read-FanInJsonFile -Path (Join-Path $resultsRoot "required-test-lanes.json") -Description "Component lane definition"
        foreach ($lane in @($laneDocument.lanes)) {
            $laneName = [string]$lane.name
            Assert-FanInCondition -Condition (-not [string]::IsNullOrWhiteSpace($laneName) -and -not $lanes.ContainsKey($laneName)) -Message "Aggregate lane definitions are not disjoint: $laneName"
            $lanes.Add($laneName, $lane)
        }
        $executionInventory = Read-FanInJsonFile -Path (Join-Path $resultsRoot "required-execution-tests.json") -Description "Component execution inventory"
        foreach ($test in @($executionInventory.tests)) {
            $testId = ([Guid][string]$test.id).ToString("D")
            Assert-FanInCondition -Condition (-not $expectedById.ContainsKey($testId)) -Message "Aggregate canonical execution inventory contains an overlapping test ID: $testId"
            $laneName = [string]$test.lane
            Assert-FanInCondition -Condition ($lanes.ContainsKey($laneName)) -Message "Aggregate execution inventory references an undeclared lane: $laneName"
            $expectedById.Add($testId, [pscustomobject]@{ Lane = $laneName; XunitId = [string]$test.xunitTestCaseUniqueId })
        }
        $actualResults.AddRange([object[]](Read-FanInTrxResults -ResultsRoot $resultsRoot -Description "Component receipt"))
    }

    Assert-FanInCondition -Condition ($lanes.Count -eq 10 -and @($lanes.Values | ForEach-Object projectName | Sort-Object -Unique).Count -eq 9) -Message "Aggregate must reconstruct exactly ten disjoint lanes across nine canonical projects."
    Assert-FanInCondition -Condition (@($lanes.Keys | Where-Object { $_ -ceq "EmbodySense.Core.Startup.Tests-nested-process" }).Count -eq 1) -Message "Aggregate is missing the exact Startup nested-process lane."
    $actualById = $actualResults | Group-Object TestId -AsHashTable
    foreach ($expectedId in $expectedById.Keys) {
        Assert-FanInCondition -Condition $actualById.ContainsKey($expectedId) -Message "Aggregate execution inventory is missing test ID: $expectedId"
        $reports = @($actualById[$expectedId] | ForEach-Object Report | Sort-Object -Unique)
        Assert-FanInCondition -Condition ($reports.Count -eq 1) -Message "Aggregate execution inventory overlaps test ID across component receipts: $expectedId"
    }
    foreach ($actualId in $actualById.Keys) {
        Assert-FanInCondition -Condition $expectedById.ContainsKey($actualId) -Message "Aggregate execution inventory contains an unexpected test ID: $actualId"
    }
    foreach ($actualResult in $actualResults) {
        Assert-FanInCondition -Condition $lanes.ContainsKey($actualResult.Lane) -Message "Aggregate TRX filename does not identify a source-owned lane: $($actualResult.Lane)"
        Assert-FanInCondition -Condition ($expectedById[$actualResult.TestId].Lane -ceq $actualResult.Lane) -Message "Aggregate TRX test result is attributed to the wrong source-owned lane: $($actualResult.TestId)"
    }
    $executionIds = @($actualResults | Group-Object ExecutionId | Where-Object Count -ne 1)
    Assert-FanInCondition -Condition ($executionIds.Count -eq 0) -Message "Aggregate execution inventory contains duplicate execution IDs."
    $nonPassing = @($actualResults | Where-Object { $_.Outcome -cne "Passed" })
    Assert-FanInCondition -Condition ($nonPassing.Count -eq 0) -Message "Aggregate execution inventory contains non-passing test results."
    Assert-FanInCondition -Condition ($actualResults.Count -ge $expectedById.Count -and $actualById.Count -eq $expectedById.Count) -Message "Aggregate execution inventory counts are not exact."
    return [pscustomobject]@{ LaneCount = $lanes.Count; ProjectCount = @($lanes.Values | ForEach-Object projectName | Sort-Object -Unique).Count; ExpectedTestCount = $expectedById.Count; ExecutedRows = $actualResults.Count }
}

function Invoke-VerificationPromotionFanIn {
    param(
        [string]$SolutionArtifactRoot,
        [string]$StaticArtifactRoot,
        [string]$ExpectedHead,
        [string]$ExpectedRunId,
        [string]$ExpectedRunAttempt,
        [string]$SolutionResult,
        [string]$StaticResult,
        [string]$NestedArtifactRoot,
        [string]$NestedResult
    )

    Assert-FanInCondition -Condition ($SolutionResult -ceq "success" -and $StaticResult -ceq "success" -and $NestedResult -ceq "success") -Message "All three hosted verification children must succeed before fan-in."
    $solution = Read-FanInComponent -ArtifactRoot $SolutionArtifactRoot -Component "solution" -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt
    $nested = Read-FanInComponent -ArtifactRoot $NestedArtifactRoot -Component "nested-process" -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt
    $static = Read-FanInComponent -ArtifactRoot $StaticArtifactRoot -Component "static-contracts" -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt
    $inventory = Invoke-FanInInventoryAggregate -Components @($solution, $nested)
    $coverage = Invoke-FanInCoverageAggregate -Components @($solution, $nested) -RepositoryRoot (Split-Path -Parent $PSScriptRoot)
    Write-Output "VERIFY_PROMOTION_FAN_IN schema_version=1 status=passed solution=solution nested=nested-process static=static-contracts lanes=$($inventory.LaneCount) projects=$($inventory.ProjectCount) tests=$($inventory.ExpectedTestCount) coverage_reports=$($coverage.ReportCount) coverage_packages=$($coverage.PackageCount)"
}

if (-not $NoRun) {
    Assert-FanInCondition -Condition (-not [string]::IsNullOrWhiteSpace($SolutionArtifactRoot) -and -not [string]::IsNullOrWhiteSpace($NestedArtifactRoot) -and -not [string]::IsNullOrWhiteSpace($StaticArtifactRoot) -and -not [string]::IsNullOrWhiteSpace($ExpectedHead) -and -not [string]::IsNullOrWhiteSpace($ExpectedRunId) -and -not [string]::IsNullOrWhiteSpace($ExpectedRunAttempt)) -Message "Promotion fan-in requires all three artifact roots and exact run identity."
    Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $SolutionArtifactRoot -NestedArtifactRoot $NestedArtifactRoot -StaticArtifactRoot $StaticArtifactRoot -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt -SolutionResult $SolutionResult -NestedResult $NestedResult -StaticResult $StaticResult
}
