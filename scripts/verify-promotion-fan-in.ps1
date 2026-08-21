param(
    [string]$SolutionArtifactRoot = "",

    [string]$StaticArtifactRoot = "",

    [string]$ExpectedHead = "",

    [string]$ExpectedRunId = "",

    [string]$ExpectedRunAttempt = "",

    [ValidateSet("success", "failure", "cancelled", "skipped")]
    [string]$SolutionResult = "success",

    [ValidateSet("success", "failure", "cancelled", "skipped")]
    [string]$StaticResult = "success",

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
    $expectedDeadlineSeconds = if ($Component -ceq "solution") { 900 } elseif ($Component -ceq "static-contracts") { 600 } else { throw "Unsupported fan-in component: $Component" }
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

function Assert-FanInSolutionEvidence {
    param([string]$ResultsRoot, [object]$Evidence)

    $laneDefinitions = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "required-test-lanes.json") -Description "Required-test lane definitions"
    $lanes = @($laneDefinitions.lanes)
    Assert-FanInCondition -Condition ($laneDefinitions.schemaVersion -eq 1 -and $lanes.Count -eq 9 -and @($lanes | Group-Object name | Where-Object Count -ne 1).Count -eq 0) -Message "Solution evidence must contain nine unique required-test lanes."
    Assert-FanInCondition -Condition ([int]$Evidence.laneCount -eq 9) -Message "Solution component evidence lane count is not nine."
    $manifest = Read-FanInJsonFile -Path (Join-Path $ResultsRoot "verification-component-manifest.json") -Description "Solution component manifest"
    $manifestPaths = @($manifest.files | ForEach-Object { [string]$_.path })
    foreach ($requiredPath in @("required-test-lanes.json", "required-test-partition.json", "required-execution-tests.json", "required-test-report.json", "coverage-manifest.json", "coverage-summary.json")) {
        Assert-FanInCondition -Condition ($manifestPaths -contains $requiredPath) -Message "Solution component manifest omitted required control evidence: $requiredPath"
    }
    $solutionTrxPaths = @($manifestPaths | Where-Object { $_ -match '\.trx$' })
    Assert-FanInCondition -Condition ($solutionTrxPaths.Count -eq 9 -and $manifestPaths.Count -eq 15) -Message "Solution component manifest must contain exactly six control reports and nine TRX reports."

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
    else {
        Assert-FanInStaticEvidence -ResultsRoot $resultsRoot -Evidence $evidence
    }
}

function Invoke-VerificationPromotionFanIn {
    param(
        [string]$SolutionArtifactRoot,
        [string]$StaticArtifactRoot,
        [string]$ExpectedHead,
        [string]$ExpectedRunId,
        [string]$ExpectedRunAttempt,
        [string]$SolutionResult,
        [string]$StaticResult
    )

    Assert-FanInCondition -Condition ($SolutionResult -ceq "success" -and $StaticResult -ceq "success") -Message "Both hosted verification children must succeed before fan-in."
    Read-FanInComponent -ArtifactRoot $SolutionArtifactRoot -Component "solution" -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt
    Read-FanInComponent -ArtifactRoot $StaticArtifactRoot -Component "static-contracts" -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt
    Write-Output "VERIFY_PROMOTION_FAN_IN schema_version=1 status=passed solution=solution static=static-contracts"
}

if (-not $NoRun) {
    Assert-FanInCondition -Condition (-not [string]::IsNullOrWhiteSpace($SolutionArtifactRoot) -and -not [string]::IsNullOrWhiteSpace($StaticArtifactRoot) -and -not [string]::IsNullOrWhiteSpace($ExpectedHead) -and -not [string]::IsNullOrWhiteSpace($ExpectedRunId) -and -not [string]::IsNullOrWhiteSpace($ExpectedRunAttempt)) -Message "Promotion fan-in requires both artifact roots and exact run identity."
    Invoke-VerificationPromotionFanIn -SolutionArtifactRoot $SolutionArtifactRoot -StaticArtifactRoot $StaticArtifactRoot -ExpectedHead $ExpectedHead -ExpectedRunId $ExpectedRunId -ExpectedRunAttempt $ExpectedRunAttempt -SolutionResult $SolutionResult -StaticResult $StaticResult
}
