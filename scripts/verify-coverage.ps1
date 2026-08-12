param(
    [DateTime]$MinimumWriteTimeUtc = [DateTime]::MinValue,
    [string]$ResultsRoot,
    [string]$ManifestPath,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$threshold = 0.90
$repoRoot = Split-Path -Parent $PSScriptRoot
$testsPath = Join-Path $repoRoot "tests"

$coverageFiles = @()
if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    $fullManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    if (-not (Test-Path -LiteralPath $fullManifestPath -PathType Leaf)) {
        throw "Coverage report manifest is missing: $fullManifestPath"
    }
    try {
        $manifest = Get-Content -LiteralPath $fullManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Coverage report manifest is corrupt: $fullManifestPath. $($_.Exception.Message)"
    }
    $fullResultsRoot = [IO.Path]::GetFullPath($ResultsRoot)
    if ($manifest.schemaVersion -ne 1 -or -not ([IO.Path]::GetFullPath([string]$manifest.resultsRoot)).Equals($fullResultsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Coverage report manifest does not bind the requested schema-1 results root: $fullResultsRoot"
    }
    $manifestReports = @($manifest.reports)
    if ($manifestReports.Count -eq 0) {
        throw "Coverage report manifest contains no reports: $fullManifestPath"
    }
    $aliasProperty = $manifest.PSObject.Properties["aliases"]
    if ($null -eq $aliasProperty) {
        throw "Coverage report manifest does not declare its schema-1 staging alias inventory: $fullManifestPath"
    }
    $manifestAliases = @($aliasProperty.Value)
    $manifestPaths = @()
    $reportEvidenceByPath = @{}
    foreach ($entry in $manifestReports) {
        $path = [IO.Path]::GetFullPath([string]$entry.path)
        if (-not $path.StartsWith($fullResultsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Coverage report manifest references a missing or out-of-root report: $path"
        }
        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($file.Length -ne [long]$entry.length -or $hash -cne [string]$entry.sha256 -or $file.LastWriteTimeUtc -lt $MinimumWriteTimeUtc) {
            throw "Coverage report manifest evidence does not match the fresh report: $path"
        }
        $manifestPaths += $path
        $coverageFiles += $file
        if ($reportEvidenceByPath.ContainsKey($path)) {
            throw "Coverage report manifest contains duplicate report paths."
        }
        $reportEvidenceByPath[$path] = $entry
    }

    $aliasPaths = @()
    $aliasPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifestAliases) {
        $pathText = [string]$entry.path
        $canonicalPathText = [string]$entry.canonicalPath
        if ([string]::IsNullOrWhiteSpace($pathText) -or [string]::IsNullOrWhiteSpace($canonicalPathText)) {
            throw "Coverage report manifest contains an incomplete staging alias entry."
        }

        $path = [IO.Path]::GetFullPath($pathText)
        $canonicalPath = [IO.Path]::GetFullPath($canonicalPathText)
        if (-not $path.StartsWith($fullResultsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Coverage report manifest references a missing or out-of-root staging alias: $path"
        }
        if (-not $reportEvidenceByPath.ContainsKey($canonicalPath)) {
            throw "Coverage report manifest staging alias does not reference one canonical report: $canonicalPath"
        }
        if ($reportEvidenceByPath.ContainsKey($path) -or -not $aliasPathSet.Add($path)) {
            throw "Coverage report manifest contains duplicate or overlapping staging alias paths."
        }

        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $canonicalEntry = $reportEvidenceByPath[$canonicalPath]
        if ($file.Length -ne [long]$entry.length -or $hash -cne [string]$entry.sha256 -or $file.LastWriteTimeUtc -lt $MinimumWriteTimeUtc) {
            throw "Coverage report manifest evidence does not match the fresh staging alias: $path"
        }
        if ([long]$entry.length -ne [long]$canonicalEntry.length -or [string]$entry.sha256 -cne [string]$canonicalEntry.sha256) {
            throw "Coverage report manifest staging alias does not byte-match its canonical report: $path"
        }
        $aliasPaths += $path
    }

    $actualPaths = @(Get-ChildItem -LiteralPath $fullResultsRoot -Recurse -Filter "coverage.cobertura.xml" -File | ForEach-Object { $_.FullName } | Sort-Object -Unique)
    $expectedPaths = @((@($manifestPaths) + @($aliasPaths)) | Sort-Object)
    if (@(Compare-Object -ReferenceObject $expectedPaths -DifferenceObject $actualPaths -CaseSensitive).Count -ne 0) {
        throw "Coverage results contain missing, stale, or unexpected reports outside the exact manifest."
    }
}
else {
    $testProjectDirectories = Get-ChildItem -Path $testsPath -Directory | Where-Object { Test-Path (Join-Path $_.FullName ($_.Name + ".csproj")) }
    foreach ($testProjectDirectory in $testProjectDirectories) {
        $testResultsPath = Join-Path $testProjectDirectory.FullName "TestResults"
        if (Test-Path $testResultsPath) {
            $coverageFiles += @(Get-ChildItem -Path $testResultsPath -Recurse -Filter "coverage.cobertura.xml" | Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc } | Sort-Object FullName)
        }
    }
}

if ($coverageFiles.Count -eq 0) {
    throw "Coverage output was not found in the exact current-run report inventory. Run the canonical verifier first."
}

$failures = @()
$packageSummaries = [Collections.Generic.List[object]]::new()
$packageFileLines = @{}
$sourceProjectDirectories = @{}
Get-ChildItem -Path (Join-Path $repoRoot "src") -Directory -Recurse | Where-Object {
    Test-Path (Join-Path $_.FullName ($_.Name + ".csproj"))
} | ForEach-Object {
    $sourceProjectDirectories[$_.Name] = $_.FullName
}
$expectedPackages = @($sourceProjectDirectories.Keys | Sort-Object)

function Get-CoverageFileKey {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageName,

        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    $directorySeparator = [IO.Path]::DirectorySeparatorChar
    $normalizedFileName = $FileName.Replace("/", $directorySeparator).Replace("\", $directorySeparator)
    $sourceSegment = "src$directorySeparator"
    $sourceIndex = $normalizedFileName.IndexOf($sourceSegment, [StringComparison]::OrdinalIgnoreCase)

    if ([IO.Path]::IsPathRooted($normalizedFileName)) {
        $candidatePath = $normalizedFileName
    }
    elseif ($normalizedFileName.StartsWith("src$directorySeparator", [StringComparison]::OrdinalIgnoreCase)) {
        $candidatePath = Join-Path $repoRoot $normalizedFileName
    }
    elseif ($sourceIndex -ge 0) {
        $candidatePath = Join-Path $repoRoot $normalizedFileName.Substring($sourceIndex)
    }
    elseif ($normalizedFileName -match "^\d{2}-") {
        $candidatePath = Join-Path (Join-Path $repoRoot "src") $normalizedFileName
    }
    elseif ($normalizedFileName.StartsWith("$PackageName$directorySeparator", [StringComparison]::OrdinalIgnoreCase) -and $sourceProjectDirectories.ContainsKey($PackageName)) {
        $relativeProjectFileName = $normalizedFileName.Substring($PackageName.Length + 1)
        $candidatePath = Join-Path $sourceProjectDirectories[$PackageName] $relativeProjectFileName
    }
    else {
        $candidatePath = Join-Path $repoRoot $normalizedFileName
    }

    return [IO.Path]::GetFullPath($candidatePath).ToUpperInvariant()
}

foreach ($coverageFile in $coverageFiles) {
    $coverage = [xml](Get-Content -Raw -LiteralPath $coverageFile.FullName)

    foreach ($package in $coverage.coverage.packages.package) {
        $packageName = [string]$package.name

        if (-not $expectedPackages.Contains($packageName)) {
            continue
        }

        if (-not $packageFileLines.ContainsKey($packageName)) {
            $packageFileLines[$packageName] = @{}
        }

        $packageFiles = $packageFileLines[$packageName]
        foreach ($class in $package.classes.class) {
            $classLines = $class.SelectNodes("lines/line")
            if ($classLines.Count -eq 0) {
                continue
            }

            $fileKey = Get-CoverageFileKey -PackageName $packageName -FileName $class.filename
            if (-not $packageFiles.ContainsKey($fileKey)) {
                $packageFiles[$fileKey] = @{}
            }

            $fileLines = $packageFiles[$fileKey]
            foreach ($line in $classLines) {
                $lineNumber = [int]$line.number
                $hits = [int]$line.hits
                if (-not $fileLines.ContainsKey($lineNumber) -or $hits -gt $fileLines[$lineNumber]) {
                    $fileLines[$lineNumber] = $hits
                }
            }
        }
    }
}

foreach ($expectedPackage in $expectedPackages) {
    if (-not $packageFileLines.ContainsKey($expectedPackage)) {
        $failures += "Coverage output did not include expected production package {0}" -f $expectedPackage
        continue
    }

    $packageFiles = $packageFileLines[$expectedPackage]
    $lineCount = 0
    $coveredLineCount = 0
    foreach ($fileLines in $packageFiles.Values) {
        $lineCount += $fileLines.Count
        foreach ($hits in $fileLines.Values) {
            if ($hits -gt 0) {
                $coveredLineCount++
            }
        }
    }

    if ($lineCount -eq 0) {
        $failures += "Coverage output for {0} did not include executable lines" -f $expectedPackage
        continue
    }

    $lineRate = $coveredLineCount / $lineCount
    $percent = [math]::Round($lineRate * 100, 2)
    Write-Output ("{0}: {1}%" -f $expectedPackage, $percent)
    $packageSummaries.Add([pscustomobject][ordered]@{
        package = $expectedPackage
        coveredLines = $coveredLineCount
        totalLines = $lineCount
        lineRate = [math]::Round($lineRate, 8)
        percent = $percent
    })

    if ($lineRate -lt $threshold) {
        $normalizedRepoRoot = [IO.Path]::GetFullPath($repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar).ToUpperInvariant()
        $packageFiles.GetEnumerator() |
            ForEach-Object {
                $fileLineCount = $_.Value.Count
                $fileCoveredLineCount = @($_.Value.Values | Where-Object { $_ -gt 0 }).Count
                $uncoveredLineNumbers = @($_.Value.GetEnumerator() | Where-Object { $_.Value -le 0 } | ForEach-Object { $_.Key } | Sort-Object)
                $displayPath = if ($_.Key.StartsWith($normalizedRepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    $_.Key.Substring($normalizedRepoRoot.Length + 1)
                }
                else {
                    $_.Key
                }

                [pscustomobject]@{
                    File = $displayPath
                    Uncovered = $fileLineCount - $fileCoveredLineCount
                    Total = $fileLineCount
                    UncoveredLines = $uncoveredLineNumbers
                }
            } |
            Where-Object { $_.Uncovered -gt 0 } |
            Sort-Object -Property @{ Expression = "Uncovered"; Descending = $true }, @{ Expression = "Total"; Descending = $true }, @{ Expression = "File"; Descending = $false } |
            Select-Object -First 25 |
            ForEach-Object {
                Write-Output ("COVERAGE_GAP package={0} uncovered={1} total={2} file={3} lines={4}" -f $expectedPackage, $_.Uncovered, $_.Total, $_.File, ($_.UncoveredLines -join ","))
            }

        $failures += "{0} line coverage {1}% is below {2}%" -f $expectedPackage, $percent, ($threshold * 100)
    }
}

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $fullReportPath = [IO.Path]::GetFullPath($ReportPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $fullReportPath) -Force | Out-Null
    $report = [ordered]@{
        schemaVersion = 1
        threshold = $threshold
        reports = @($coverageFiles | Sort-Object FullName | ForEach-Object { $_.FullName })
        packages = @($packageSummaries | Sort-Object -Property package)
        failures = @($failures)
    }
    [IO.File]::WriteAllText($fullReportPath, ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output "VERIFY_COVERAGE_REPORT reports=$($coverageFiles.Count) packages=$($packageSummaries.Count) path=$fullReportPath"
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}
