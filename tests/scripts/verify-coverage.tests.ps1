Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$coverageScriptPath = Join-Path $repoRoot "scripts\verify-coverage.ps1"
$coverageManifestScriptPath = Join-Path $repoRoot "scripts\verification-coverage-manifest.ps1"
$phaseScriptPath = Join-Path $repoRoot "scripts\verification-phase.ps1"
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

function Invoke-ExpectedFailure {
    param([scriptblock]$Action, [string]$ExpectedMessage)

    $failureMessage = $null
    try {
        & $Action | Out-Null
    }
    catch {
        $failureMessage = $_.Exception.Message
    }
    if ($null -eq $failureMessage) { throw "Expected the action to fail, but it completed successfully." }
    Assert-Contains -Actual $failureMessage -Expected $ExpectedMessage -Message "Failure diagnostic mismatch."
}

$noOpWasRejected = $false
try { Invoke-ExpectedFailure -ExpectedMessage "never emitted" -Action { } } catch { $noOpWasRejected = $_.Exception.Message -ceq "Expected the action to fail, but it completed successfully." }
Assert-True -Condition $noOpWasRejected -Message "The negative-test helper must reject a successful action instead of catching its own sentinel."

. $phaseScriptPath
. (Join-Path $repoRoot "scripts\verification-coverage-evidence.ps1")
. $coverageManifestScriptPath

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
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\verification-coverage-evidence.ps1") -Destination (Join-Path $scriptsPath "verification-coverage-evidence.ps1")
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
        [string]$ReportPath,
        [int]$MaximumCoverageWorkers = 1,
        [switch]$ExternalProcess
    )

    $coverageParameters = @{
        MinimumWriteTimeUtc = $MinimumWriteTimeUtc
        MaximumCoverageWorkers = $MaximumCoverageWorkers
    }
    if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
        $coverageParameters.ResultsRoot = $ResultsRoot
        $coverageParameters.ManifestPath = $ManifestPath
    }
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $coverageParameters.ReportPath = $ReportPath
    }

    if (-not $ExternalProcess) {
        $outputLines = [Collections.Generic.List[string]]::new()
        $exitCode = 0
        try {
            & (Join-Path $RepositoryRoot "scripts\verify-coverage.ps1") @coverageParameters 2>&1 | ForEach-Object {
                $outputLines.Add($_.ToString())
            }
        }
        catch {
            $exitCode = 1
            $outputLines.Add($_.Exception.Message)
        }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = $outputLines -join [Environment]::NewLine
        }
    }

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
    $arguments += @("-MaximumCoverageWorkers", [string]$MaximumCoverageWorkers)
    $startInfo = New-VerificationProcessStartInfo -FileName $powerShellExecutable -Arguments $arguments -WorkingDirectory $RepositoryRoot
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Coverage verifier child process did not start."
        }

        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            Stop-VerificationProcessTree $process
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

