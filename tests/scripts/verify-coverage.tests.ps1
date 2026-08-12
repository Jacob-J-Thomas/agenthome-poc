Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$coverageScriptPath = Join-Path $repoRoot "scripts\verify-coverage.ps1"
$powerShellExecutable = (Get-Process -Id $PID).Path
$assertionCount = 0

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }

    $script:assertionCount++
}

function Assert-Contains {
    param(
        [string]$Actual,
        [string]$Expected,
        [string]$Message
    )

    Assert-True -Condition ($Actual.IndexOf($Expected, [StringComparison]::Ordinal) -ge 0) -Message "$Message Expected '$Expected'. Actual: $Actual"
}

function Assert-NotContains {
    param(
        [string]$Actual,
        [string]$Unexpected,
        [string]$Message
    )

    Assert-True -Condition ($Actual.IndexOf($Unexpected, [StringComparison]::Ordinal) -lt 0) -Message "$Message Unexpected '$Unexpected'. Actual: $Actual"
}

function New-CoverageLines {
    param([int[]]$Hits)

    for ($index = 0; $index -lt $Hits.Count; $index++) {
        [pscustomobject]@{
            Number = $index + 1
            Hits = $Hits[$index]
        }
    }
}

function New-CoverageLine {
    param(
        [int]$Number,
        [int]$Hits
    )

    return [pscustomobject]@{
        Number = $Number
        Hits = $Hits
    }
}

function New-CoverageClass {
    param(
        [string]$Name,
        [string]$FileName,
        [object[]]$Lines
    )

    return [pscustomobject]@{
        Name = $Name
        FileName = $FileName
        Lines = @($Lines)
    }
}

function New-CoveragePackage {
    param(
        [string]$Name,
        [object[]]$Classes
    )

    return [pscustomobject]@{
        Name = $Name
        Classes = @($Classes)
    }
}

function New-FixtureRepository {
    param(
        [string]$ScenarioRoot,
        [string]$Name
    )

    $fixtureRoot = Join-Path $ScenarioRoot $Name
    $scriptsPath = Join-Path $fixtureRoot "scripts"
    $testsPath = Join-Path $fixtureRoot "tests\Fixture.Tests"
    New-Item -ItemType Directory -Path $scriptsPath, $testsPath -Force | Out-Null
    Copy-Item -LiteralPath $coverageScriptPath -Destination (Join-Path $scriptsPath "verify-coverage.ps1")
    Set-Content -LiteralPath (Join-Path $testsPath "Fixture.Tests.csproj") -Value "<Project />" -Encoding UTF8

    foreach ($packageName in @("Fixture.One", "Fixture.Two")) {
        $projectPath = Join-Path $fixtureRoot "src\$packageName"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "$packageName.csproj") -Value "<Project />" -Encoding UTF8
    }

    return $fixtureRoot
}

function Write-CoverageReport {
    param(
        [string]$RepositoryRoot,
        [string]$Name,
        [object[]]$Packages,
        [DateTime]$LastWriteTimeUtc
    )

    $reportDirectory = Join-Path $RepositoryRoot "tests\Fixture.Tests\TestResults\$Name"
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $reportPath = Join-Path $reportDirectory "coverage.cobertura.xml"
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($reportPath, $settings)

    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement("coverage")
        $writer.WriteStartElement("packages")
        foreach ($package in $Packages) {
            $writer.WriteStartElement("package")
            $writer.WriteAttributeString("name", [string]$package.Name)
            $writer.WriteStartElement("classes")
            foreach ($class in $package.Classes) {
                $writer.WriteStartElement("class")
                $writer.WriteAttributeString("name", [string]$class.Name)
                $writer.WriteAttributeString("filename", [string]$class.FileName)
                $writer.WriteStartElement("lines")
                foreach ($line in $class.Lines) {
                    $writer.WriteStartElement("line")
                    $writer.WriteAttributeString("number", [string]$line.Number)
                    $writer.WriteAttributeString("hits", [string]$line.Hits)
                    $writer.WriteEndElement()
                }

                $writer.WriteEndElement()
                $writer.WriteEndElement()
            }

            $writer.WriteEndElement()
            $writer.WriteEndElement()
        }

        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }

    [IO.File]::SetLastWriteTimeUtc($reportPath, $LastWriteTimeUtc)
}

