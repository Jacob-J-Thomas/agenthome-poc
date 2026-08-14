param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EvidenceRoot,

    [ValidateRange(1, 8)]
    [int]$MaximumTestWorkers = [Math]::Min(8, [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 1.5)))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$verificationResultsRoot = Join-Path $repoRoot "tests/VerificationResults"
$fullEvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$relativeEvidenceRoot = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($repoRoot), $fullEvidenceRoot)
if (-not [IO.Path]::IsPathRooted($relativeEvidenceRoot) -and
    $relativeEvidenceRoot -cne ".." -and
    -not $relativeEvidenceRoot.StartsWith("../", [StringComparison]::Ordinal) -and
    -not $relativeEvidenceRoot.StartsWith("..\", [StringComparison]::Ordinal)) {
    throw "Coverage ownership equivalence evidence root must be outside the repository."
}
if (Test-Path -LiteralPath $fullEvidenceRoot) {
    throw "Coverage ownership equivalence evidence root already exists: $fullEvidenceRoot"
}

$parent = Split-Path -Parent $fullEvidenceRoot
if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw "Coverage ownership equivalence evidence parent must already exist: $parent"
}
New-Item -ItemType Directory -Path $fullEvidenceRoot | Out-Null

foreach ($mode in @("UnfilteredEvidence", "FilteredEvidence")) {
    Write-Output "VERIFY_COVERAGE_OWNERSHIP_COLLECTION_START mode=$mode deadline_seconds=600"
    & (Join-Path $PSScriptRoot "verify-with-watchdog.ps1") -Configuration Release -MaximumTestWorkers $MaximumTestWorkers -DeadlineSeconds 600 -CoverageOwnershipMode $mode
    if (-not (Test-Path -LiteralPath $verificationResultsRoot -PathType Container)) {
        throw "Coverage ownership $mode verifier did not produce its canonical results root."
    }
    $destination = Join-Path $fullEvidenceRoot $mode
    Move-Item -LiteralPath $verificationResultsRoot -Destination $destination
    Write-Output "VERIFY_COVERAGE_OWNERSHIP_COLLECTION_COMPLETE mode=$mode results=$destination"
}

$reportPath = Join-Path $fullEvidenceRoot "equivalence-report.json"
& (Join-Path $PSScriptRoot "verify-coverage-ownership-equivalence.ps1") `
    -UnfilteredResultsRoot (Join-Path $fullEvidenceRoot "UnfilteredEvidence") `
    -FilteredResultsRoot (Join-Path $fullEvidenceRoot "FilteredEvidence") `
    -ReportPath $reportPath
Write-Output "VERIFY_COVERAGE_OWNERSHIP_COLLECTION_SUITE_COMPLETE status=passed evidence_root=$fullEvidenceRoot report=$reportPath"
