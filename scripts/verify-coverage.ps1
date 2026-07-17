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

    $coverageFile = Get-ChildItem -Path $testResultsPath -Recurse -Filter "coverage.cobertura.xml" |
        Where-Object { $_.LastWriteTimeUtc -ge $MinimumWriteTimeUtc } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -ne $coverageFile) {
        $coverageFiles += $coverageFile
    }
}

if ($coverageFiles.Count -eq 0) {
    throw "Coverage output was not found under split test project TestResults folders. Run dotnet test --collect:`"XPlat Code Coverage`" /p:RestoreIgnoreFailedSources=true first."
}

$failures = @()
$packageLines = @{}
$sourceProjectDirectories = @{}
Get-ChildItem -Path (Join-Path $repoRoot "src") -Directory -Recurse | Where-Object {
    Test-Path (Join-Path $_.FullName ($_.Name + ".csproj"))
} | ForEach-Object {
    $sourceProjectDirectories[$_.Name] = $_.FullName
}
$expectedPackages = @($sourceProjectDirectories.Keys | Sort-Object)

function Get-CoverageLineKey {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageName,

        [Parameter(Mandatory = $true)]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [string]$LineNumber
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

    return "{0}:{1}" -f [IO.Path]::GetFullPath($candidatePath).ToUpperInvariant(), $LineNumber
}

foreach ($coverageFile in $coverageFiles) {
    $coverage = [xml](Get-Content $coverageFile.FullName)

    foreach ($package in $coverage.coverage.packages.package) {
        $packageName = [string]$package.name

        if (-not $expectedPackages.Contains($packageName)) {
            continue
        }

        if (-not $packageLines.ContainsKey($packageName)) {
            $packageLines[$packageName] = @{}
        }

        foreach ($class in $package.classes.class) {
            foreach ($line in $class.lines.line) {
                $lineKey = Get-CoverageLineKey -PackageName $packageName -FileName $class.filename -LineNumber $line.number
                $hits = [int]$line.hits

                if (-not $packageLines[$packageName].ContainsKey($lineKey) -or $hits -gt $packageLines[$packageName][$lineKey]) {
                    $packageLines[$packageName][$lineKey] = $hits
                }
            }
        }
    }
}

foreach ($expectedPackage in $expectedPackages) {
    if (-not $packageLines.ContainsKey($expectedPackage)) {
        $failures += "Coverage output did not include expected production package {0}" -f $expectedPackage
        continue
    }

    $lines = $packageLines[$expectedPackage]
    $lineCount = $lines.Count

    if ($lineCount -eq 0) {
        $failures += "Coverage output for {0} did not include executable lines" -f $expectedPackage
        continue
    }

    $coveredLineCount = @($lines.Values | Where-Object { $_ -gt 0 }).Count
    $lineRate = $coveredLineCount / $lineCount
    $percent = [math]::Round($lineRate * 100, 2)
    Write-Output ("{0}: {1}%" -f $expectedPackage, $percent)

    if ($lineRate -lt $threshold) {
        $failures += "{0} line coverage {1}% is below {2}%" -f $expectedPackage, $percent, ($threshold * 100)
    }
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}