function Invoke-CoverageVerification {
    param(
        [string]$RepositoryRoot,
        [DateTime]$MinimumWriteTimeUtc,
        [string]$ResultsRoot,
        [string]$ManifestPath,
        [string]$ReportPath
    )

    $arguments = @("-NoLogo", "-NoProfile")
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
        $arguments += @("-ExecutionPolicy", "Bypass")
    }

    $arguments += @("-File", (Join-Path $RepositoryRoot "scripts\verify-coverage.ps1"), "-MinimumWriteTimeUtc", $MinimumWriteTimeUtc.ToString("O"))
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        $arguments += @("-ResultsRoot", $ResultsRoot, "-ManifestPath", $ManifestPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $arguments += @("-ReportPath", $ReportPath)
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $powerShellExecutable
    $startInfo.WorkingDirectory = $RepositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Coverage verifier child process did not start."
        }

        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Coverage verifier child process exceeded its 30-second contract-test bound."
        }

        $output = $standardOutput.GetAwaiter().GetResult() + [Environment]::NewLine + $standardError.GetAwaiter().GetResult()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = $output
        }
    }
    finally {
        $process.Dispose()
    }
}

function Write-CoverageManifest {
    param([string]$ResultsRoot, [string]$ManifestPath)

    $fullRoot = [IO.Path]::GetFullPath($ResultsRoot)
    $files = @(Get-ChildItem -LiteralPath $fullRoot -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)
    $manifest = [ordered]@{
        schemaVersion = 1
        resultsRoot = $fullRoot
        reports = @($files | ForEach-Object { [ordered]@{ path = $_.FullName; length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    }
    [IO.File]::WriteAllText($ManifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}

$scenarioRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-coverage-verifier-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
try {
    $minimumWriteTimeUtc = [DateTime]::UtcNow
    $freshWriteTimeUtc = $minimumWriteTimeUtc.AddDays(2)
    $staleWriteTimeUtc = $minimumWriteTimeUtc.AddDays(-2)

    $passingRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "passing"
    $onePrimaryClass = New-CoverageClass -Name "Fixture.One.Primary" -FileName "src/Fixture.One/File.cs" -Lines @(New-CoverageLines -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 0, 0))
    $twoPrimaryClass = New-CoverageClass -Name "Fixture.Two.Primary" -FileName "src/Fixture.Two/File.cs" -Lines @(New-CoverageLines -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 1, 0))
    $primaryPackages = @(
        (New-CoveragePackage -Name "Fixture.One" -Classes @($onePrimaryClass)),
        (New-CoveragePackage -Name "Fixture.Two" -Classes @($twoPrimaryClass))
    )
    Write-CoverageReport -RepositoryRoot $passingRepository -Name "primary" -Packages $primaryPackages -LastWriteTimeUtc $freshWriteTimeUtc

    $oneAliasClass = New-CoverageClass -Name "Fixture.One.Alias" -FileName (Join-Path $passingRepository "src\Fixture.One\File.cs") -Lines @(
        (New-CoverageLine -Number 1 -Hits 0),
        (New-CoverageLine -Number 9 -Hits 7)
    )
    $aliasPackage = New-CoveragePackage -Name "Fixture.One" -Classes @($oneAliasClass)
    Write-CoverageReport -RepositoryRoot $passingRepository -Name "alias" -Packages @($aliasPackage) -LastWriteTimeUtc $freshWriteTimeUtc

    $oneStaleClass = New-CoverageClass -Name "Fixture.One.Stale" -FileName "src/Fixture.One/File.cs" -Lines @(
        (New-CoverageLine -Number 10 -Hits 1)
    )
    $stalePackage = New-CoveragePackage -Name "Fixture.One" -Classes @($oneStaleClass)
    Write-CoverageReport -RepositoryRoot $passingRepository -Name "stale" -Packages @($stalePackage) -LastWriteTimeUtc $staleWriteTimeUtc

    $passingResult = Invoke-CoverageVerification -RepositoryRoot $passingRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc
    Assert-True -Condition ($passingResult.ExitCode -eq 0) -Message "Fresh complete coverage fixtures should pass. Actual: $($passingResult.Output)"
    Assert-Contains -Actual $passingResult.Output -Expected "Fixture.One: 90%" -Message "Path aliases and duplicate lines must merge by maximum hits."
    Assert-Contains -Actual $passingResult.Output -Expected "Fixture.Two: 90%" -Message "Every expected package must be evaluated."
    Assert-NotContains -Actual $passingResult.Output -Unexpected "Fixture.One: 100%" -Message "Reports older than the supplied minimum write time must be ignored."

    $manifestRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-passing"
    Write-CoverageReport -RepositoryRoot $manifestRepository -Name "primary" -Packages $primaryPackages -LastWriteTimeUtc $freshWriteTimeUtc
    $manifestAliasClass = New-CoverageClass -Name "Fixture.One.Alias" -FileName (Join-Path $manifestRepository "src\Fixture.One\File.cs") -Lines @((New-CoverageLine -Number 1 -Hits 0), (New-CoverageLine -Number 9 -Hits 7))
    Write-CoverageReport -RepositoryRoot $manifestRepository -Name "alias" -Packages @((New-CoveragePackage -Name "Fixture.One" -Classes @($manifestAliasClass))) -LastWriteTimeUtc $freshWriteTimeUtc
    $manifestResultsRoot = Join-Path $manifestRepository "tests\Fixture.Tests\TestResults"
    $manifestPath = Join-Path $manifestRepository "coverage-manifest.json"
    $summaryPath = Join-Path $manifestRepository "coverage-summary.json"
    Write-CoverageManifest -ResultsRoot $manifestResultsRoot -ManifestPath $manifestPath
    $manifestPassing = Invoke-CoverageVerification -RepositoryRoot $manifestRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $manifestResultsRoot -ManifestPath $manifestPath -ReportPath $summaryPath
    Assert-True -Condition ($manifestPassing.ExitCode -eq 0) -Message "An exact fresh coverage manifest must pass. Actual: $($manifestPassing.Output)"
    Assert-Contains -Actual $manifestPassing.Output -Expected "VERIFY_COVERAGE_REPORT reports=2 packages=2" -Message "Manifest-backed coverage must retain exact counts."
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    Assert-True -Condition ($summary.threshold -eq 0.90 -and @($summary.packages).Count -eq 2) -Message "The deterministic summary must retain the unchanged threshold and every package."

    $unexpectedDirectory = Join-Path $manifestResultsRoot "unexpected"
    New-Item -ItemType Directory -Path $unexpectedDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $manifestResultsRoot "primary\coverage.cobertura.xml") -Destination (Join-Path $unexpectedDirectory "coverage.cobertura.xml")
    $unexpectedManifestResult = Invoke-CoverageVerification -RepositoryRoot $manifestRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $manifestResultsRoot -ManifestPath $manifestPath
    Assert-True -Condition ($unexpectedManifestResult.ExitCode -ne 0) -Message "Coverage outside the exact manifest must fail closed."
    Assert-Contains -Actual $unexpectedManifestResult.Output -Expected "missing, stale, or unexpected reports" -Message "Unexpected-report diagnostics must be actionable."

    $duplicateManifestRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "duplicate-manifest"
    Write-CoverageReport -RepositoryRoot $duplicateManifestRepository -Name "primary" -Packages $primaryPackages -LastWriteTimeUtc $freshWriteTimeUtc
    $duplicateResultsRoot = Join-Path $duplicateManifestRepository "tests\Fixture.Tests\TestResults"
    $duplicateManifestPath = Join-Path $duplicateManifestRepository "coverage-manifest.json"
    Write-CoverageManifest -ResultsRoot $duplicateResultsRoot -ManifestPath $duplicateManifestPath
    $duplicateManifest = Get-Content -LiteralPath $duplicateManifestPath -Raw | ConvertFrom-Json
    $duplicateManifest.reports = @($duplicateManifest.reports[0], $duplicateManifest.reports[0])
    [IO.File]::WriteAllText($duplicateManifestPath, ($duplicateManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $duplicateManifestResult = Invoke-CoverageVerification -RepositoryRoot $duplicateManifestRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $duplicateResultsRoot -ManifestPath $duplicateManifestPath
    Assert-True -Condition ($duplicateManifestResult.ExitCode -ne 0) -Message "Duplicate report paths in a manifest must fail closed."
    Assert-Contains -Actual $duplicateManifestResult.Output -Expected "duplicate report paths" -Message "Duplicate-manifest diagnostics must be actionable."

    $corruptRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "corrupt-report"
    $corruptResultsRoot = Join-Path $corruptRepository "tests\Fixture.Tests\TestResults\corrupt"
    New-Item -ItemType Directory -Path $corruptResultsRoot -Force | Out-Null
    $corruptReportPath = Join-Path $corruptResultsRoot "coverage.cobertura.xml"
    Set-Content -LiteralPath $corruptReportPath -Value "<coverage" -Encoding UTF8
    [IO.File]::SetLastWriteTimeUtc($corruptReportPath, $freshWriteTimeUtc)
    $corruptManifestPath = Join-Path $corruptRepository "coverage-manifest.json"
    Write-CoverageManifest -ResultsRoot (Split-Path -Parent $corruptResultsRoot) -ManifestPath $corruptManifestPath
    $corruptResult = Invoke-CoverageVerification -RepositoryRoot $corruptRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot (Split-Path -Parent $corruptResultsRoot) -ManifestPath $corruptManifestPath
    Assert-True -Condition ($corruptResult.ExitCode -ne 0) -Message "Corrupt coverage XML must fail closed."

    $failingRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "below-threshold"
    $oneFailingClass = New-CoverageClass -Name "Fixture.One.Failing" -FileName "src/Fixture.One/File.cs" -Lines @(New-CoverageLines -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 0, 0))
    $twoPassingClass = New-CoverageClass -Name "Fixture.Two.Passing" -FileName "src/Fixture.Two/File.cs" -Lines @(New-CoverageLines -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 1, 0))
    $failingPackages = @(
        (New-CoveragePackage -Name "Fixture.One" -Classes @($oneFailingClass)),
        (New-CoveragePackage -Name "Fixture.Two" -Classes @($twoPassingClass))
    )
    Write-CoverageReport -RepositoryRoot $failingRepository -Name "below" -Packages $failingPackages -LastWriteTimeUtc $freshWriteTimeUtc

    $failingResult = Invoke-CoverageVerification -RepositoryRoot $failingRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc
    Assert-True -Condition ($failingResult.ExitCode -ne 0) -Message "Below-threshold coverage must fail."
    $separator = [IO.Path]::DirectorySeparatorChar
    $expectedGap = "COVERAGE_GAP package=Fixture.One uncovered=2 total=10 file=SRC${separator}FIXTURE.ONE${separator}FILE.CS lines=9,10"
    Assert-Contains -Actual $failingResult.Output -Expected $expectedGap -Message "Coverage gaps must remain stable and actionable."
    Assert-Contains -Actual $failingResult.Output -Expected "Fixture.One line coverage 80% is below 90%" -Message "Below-threshold diagnostics must preserve the enforced threshold."

    $missingRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "missing-package"
    $onePassingClass = New-CoverageClass -Name "Fixture.One.Passing" -FileName "src/Fixture.One/File.cs" -Lines @(New-CoverageLines -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 1, 0))
    $onePassingPackage = New-CoveragePackage -Name "Fixture.One" -Classes @($onePassingClass)
    Write-CoverageReport -RepositoryRoot $missingRepository -Name "missing" -Packages @($onePassingPackage) -LastWriteTimeUtc $freshWriteTimeUtc

    $missingResult = Invoke-CoverageVerification -RepositoryRoot $missingRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc
    Assert-True -Condition ($missingResult.ExitCode -ne 0) -Message "Missing production packages must fail."
    Assert-Contains -Actual $missingResult.Output -Expected "Coverage output did not include expected production package Fixture.Two" -Message "Missing-package diagnostics must be preserved."

    $emptyRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "empty-package"
    $emptyClass = New-CoverageClass -Name "Fixture.Two.Empty" -FileName "src/Fixture.Two/Empty.cs" -Lines @()
    $emptyPackages = @(
        (New-CoveragePackage -Name "Fixture.One" -Classes @($onePassingClass)),
        (New-CoveragePackage -Name "Fixture.Two" -Classes @($emptyClass))
    )
    Write-CoverageReport -RepositoryRoot $emptyRepository -Name "empty" -Packages $emptyPackages -LastWriteTimeUtc $freshWriteTimeUtc

    $emptyResult = Invoke-CoverageVerification -RepositoryRoot $emptyRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc
    Assert-True -Condition ($emptyResult.ExitCode -ne 0) -Message "Packages without executable lines must fail."
    Assert-Contains -Actual $emptyResult.Output -Expected "Coverage output for Fixture.Two did not include executable lines" -Message "Empty-package diagnostics must be preserved."
}
finally {
    if (Test-Path $scenarioRoot) {
        Remove-Item -LiteralPath $scenarioRoot -Recurse -Force
    }
}

Write-Output "Coverage verifier contract tests passed ($assertionCount assertions)."
