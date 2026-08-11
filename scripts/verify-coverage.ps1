param(
    [DateTime]$MinimumWriteTimeUtc = [DateTime]::MinValue
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$threshold = 0.90
$repoRoot = Split-Path -Parent $PSScriptRoot
$testsPath = Join-Path $repoRoot "tests"

$coverageFiles = @()
$testProjectDirectories = Get-ChildItem -Path $testsPath -Directory | Where-Object {
    Test-Path (Join-Path $_.FullName ($_.Name + ".csproj"))
}

foreach ($testProjectDirectory in $testProjectDirectories) {
    $testResultsPath = Join-Path $testProjectDirectory.FullName "TestResults"
    if (-not (Test-Path $testResultsPath)) {
        continue
    }

    $projectCoverageFiles = @(Get-ChildItem -Path $testResultsPath -Recurse -Filter "coverage.cobertura.xml" |
        Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc } |
        Sort-Object FullName)

    if ($projectCoverageFiles.Count -gt 0) {
        $coverageFiles += $projectCoverageFiles
    }
}

if ($coverageFiles.Count -eq 0) {
    throw "Coverage output was not found under split test project TestResults folders. Run dotnet test --collect:`"XPlat Code Coverage`" /p:RestoreIgnoreFailedSources=true first."
}

$failures = @()
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

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}