function Write-FixtureCoverageManifest {
    param([string]$ResultsRoot, [string]$ManifestPath, [DateTime]$MinimumWriteTimeUtc)

    $fullRoot = [IO.Path]::GetFullPath($ResultsRoot)
    $files = @(Get-ChildItem -LiteralPath $fullRoot -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)
    $projectName = "Fixture.Tests"
    $childResultsRoot = Join-Path $fullRoot "CoverageIsolation\$projectName\canonical\bin\Release\Results"
    New-Item -ItemType Directory -Path $childResultsRoot -Force | Out-Null
    $childFiles = @($files | ForEach-Object {
        $collectorRoot = Join-Path $childResultsRoot ([Guid]::NewGuid().ToString("D"))
        New-Item -ItemType Directory -Path $collectorRoot -Force | Out-Null
        $destination = Join-Path $collectorRoot "coverage.cobertura.xml"
        Move-Item -LiteralPath $_.FullName -Destination $destination
        Get-Item -LiteralPath $destination
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        resultsRoot = $fullRoot
        minimumWriteTimeUtc = $MinimumWriteTimeUtc.ToUniversalTime().ToString("O")
        laneReportCount = 0
        childReportCount = $childFiles.Count
        aliasReportCount = 0
        reports = @($childFiles | ForEach-Object {
            [ordered]@{
                kind = "child"
                projectName = $projectName
                childResultsRoot = $childResultsRoot
                path = $_.FullName
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
        aliases = @()
    }
    [IO.File]::WriteAllText($ManifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}

function Write-FixtureTrx {
    param(
        [string]$Path,
        [string]$DeploymentRoot,
        [switch]$OmitDeployment
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $deployment = if ($OmitDeployment) { "" } else { "<Deployment runDeploymentRoot=`"$DeploymentRoot`" />" }
    $content = "<?xml version=`"1.0`" encoding=`"utf-8`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><TestSettings name=`"default`">$deployment</TestSettings></TestRun>"
    [IO.File]::WriteAllText($Path, $content, [Text.UTF8Encoding]::new($false))
}

function New-CoverageManifestLaneFixture {
    param(
        [string]$RepositoryRoot,
        [object[]]$Packages,
        [DateTime]$LastWriteTimeUtc,
        [switch]$IncludeAlias
    )

    $resultsRoot = Join-Path $RepositoryRoot "tests\Fixture.Tests\TestResults"
    $laneId = "fixture-lane"
    $laneRoot = Join-Path (Join-Path $resultsRoot "StandardTests") $laneId
    $canonicalId = "11111111-1111-4111-8111-111111111111"
    $canonicalName = Join-Path (Join-Path "StandardTests" $laneId) $canonicalId
    Write-CoverageReport -RepositoryRoot $RepositoryRoot -Name $canonicalName -Packages $Packages -LastWriteTimeUtc $LastWriteTimeUtc
    $canonicalPath = Join-Path (Join-Path $laneRoot $canonicalId) "coverage.cobertura.xml"
    $deploymentRoot = "fixture_runner_2026-08-12_00_00_00"
    $trxPath = Join-Path $laneRoot "$laneId.trx"
    Write-FixtureTrx -Path $trxPath -DeploymentRoot $deploymentRoot
    $aliasPath = Join-Path (Join-Path (Join-Path (Join-Path $laneRoot $deploymentRoot) "In") "fixture-machine") "coverage.cobertura.xml"
    if ($IncludeAlias) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $aliasPath) -Force | Out-Null
        Copy-Item -LiteralPath $canonicalPath -Destination $aliasPath
        [IO.File]::SetLastWriteTimeUtc($aliasPath, $LastWriteTimeUtc)
    }

    return [pscustomobject]@{
        ResultsRoot = $resultsRoot
        LaneRoot = $laneRoot
        CanonicalPath = $canonicalPath
        AliasPath = $aliasPath
        TrxPath = $trxPath
        ManifestPath = Join-Path $resultsRoot "coverage-manifest.json"
        Result = [pscustomobject]@{ Name = "tests-$laneId"; CoverageSearchRoot = $laneRoot; TrxPath = $trxPath }
    }
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
    $oneManifestClass = New-CoverageClass -Name "Fixture.One.Manifest" -FileName "src/Fixture.One/File.cs" -Lines @(New-CoverageLines -Hits @(1, 1, 1, 1, 1, 1, 1, 1, 1, 0))
    $manifestGeneratorPackages = @(
        (New-CoveragePackage -Name "Fixture.One" -Classes @($oneManifestClass)),
        (New-CoveragePackage -Name "Fixture.Two" -Classes @($twoPrimaryClass))
    )

    $canonicalOnlyRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-canonical"
    $canonicalOnlyFixture = New-CoverageManifestLaneFixture -RepositoryRoot $canonicalOnlyRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Write-CoverageManifest -TestResults @($canonicalOnlyFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $canonicalOnlyFixture.ResultsRoot -ManifestPath $canonicalOnlyFixture.ManifestPath | Out-Null
    $canonicalOnlyManifest = Get-Content -LiteralPath $canonicalOnlyFixture.ManifestPath -Raw | ConvertFrom-Json
    Assert-True -Condition (@($canonicalOnlyManifest.reports).Count -eq 1 -and @($canonicalOnlyManifest.aliases).Count -eq 0) -Message "A canonical-only lane must retain one merge report and an explicit empty staging-alias inventory."
    Invoke-ExpectedFailure -ExpectedMessage "duplicate lane name, results root, or exact TRX path" -Action {
        Write-CoverageManifest -TestResults @($canonicalOnlyFixture.Result, $canonicalOnlyFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $canonicalOnlyFixture.ResultsRoot -ManifestPath $canonicalOnlyFixture.ManifestPath
    }

    $validAliasRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-alias"
    $validAliasFixture = New-CoverageManifestLaneFixture -RepositoryRoot $validAliasRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc -IncludeAlias
    Write-CoverageManifest -TestResults @($validAliasFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $validAliasFixture.ResultsRoot -ManifestPath $validAliasFixture.ManifestPath | Out-Null
    $validAliasManifest = Get-Content -LiteralPath $validAliasFixture.ManifestPath -Raw | ConvertFrom-Json
    Assert-True -Condition (@($validAliasManifest.reports).Count -eq 1 -and @($validAliasManifest.aliases).Count -eq 1) -Message "One byte-identical VSTest staging alias must be explicit evidence, not a second merge input."
    $validAliasSummaryPath = Join-Path $validAliasFixture.ResultsRoot "coverage-summary.json"
    $validAliasResult = Invoke-CoverageVerification -RepositoryRoot $validAliasRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $validAliasFixture.ResultsRoot -ManifestPath $validAliasFixture.ManifestPath -ReportPath $validAliasSummaryPath
    Assert-True -Condition ($validAliasResult.ExitCode -eq 0) -Message "A validated VSTest staging alias must preserve coverage verification. Actual: $($validAliasResult.Output)"
    Assert-Contains -Actual $validAliasResult.Output -Expected "VERIFY_COVERAGE_REPORT reports=1 packages=2" -Message "Coverage aggregation must merge only the canonical report."
    $validAliasExternalResult = Invoke-CoverageVerification -RepositoryRoot $validAliasRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $validAliasFixture.ResultsRoot -ManifestPath $validAliasFixture.ManifestPath -ReportPath $validAliasSummaryPath -ExternalProcess
    Assert-True -Condition ($validAliasExternalResult.ExitCode -eq 0) -Message "The external coverage-verifier entry point must preserve the passing in-process contract. Actual: $($validAliasExternalResult.Output)"
    Assert-Contains -Actual $validAliasExternalResult.Output -Expected "VERIFY_COVERAGE_REPORT reports=1 packages=2" -Message "The external coverage-verifier entry point must merge the same canonical report inventory."

    $reclassifiedAliasRoot = Join-Path $validAliasFixture.ResultsRoot "CoverageIsolation\Fixture.Tests\canonical\bin\Release\Results"
    New-Item -ItemType Directory -Path $reclassifiedAliasRoot -Force | Out-Null
    $reclassifiedAlias = $validAliasManifest.aliases[0]
    $validAliasManifest.reports = @($validAliasManifest.reports) + [pscustomobject]@{
        kind = "child"
        projectName = "Fixture.Tests"
        childResultsRoot = $reclassifiedAliasRoot
        path = $reclassifiedAlias.path
        length = $reclassifiedAlias.length
        sha256 = $reclassifiedAlias.sha256
    }
    $validAliasManifest.childReportCount = 1
    $validAliasManifest.aliasReportCount = 0
    $validAliasManifest.aliases = @()
    $validAliasManifest.minimumWriteTimeUtc = $minimumWriteTimeUtc.ToUniversalTime().ToString("O")
    [IO.File]::WriteAllText($validAliasFixture.ManifestPath, ($validAliasManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $reclassifiedAliasResult = Invoke-CoverageVerification -RepositoryRoot $validAliasRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $validAliasFixture.ResultsRoot -ManifestPath $validAliasFixture.ManifestPath
    Assert-True -Condition ($reclassifiedAliasResult.ExitCode -ne 0) -Message "A staging alias reclassified as a child report must fail closed."
    Assert-Contains -Actual $reclassifiedAliasResult.Output -Expected "outside its exact collector root" -Message "Child report provenance must reject a reclassified staging alias before aggregation."

    $duplicateLaneRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-consumer-duplicate-lane"
    $duplicateLaneFixture = New-CoverageManifestLaneFixture -RepositoryRoot $duplicateLaneRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Write-CoverageManifest -TestResults @($duplicateLaneFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $duplicateLaneFixture.ResultsRoot -ManifestPath $duplicateLaneFixture.ManifestPath | Out-Null
    $duplicateLaneManifest = Get-Content -LiteralPath $duplicateLaneFixture.ManifestPath -Raw | ConvertFrom-Json
    $secondCollectorRoot = Join-Path $duplicateLaneFixture.LaneRoot "33333333-3333-4333-8333-333333333333"
    New-Item -ItemType Directory -Path $secondCollectorRoot -Force | Out-Null
    $secondLaneReportPath = Join-Path $secondCollectorRoot "coverage.cobertura.xml"
    Copy-Item -LiteralPath $duplicateLaneFixture.CanonicalPath -Destination $secondLaneReportPath
    [IO.File]::SetLastWriteTimeUtc($secondLaneReportPath, $freshWriteTimeUtc)
    $secondLaneEntry = $duplicateLaneManifest.reports[0].PSObject.Copy()
    $secondLaneEntry.path = $secondLaneReportPath
    $secondLaneEntry.length = (Get-Item -LiteralPath $secondLaneReportPath).Length
    $secondLaneEntry.sha256 = (Get-FileHash -LiteralPath $secondLaneReportPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $duplicateLaneManifest.reports = @($duplicateLaneManifest.reports[0], $secondLaneEntry)
    $duplicateLaneManifest.laneReportCount = 2
    $duplicateLaneManifest.minimumWriteTimeUtc = $minimumWriteTimeUtc.ToUniversalTime().ToString("O")
    [IO.File]::WriteAllText($duplicateLaneFixture.ManifestPath, ($duplicateLaneManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $duplicateLaneResult = Invoke-CoverageVerification -RepositoryRoot $duplicateLaneRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $duplicateLaneFixture.ResultsRoot -ManifestPath $duplicateLaneFixture.ManifestPath
    Assert-True -Condition ($duplicateLaneResult.ExitCode -ne 0) -Message "Multiple canonical reports attributed to one lane must fail closed."
    Assert-Contains -Actual $duplicateLaneResult.Output -Expected "duplicate lane name" -Message "Lane identity uniqueness failures must identify the provenance collision."

    $mismatchedAliasRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-alias-mismatch"
    $mismatchedAliasFixture = New-CoverageManifestLaneFixture -RepositoryRoot $mismatchedAliasRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc -IncludeAlias
    Add-Content -LiteralPath $mismatchedAliasFixture.AliasPath -Value "mismatch"
    [IO.File]::SetLastWriteTimeUtc($mismatchedAliasFixture.AliasPath, $freshWriteTimeUtc)
    Invoke-ExpectedFailure -ExpectedMessage "staging alias does not byte-match" -Action {
        Write-CoverageManifest -TestResults @($mismatchedAliasFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $mismatchedAliasFixture.ResultsRoot -ManifestPath $mismatchedAliasFixture.ManifestPath
    }

    $extraReportRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-extra"
    $extraReportFixture = New-CoverageManifestLaneFixture -RepositoryRoot $extraReportRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Write-CoverageReport -RepositoryRoot $extraReportRepository -Name (Join-Path (Join-Path "StandardTests" "fixture-lane") "22222222-2222-4222-8222-222222222222") -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Invoke-ExpectedFailure -ExpectedMessage "2 fresh canonical reports" -Action {
        Write-CoverageManifest -TestResults @($extraReportFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $extraReportFixture.ResultsRoot -ManifestPath $extraReportFixture.ManifestPath
    }

    $malformedTrxRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-malformed-trx"
    $malformedTrxFixture = New-CoverageManifestLaneFixture -RepositoryRoot $malformedTrxRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Set-Content -LiteralPath $malformedTrxFixture.TrxPath -Value "<TestRun" -Encoding UTF8
    Invoke-ExpectedFailure -ExpectedMessage "malformed exact TRX" -Action {
        Write-CoverageManifest -TestResults @($malformedTrxFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $malformedTrxFixture.ResultsRoot -ManifestPath $malformedTrxFixture.ManifestPath
    }

    $missingDeploymentRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-missing-deployment"
    $missingDeploymentFixture = New-CoverageManifestLaneFixture -RepositoryRoot $missingDeploymentRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Write-FixtureTrx -Path $missingDeploymentFixture.TrxPath -DeploymentRoot "unused" -OmitDeployment
    Invoke-ExpectedFailure -ExpectedMessage "must declare exactly one Deployment" -Action {
        Write-CoverageManifest -TestResults @($missingDeploymentFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $missingDeploymentFixture.ResultsRoot -ManifestPath $missingDeploymentFixture.ManifestPath
    }

    $missingTrxRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-missing-trx"
    $missingTrxFixture = New-CoverageManifestLaneFixture -RepositoryRoot $missingTrxRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Remove-Item -LiteralPath $missingTrxFixture.TrxPath -Force
    Invoke-ExpectedFailure -ExpectedMessage "missing its exact TRX" -Action {
        Write-CoverageManifest -TestResults @($missingTrxFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $missingTrxFixture.ResultsRoot -ManifestPath $missingTrxFixture.ManifestPath
    }

    $caseProbeRoot = Join-Path $scenarioRoot "case-sensitive-root"
    $caseProbePath = Join-Path $scenarioRoot "CASE-SENSITIVE-ROOT\coverage.cobertura.xml"
    $caseProbeExpected = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
    Assert-True -Condition ((Test-VerificationCoverageDescendantPath -Path $caseProbePath -Root $caseProbeRoot) -eq $caseProbeExpected) -Message "Coverage path comparison must follow the host filesystem's Windows/non-Windows case contract."

    $snapshotRoot = Join-Path $scenarioRoot "immutable-snapshot"
    New-Item -ItemType Directory -Path $snapshotRoot | Out-Null
    $snapshotPath = Join-Path $snapshotRoot "coverage.cobertura.xml"
    [IO.File]::WriteAllText($snapshotPath, "<coverage marker=`"before`" />", [Text.UTF8Encoding]::new($false))
    $snapshot = Read-VerificationCoverageSnapshot -Path $snapshotPath -Root $snapshotRoot -Description "Immutable-snapshot probe"
    [IO.File]::WriteAllText($snapshotPath, "<coverage marker=`"after!`" />", [Text.UTF8Encoding]::new($false))
    $snapshotXml = ConvertFrom-VerificationCoverageXmlSnapshot -Snapshot $snapshot -Description "Immutable-snapshot probe"
    Assert-True -Condition ($snapshotXml.DocumentElement.GetAttribute("marker") -ceq "before") -Message "Coverage aggregation must parse the exact bytes that were authenticated, not a later same-length path replacement."

    $symlinkRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-symlink"
    $symlinkFixture = New-CoverageManifestLaneFixture -RepositoryRoot $symlinkRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    $symlinkTarget = Join-Path $symlinkRepository "outside-coverage.cobertura.xml"
    Copy-Item -LiteralPath $symlinkFixture.CanonicalPath -Destination $symlinkTarget
    Remove-Item -LiteralPath $symlinkFixture.CanonicalPath -Force
    New-Item -ItemType SymbolicLink -Path $symlinkFixture.CanonicalPath -Target $symlinkTarget | Out-Null
    Invoke-ExpectedFailure -ExpectedMessage "symbolic, hard-link, or reparse artifact" -Action {
        Write-CoverageManifest -TestResults @($symlinkFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $symlinkFixture.ResultsRoot -ManifestPath $symlinkFixture.ManifestPath
    }

    $hardLinkRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-hard-link"
    $hardLinkFixture = New-CoverageManifestLaneFixture -RepositoryRoot $hardLinkRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    $hardLinkTarget = Join-Path $hardLinkRepository "outside-coverage.cobertura.xml"
    Move-Item -LiteralPath $hardLinkFixture.CanonicalPath -Destination $hardLinkTarget
    New-Item -ItemType HardLink -Path $hardLinkFixture.CanonicalPath -Target $hardLinkTarget | Out-Null
    Invoke-ExpectedFailure -ExpectedMessage "symbolic, hard-link, or reparse artifact" -Action {
        Write-CoverageManifest -TestResults @($hardLinkFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $hardLinkFixture.ResultsRoot -ManifestPath $hardLinkFixture.ManifestPath
    }

    $linkedRootRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-linked-root"
    $linkedRootFixture = New-CoverageManifestLaneFixture -RepositoryRoot $linkedRootRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    $physicalLaneRoot = Join-Path $linkedRootRepository "physical-lane-root"
    Move-Item -LiteralPath $linkedRootFixture.LaneRoot -Destination $physicalLaneRoot
    New-Item -ItemType SymbolicLink -Path $linkedRootFixture.LaneRoot -Target $physicalLaneRoot | Out-Null
    Invoke-ExpectedFailure -ExpectedMessage "symbolic, hard-link, or reparse artifact" -Action {
        Write-CoverageManifest -TestResults @($linkedRootFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $linkedRootFixture.ResultsRoot -ManifestPath $linkedRootFixture.ManifestPath
    }

    $linkedTrxRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-generator-linked-trx"
    $linkedTrxFixture = New-CoverageManifestLaneFixture -RepositoryRoot $linkedTrxRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    $physicalTrxPath = Join-Path $linkedTrxRepository "outside.trx"
    Move-Item -LiteralPath $linkedTrxFixture.TrxPath -Destination $physicalTrxPath
    New-Item -ItemType SymbolicLink -Path $linkedTrxFixture.TrxPath -Target $physicalTrxPath | Out-Null
    Invoke-ExpectedFailure -ExpectedMessage "symbolic, hard-link, or reparse artifact" -Action {
        Write-CoverageManifest -TestResults @($linkedTrxFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $linkedTrxFixture.ResultsRoot -ManifestPath $linkedTrxFixture.ManifestPath
    }

    $badAliasPathRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-consumer-bad-alias-path"
    $badAliasPathFixture = New-CoverageManifestLaneFixture -RepositoryRoot $badAliasPathRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc -IncludeAlias
    Write-CoverageManifest -TestResults @($badAliasPathFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $badAliasPathFixture.ResultsRoot -ManifestPath $badAliasPathFixture.ManifestPath | Out-Null
    $badAliasDirectory = Join-Path $badAliasPathFixture.LaneRoot "not-vstest"
    New-Item -ItemType Directory -Path $badAliasDirectory | Out-Null
    $badAliasPath = Join-Path $badAliasDirectory "coverage.cobertura.xml"
    Move-Item -LiteralPath $badAliasPathFixture.AliasPath -Destination $badAliasPath
    $badAliasManifest = Get-Content -LiteralPath $badAliasPathFixture.ManifestPath -Raw | ConvertFrom-Json
    $badAliasManifest.minimumWriteTimeUtc = $minimumWriteTimeUtc.ToUniversalTime().ToString("O")
    $badAliasManifest.aliases[0].path = $badAliasPath
    [IO.File]::WriteAllText($badAliasPathFixture.ManifestPath, ($badAliasManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $badAliasResult = Invoke-CoverageVerification -RepositoryRoot $badAliasPathRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $badAliasPathFixture.ResultsRoot -ManifestPath $badAliasPathFixture.ManifestPath
    Assert-True -Condition ($badAliasResult.ExitCode -ne 0) -Message "A byte-identical file outside the exact TRX staging path must not be accepted as an alias."
    Assert-Contains -Actual $badAliasResult.Output -Expected "staging alias is outside" -Message "Alias provenance failures must identify the exact TRX path contract."

    $badCountRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-consumer-bad-count"
    $badCountFixture = New-CoverageManifestLaneFixture -RepositoryRoot $badCountRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Write-CoverageManifest -TestResults @($badCountFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $badCountFixture.ResultsRoot -ManifestPath $badCountFixture.ManifestPath | Out-Null
    $badCountManifest = Get-Content -LiteralPath $badCountFixture.ManifestPath -Raw | ConvertFrom-Json
    $badCountManifest.minimumWriteTimeUtc = $minimumWriteTimeUtc.ToUniversalTime().ToString("O")
    $badCountManifest.laneReportCount = 2
    [IO.File]::WriteAllText($badCountFixture.ManifestPath, ($badCountManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $badCountResult = Invoke-CoverageVerification -RepositoryRoot $badCountRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $badCountFixture.ResultsRoot -ManifestPath $badCountFixture.ManifestPath
    Assert-True -Condition ($badCountResult.ExitCode -ne 0) -Message "Manifest inventory counts must be exact."
    Assert-Contains -Actual $badCountResult.Output -Expected "counts do not match" -Message "Count-integrity failures must be actionable."

    $badTimestampRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-consumer-bad-timestamp"
    $badTimestampFixture = New-CoverageManifestLaneFixture -RepositoryRoot $badTimestampRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Write-CoverageManifest -TestResults @($badTimestampFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $badTimestampFixture.ResultsRoot -ManifestPath $badTimestampFixture.ManifestPath | Out-Null
    $badTimestampManifest = Get-Content -LiteralPath $badTimestampFixture.ManifestPath -Raw | ConvertFrom-Json
    $badTimestampManifest.minimumWriteTimeUtc = $minimumWriteTimeUtc.AddTicks(1).ToUniversalTime().ToString("O")
    [IO.File]::WriteAllText($badTimestampFixture.ManifestPath, ($badTimestampManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $badTimestampResult = Invoke-CoverageVerification -RepositoryRoot $badTimestampRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $badTimestampFixture.ResultsRoot -ManifestPath $badTimestampFixture.ManifestPath
    Assert-True -Condition ($badTimestampResult.ExitCode -ne 0) -Message "A manifest from a different minimum-write-time window must fail closed."
    Assert-Contains -Actual $badTimestampResult.Output -Expected "does not bind the requested minimum write time" -Message "Timestamp-integrity failures must be actionable."

    $badTypeRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-consumer-bad-type"
    $badTypeFixture = New-CoverageManifestLaneFixture -RepositoryRoot $badTypeRepository -Packages $manifestGeneratorPackages -LastWriteTimeUtc $freshWriteTimeUtc
    Write-CoverageManifest -TestResults @($badTypeFixture.Result) -Isolations @() -MinimumWriteTimeUtc $minimumWriteTimeUtc -VerificationResultsPath $badTypeFixture.ResultsRoot -ManifestPath $badTypeFixture.ManifestPath | Out-Null
    $badTypeManifest = Get-Content -LiteralPath $badTypeFixture.ManifestPath -Raw | ConvertFrom-Json
    $badTypeManifest.minimumWriteTimeUtc = $minimumWriteTimeUtc.ToUniversalTime().ToString("O")
    $badTypeManifest.aliasReportCount = "0"
    [IO.File]::WriteAllText($badTypeFixture.ManifestPath, ($badTypeManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $badTypeResult = Invoke-CoverageVerification -RepositoryRoot $badTypeRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $badTypeFixture.ResultsRoot -ManifestPath $badTypeFixture.ManifestPath
    Assert-True -Condition ($badTypeResult.ExitCode -ne 0) -Message "A string substituted for a schema-1 integer must fail closed."
    Assert-Contains -Actual $badTypeResult.Output -Expected "non-negative schema-1 integer" -Message "Typed-schema failures must be actionable."

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

    Assert-Contains -Actual (Get-Content -LiteralPath (Join-Path $passingRepository "scripts\verify-coverage.ps1") -Raw) -Expected '[ValidateRange(1, 2)] [int]$MaximumCoverageWorkers = 2' -Message "Production coverage verification must default to two workers and reject larger hosted fan-out before execution."

    Write-CoverageReport -RepositoryRoot $passingRepository -Name "parallel-probe" -Packages @($aliasPackage) -LastWriteTimeUtc $freshWriteTimeUtc
    $parallelSourceProjects = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($packageName in @("Fixture.One", "Fixture.Two")) { $parallelSourceProjects.Add($packageName, (Join-Path $passingRepository "src\$packageName")) }
    $parallelRoot = Join-Path $passingRepository "tests\Fixture.Tests\TestResults"
    $parallelFiles = @(Get-ChildItem -LiteralPath $parallelRoot -Recurse -Filter "coverage.cobertura.xml" -File | Sort-Object FullName)
    $parallelItems = @($parallelFiles | ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Root = $parallelRoot; Description = "Coverage parallel equivalence report"; Reduce = $true } })
    $singleWorkerReduction = Invoke-VerificationCoverageWorkers -WorkItems $parallelItems -RepositoryRoot $passingRepository -SourceProjectDirectories $parallelSourceProjects -MaximumWorkers 1
    $twoWorkerReduction = Invoke-VerificationCoverageWorkers -WorkItems $parallelItems -RepositoryRoot $passingRepository -SourceProjectDirectories $parallelSourceProjects -MaximumWorkers 2
    $singleWorkerEvidence = [ordered]@{ snapshots = @($singleWorkerReduction.Snapshots | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.Sha256)" }); packages = @($singleWorkerReduction.Packages); lines = @($singleWorkerReduction.Lines) } | ConvertTo-Json -Depth 5 -Compress
    $twoWorkerEvidence = [ordered]@{ snapshots = @($twoWorkerReduction.Snapshots | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.Sha256)" }); packages = @($twoWorkerReduction.Packages); lines = @($twoWorkerReduction.Lines) } | ConvertTo-Json -Depth 5 -Compress
    Assert-True -Condition ($singleWorkerEvidence -ceq $twoWorkerEvidence) -Message "One- and two-worker authenticated reductions must be deterministic and equivalent."
    Invoke-ExpectedFailure -ExpectedMessage "MaximumWorkers" -Action {
        Invoke-VerificationCoverageWorkers -WorkItems $parallelItems -RepositoryRoot $passingRepository -SourceProjectDirectories $parallelSourceProjects -MaximumWorkers 3
    }

    $missingParallelItems = @($parallelItems[0..2]) + @([pscustomobject]@{ Path = (Join-Path $parallelRoot "missing\coverage.cobertura.xml"); Root = $parallelRoot; Description = "Coverage missing parallel report"; Reduce = $true })
    Invoke-ExpectedFailure -ExpectedMessage "missing or is not a leaf" -Action {
        Invoke-VerificationCoverageWorkers -WorkItems $missingParallelItems -RepositoryRoot $passingRepository -SourceProjectDirectories $parallelSourceProjects -MaximumWorkers 2
    }

    $corruptParallelPath = Join-Path $parallelRoot "parallel-corrupt\coverage.cobertura.xml"
    New-Item -ItemType Directory -Path (Split-Path -Parent $corruptParallelPath) -Force | Out-Null
    Set-Content -LiteralPath $corruptParallelPath -Value "<coverage" -Encoding UTF8
    $corruptParallelItems = @($parallelItems[0..2]) + @([pscustomobject]@{ Path = $corruptParallelPath; Root = $parallelRoot; Description = "Coverage corrupt parallel report"; Reduce = $true })
    Invoke-ExpectedFailure -ExpectedMessage "Coverage worker failure" -Action {
        Invoke-VerificationCoverageWorkers -WorkItems $corruptParallelItems -RepositoryRoot $passingRepository -SourceProjectDirectories $parallelSourceProjects -MaximumWorkers 2
    }

    $manifestRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "manifest-passing"
    Write-CoverageReport -RepositoryRoot $manifestRepository -Name "primary" -Packages $primaryPackages -LastWriteTimeUtc $freshWriteTimeUtc
    $manifestAliasClass = New-CoverageClass -Name "Fixture.One.Alias" -FileName (Join-Path $manifestRepository "src\Fixture.One\File.cs") -Lines @((New-CoverageLine -Number 1 -Hits 0), (New-CoverageLine -Number 9 -Hits 7))
    Write-CoverageReport -RepositoryRoot $manifestRepository -Name "alias" -Packages @((New-CoveragePackage -Name "Fixture.One" -Classes @($manifestAliasClass))) -LastWriteTimeUtc $freshWriteTimeUtc
    $manifestResultsRoot = Join-Path $manifestRepository "tests\Fixture.Tests\TestResults"
    $manifestPath = Join-Path $manifestResultsRoot "coverage-manifest.json"
    $summaryPath = Join-Path $manifestRepository "coverage-summary.json"
    Write-FixtureCoverageManifest -ResultsRoot $manifestResultsRoot -ManifestPath $manifestPath -MinimumWriteTimeUtc $minimumWriteTimeUtc
    $manifestPassing = Invoke-CoverageVerification -RepositoryRoot $manifestRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $manifestResultsRoot -ManifestPath $manifestPath -ReportPath $summaryPath
    Assert-True -Condition ($manifestPassing.ExitCode -eq 0) -Message "An exact fresh coverage manifest must pass. Actual: $($manifestPassing.Output)"
    Assert-Contains -Actual $manifestPassing.Output -Expected "VERIFY_COVERAGE_REPORT reports=2 packages=2" -Message "Manifest-backed coverage must retain exact counts."
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    Assert-True -Condition ($summary.threshold -eq 0.90 -and @($summary.packages).Count -eq 2) -Message "The deterministic summary must retain the unchanged threshold and every package."

    $unexpectedDirectory = Join-Path $manifestResultsRoot "unexpected"
    New-Item -ItemType Directory -Path $unexpectedDirectory | Out-Null
    $manifestInventory = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath $manifestInventory.reports[0].path -Destination (Join-Path $unexpectedDirectory "coverage.cobertura.xml")
    $unexpectedManifestResult = Invoke-CoverageVerification -RepositoryRoot $manifestRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ResultsRoot $manifestResultsRoot -ManifestPath $manifestPath
    Assert-True -Condition ($unexpectedManifestResult.ExitCode -ne 0) -Message "Coverage outside the exact manifest must fail closed."
    Assert-Contains -Actual $unexpectedManifestResult.Output -Expected "missing, stale, or unexpected reports" -Message "Unexpected-report diagnostics must be actionable."

    $duplicateManifestRepository = New-FixtureRepository -ScenarioRoot $scenarioRoot -Name "duplicate-manifest"
    Write-CoverageReport -RepositoryRoot $duplicateManifestRepository -Name "primary" -Packages $primaryPackages -LastWriteTimeUtc $freshWriteTimeUtc
    $duplicateResultsRoot = Join-Path $duplicateManifestRepository "tests\Fixture.Tests\TestResults"
    $duplicateManifestPath = Join-Path $duplicateResultsRoot "coverage-manifest.json"
    Write-FixtureCoverageManifest -ResultsRoot $duplicateResultsRoot -ManifestPath $duplicateManifestPath -MinimumWriteTimeUtc $minimumWriteTimeUtc
    $duplicateManifest = Get-Content -LiteralPath $duplicateManifestPath -Raw | ConvertFrom-Json
    $duplicateManifest.reports = @($duplicateManifest.reports[0], $duplicateManifest.reports[0])
    $duplicateManifest.childReportCount = 2
    $duplicateManifest.minimumWriteTimeUtc = $minimumWriteTimeUtc.ToUniversalTime().ToString("O")
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
    $corruptManifestPath = Join-Path (Split-Path -Parent $corruptResultsRoot) "coverage-manifest.json"
    Write-FixtureCoverageManifest -ResultsRoot (Split-Path -Parent $corruptResultsRoot) -ManifestPath $corruptManifestPath -MinimumWriteTimeUtc $minimumWriteTimeUtc
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
    $expectedGap = "COVERAGE_GAP package=Fixture.One uncovered=2 total=10 file=src${separator}Fixture.One${separator}File.cs lines=9,10"
    Assert-Contains -Actual $failingResult.Output -Expected $expectedGap -Message "Coverage gaps must remain stable and actionable."
    Assert-Contains -Actual $failingResult.Output -Expected "Fixture.One line coverage 80% is below 90%" -Message "Below-threshold diagnostics must preserve the enforced threshold."
    $failingExternalResult = Invoke-CoverageVerification -RepositoryRoot $failingRepository -MinimumWriteTimeUtc $minimumWriteTimeUtc -ExternalProcess
    Assert-True -Condition ($failingExternalResult.ExitCode -ne 0) -Message "The external coverage-verifier entry point must preserve the failing in-process contract."
    Assert-Contains -Actual $failingExternalResult.Output -Expected $expectedGap -Message "The external coverage-verifier entry point must preserve actionable gap evidence."
    Assert-Contains -Actual $failingExternalResult.Output -Expected "Fixture.One line coverage 80% is below 90%" -Message "The external coverage-verifier entry point must preserve the immutable threshold failure."

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
