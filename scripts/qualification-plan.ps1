Set-StrictMode -Version Latest

$script:QualificationTestProjects = @(
    "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
    "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj",
    "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj",
    "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
    "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj",
    "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
    "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj",
    "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj",
    "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
)

$script:QualificationSourceMappings = @(
    [pscustomobject]@{ Prefix = "src/EmbodySense.Cli.Command/"; TestProjects = @("tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj") },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Cli/"; TestProjects = @("tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj") },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Application/"; TestProjects = @("tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj") },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Clients/"; TestProjects = @("tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj") },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Common/"; TestProjects = @("tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj") },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Core.Persistence/"; TestProjects = @("tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj") },
    [pscustomobject]@{
        Prefix = "src/EmbodySense.Core.Startup/"
        TestProjects = @(
            "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj",
            "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj",
            "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj",
            "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj"
        )
    },
    [pscustomobject]@{ Prefix = "src/EmbodySense.Web/"; TestProjects = @("tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj") }
)

$script:QualificationTestMappings = @(
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Cli.Command.Tests/"; TestProject = "tests/EmbodySense.Cli.Command.Tests/EmbodySense.Cli.Command.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Application.Tests/"; TestProject = "tests/EmbodySense.Core.Application.Tests/EmbodySense.Core.Application.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Clients.Tests/"; TestProject = "tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Common.Tests/"; TestProject = "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Persistence.Tests/"; TestProject = "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Core.Startup.Tests/"; TestProject = "tests/EmbodySense.Core.Startup.Tests/EmbodySense.Core.Startup.Tests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.E2ETests/"; TestProject = "tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.IntegrationTests/"; TestProject = "tests/EmbodySense.IntegrationTests/EmbodySense.IntegrationTests.csproj" },
    [pscustomobject]@{ Prefix = "tests/EmbodySense.Web.Tests/"; TestProject = "tests/EmbodySense.Web.Tests/EmbodySense.Web.Tests.csproj" }
)

$script:QualificationLinkedTestMappings = @(
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/Authority/Grants/AuthorityGrantTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/Loops/Admission/GovernedLoopAdmissionTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Common.Tests/Loops/Execution/Authority/GovernedLoopEffectAuthorityTestFixture.cs"
        TestProjects = @(
            "tests/EmbodySense.Core.Common.Tests/EmbodySense.Core.Common.Tests.csproj",
            "tests/EmbodySense.Core.Persistence.Tests/EmbodySense.Core.Persistence.Tests.csproj"
        )
    }
)
$script:QualificationFocusedHelperMappings = @(
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Application.Tests/Capabilities/CapabilityArtifactTestData.cs"
        ConsumerNamespaces = @(
            "EmbodySense.Core.Application.Tests.Capabilities",
            "EmbodySense.Core.Application.Tests.Credentials"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Persistence.Tests/Verification/Models/VerificationPhaseBudget.cs"
        ConsumerNamespaces = @(
            "EmbodySense.Core.Persistence.Tests.Loops",
            "EmbodySense.Core.Persistence.Tests.Verification"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.Core.Persistence.Tests/Verification/Models/VerificationPhaseClassification.cs"
        ConsumerNamespaces = @(
            "EmbodySense.Core.Persistence.Tests.Loops",
            "EmbodySense.Core.Persistence.Tests.Verification",
            "EmbodySense.Core.Persistence.Tests.Verification.Models"
        )
    },
    [pscustomobject]@{
        Path = "tests/EmbodySense.IntegrationTests/Core/Governance/Tools/ImmediateToolResultRetentionStore.cs"
        ConsumerNamespaces = @("EmbodySense.IntegrationTests.Core.Governance.Tools")
    }
)
$script:QualificationCSharpParserInitialized = $false

function ConvertTo-QualificationPath {
    param([Parameter(Mandatory = $true)] [string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.IndexOfAny([char[]]@([char]0, [char]10, [char]13)) -ge 0) {
        throw "Qualification paths must be non-empty repository-relative text."
    }

    $normalized = $Path.Replace('\', '/')
    if ($normalized.StartsWith("./", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }
    if ($normalized.Length -eq 0 -or $normalized.Split('/') -contains "..") {
        throw "Qualification path escapes the repository: '$Path'."
    }

    return $normalized
}

function Get-QualificationTestProject {
    param([Parameter(Mandatory = $true)] [string]$Path)

    foreach ($mapping in $script:QualificationTestMappings) {
        if ($Path.StartsWith($mapping.Prefix, [StringComparison]::Ordinal)) {
            return $mapping.TestProject
        }
    }

    return $null
}

function Get-QualificationLinkedTestMapping {
    param([Parameter(Mandatory = $true)] [string]$Path)

    foreach ($mapping in $script:QualificationLinkedTestMappings) {
        if ($Path -ceq $mapping.Path) {
            return $mapping
        }
    }

    return $null
}

function Get-QualificationFocusedHelperMapping {
    param([Parameter(Mandatory = $true)] [string]$Path)

    foreach ($mapping in $script:QualificationFocusedHelperMappings) {
        if ($Path -ceq $mapping.Path) {
            return $mapping
        }
    }

    return $null
}

function Test-QualificationFilterableTestSource {
    param([Parameter(Mandatory = $true)] [string]$Path)

    return $Path.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($Path) -cne "AssemblyInfo.cs" -and
        $null -eq (Get-QualificationLinkedTestMapping -Path $Path) -and
        $null -ne (Get-QualificationTestProject -Path $Path)
}

function Initialize-QualificationCSharpParser {
    if ($script:QualificationCSharpParserInitialized) {
        return
    }

    if ($null -eq ("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree" -as [type])) {
        Add-Type -AssemblyName "Microsoft.CodeAnalysis.CSharp"
    }
    if ($null -eq ("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree" -as [type])) {
        throw "Qualification could not load the PowerShell-hosted Roslyn C# parser."
    }

    $script:QualificationCSharpParserInitialized = $true
}

function Get-QualificationDeclaredTestNamespace {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Content
    )

    Initialize-QualificationCSharpParser
    $syntaxTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content)
    $namespaceDeclarations = @($syntaxTree.GetCompilationUnitRoot().Members | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax] })
    if ($namespaceDeclarations.Count -ne 1) {
        throw "Qualification requires exactly one top-level declared namespace in changed test source '$Path'. Found $($namespaceDeclarations.Count)."
    }

    $declaredNamespace = $namespaceDeclarations[0].Name.ToString()

    $testProject = Get-QualificationTestProject -Path $Path
    $projectNamespace = [IO.Path]::GetFileNameWithoutExtension($testProject)
    if ($declaredNamespace -cne $projectNamespace -and -not $declaredNamespace.StartsWith("$projectNamespace.", [StringComparison]::Ordinal)) {
        throw "Qualification test namespace '$declaredNamespace' does not belong to owning project '$projectNamespace' for '$Path'."
    }

    return $declaredNamespace
}

function Test-QualificationContainsDirectXunitTest {
    param([Parameter(Mandatory = $true)] [string]$Content)

    Initialize-QualificationCSharpParser
    $root = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content).GetCompilationUnitRoot()
    $testAttributeNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($attributeName in @("Fact", "FactAttribute", "Theory", "TheoryAttribute")) {
        [void]$testAttributeNames.Add($attributeName)
    }

    foreach ($typeDeclaration in @($root.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax] })) {
        if ($null -eq $typeDeclaration.BaseList) {
            continue
        }

        foreach ($baseType in @($typeDeclaration.BaseList.Types)) {
            $baseName = @($baseType.Type.ToString() -split '::|\.')[-1]
            if ($baseName -cne "FactAttribute" -and $baseName -cne "TheoryAttribute") {
                continue
            }

            $declaredName = $typeDeclaration.Identifier.ValueText
            [void]$testAttributeNames.Add($declaredName)
            if ($declaredName.EndsWith("Attribute", [StringComparison]::Ordinal)) {
                [void]$testAttributeNames.Add($declaredName.Substring(0, $declaredName.Length - "Attribute".Length))
            }
        }
    }

    foreach ($methodDeclaration in @($root.DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] })) {
        foreach ($attributeList in @($methodDeclaration.AttributeLists)) {
            foreach ($attribute in @($attributeList.Attributes)) {
                $simpleName = @($attribute.Name.ToString() -split '::|\.')[-1]
                if ($testAttributeNames.Contains($simpleName)) {
                    return $true
                }
            }
        }
    }

    return $false
}

function Test-QualificationContainsFocusedHelperReference {
    param(
        [Parameter(Mandatory = $true)] [string]$Content,
        [AllowEmptyCollection()] [string[]]$HelperIdentifiers = @()
    )

    if ($HelperIdentifiers.Count -eq 0) {
        $HelperIdentifiers = @($script:QualificationFocusedHelperMappings | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Path) })
    }

    $identifiers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($helperIdentifier in $HelperIdentifiers) {
        if ([string]::IsNullOrWhiteSpace($helperIdentifier)) {
            throw "Qualification focused helper identifiers must be non-empty."
        }
        [void]$identifiers.Add($helperIdentifier)
    }

    Initialize-QualificationCSharpParser
    $root = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($Content).GetCompilationUnitRoot()
    foreach ($token in $root.DescendantTokens()) {
        if ($identifiers.Contains($token.ValueText)) {
            return $true
        }
    }

    return $false
}

function Get-QualificationPlan {
    param(
        [Parameter(Mandatory = $true)] [string[]]$ChangedPaths,
        [Collections.IDictionary]$TestNamespacesByPath = @{},
        [AllowEmptyCollection()] [string[]]$FocusedHelperRelevantPaths = @(),
        [AllowNull()] [AllowEmptyCollection()] [string[]]$AvailableTestProjects = $null
    )

    if ($ChangedPaths.Count -eq 0) {
        throw "Qualification requires at least one changed path."
    }

    $normalizedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $canonicalTestProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($testProject in $script:QualificationTestProjects) {
        [void]$canonicalTestProjects.Add($testProject)
    }
    $availableTestProjectSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (-not $PSBoundParameters.ContainsKey("AvailableTestProjects")) {
        foreach ($testProject in $script:QualificationTestProjects) {
            [void]$availableTestProjectSet.Add($testProject)
        }
    }
    else {
        foreach ($availableTestProject in $AvailableTestProjects) {
            $normalizedTestProject = ConvertTo-QualificationPath -Path $availableTestProject
            if (-not $canonicalTestProjects.Contains($normalizedTestProject)) {
                throw "Qualification received an unknown available test project '$normalizedTestProject'."
            }
            [void]$availableTestProjectSet.Add($normalizedTestProject)
        }
    }
    $focusedHelperRelevantPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($focusedHelperRelevantPath in $FocusedHelperRelevantPaths) {
        [void]$focusedHelperRelevantPathSet.Add((ConvertTo-QualificationPath -Path $focusedHelperRelevantPath))
    }
    $testProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $unfilteredTestProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $filteredTestNamespaces = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $unclassifiedPaths = [Collections.Generic.List[string]]::new()
    $requiresBuild = $false
    $requiresFrontend = $false
    $requiresWorkflowValidation = $false
    $requiresVerifierContracts = $false
    $requiresCSharpFormat = $false
    $requiresArchitecture = $false
    $requiresDrawioValidation = $false
    $changedCSharpFiles = [Collections.Generic.List[string]]::new()

    foreach ($changedPath in $ChangedPaths) {
        $path = ConvertTo-QualificationPath -Path $changedPath
        if (-not $normalizedPaths.Add($path)) {
            continue
        }

        $classified = $false
        if ($focusedHelperRelevantPathSet.Contains($path)) {
            $requiresVerifierContracts = $true
        }
        if ($path.StartsWith("scripts/", [StringComparison]::Ordinal) -or $path.StartsWith("tests/scripts/", [StringComparison]::Ordinal) -or $path.StartsWith(".github/", [StringComparison]::Ordinal)) {
            $requiresVerifierContracts = $true
            $classified = $true
        }

        if ($path.StartsWith(".github/workflows/", [StringComparison]::Ordinal)) {
            $requiresFrontend = $true
            $requiresWorkflowValidation = $true
        }

        if ($path -ceq "package.json" -or $path -ceq "package-lock.json" -or $path -ceq "eslint.config.js" -or $path -ceq ".prettierignore" -or $path.StartsWith("tests/frontend/", [StringComparison]::Ordinal) -or $path.StartsWith("src/EmbodySense.Web/wwwroot/", [StringComparison]::Ordinal)) {
            $requiresFrontend = $true
            $classified = $true
        }

        foreach ($mapping in $script:QualificationSourceMappings) {
            if ($path.StartsWith($mapping.Prefix, [StringComparison]::Ordinal)) {
                $requiresBuild = $true
                $requiresArchitecture = $true
                foreach ($testProject in $mapping.TestProjects) {
                    [void]$testProjects.Add($testProject)
                    [void]$unfilteredTestProjects.Add($testProject)
                    [void]$filteredTestNamespaces.Remove($testProject)
                }
                $classified = $true
                break
            }
        }

        foreach ($mapping in $script:QualificationTestMappings) {
            if ($path.StartsWith($mapping.Prefix, [StringComparison]::Ordinal)) {
                $requiresBuild = $true
                [void]$testProjects.Add($mapping.TestProject)
                if (Test-QualificationFilterableTestSource -Path $path) {
                    if (-not $TestNamespacesByPath.ContainsKey($path)) {
                        throw "Qualification is missing the declared namespace for changed test source '$path'."
                    }

                    $testNamespaces = @($TestNamespacesByPath[$path])
                    if ($testNamespaces.Count -eq 0 -or @($testNamespaces | Where-Object { [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
                        [void]$unfilteredTestProjects.Add($mapping.TestProject)
                        [void]$filteredTestNamespaces.Remove($mapping.TestProject)
                        $classified = $true
                        break
                    }

                    $projectNamespace = [IO.Path]::GetFileNameWithoutExtension($mapping.TestProject)
                    foreach ($testNamespaceValue in $testNamespaces) {
                        $testNamespace = [string]$testNamespaceValue
                        if ($testNamespace -cne $projectNamespace -and -not $testNamespace.StartsWith("$projectNamespace.", [StringComparison]::Ordinal)) {
                            throw "Qualification test namespace '$testNamespace' does not belong to owning project '$projectNamespace' for '$path'."
                        }

                        if (-not $unfilteredTestProjects.Contains($mapping.TestProject)) {
                            if (-not $filteredTestNamespaces.ContainsKey($mapping.TestProject)) {
                                $filteredTestNamespaces.Add($mapping.TestProject, [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal))
                            }
                            [void]$filteredTestNamespaces[$mapping.TestProject].Add($testNamespace)
                        }
                    }
                }
                else {
                    [void]$unfilteredTestProjects.Add($mapping.TestProject)
                    [void]$filteredTestNamespaces.Remove($mapping.TestProject)
                }
                $classified = $true
                break
            }
        }

        $linkedTestMapping = Get-QualificationLinkedTestMapping -Path $path
        if ($null -ne $linkedTestMapping) {
            $requiresBuild = $true
            foreach ($testProject in $linkedTestMapping.TestProjects) {
                [void]$testProjects.Add($testProject)
                [void]$unfilteredTestProjects.Add($testProject)
                [void]$filteredTestNamespaces.Remove($testProject)
            }
            $classified = $true
        }

        if ($path.StartsWith("tests/EmbodySense.CancellationHost/", [StringComparison]::Ordinal) -or $path.StartsWith("tests/EmbodySense.Tests.Support/", [StringComparison]::Ordinal) -or $path.StartsWith("tests/Shared/", [StringComparison]::Ordinal)) {
            $requiresBuild = $true
            foreach ($testProject in $script:QualificationTestProjects) {
                [void]$testProjects.Add($testProject)
                [void]$unfilteredTestProjects.Add($testProject)
                [void]$filteredTestNamespaces.Remove($testProject)
            }
            $classified = $true
        }

        if ($path -ceq "tests/verification-pull-request.runsettings" -or $path -ceq "tests/verification-stress.runsettings") {
            $requiresBuild = $true
            $requiresVerifierContracts = $true
            foreach ($testProject in $script:QualificationTestProjects) {
                [void]$testProjects.Add($testProject)
                [void]$unfilteredTestProjects.Add($testProject)
                [void]$filteredTestNamespaces.Remove($testProject)
            }
            $classified = $true
        }

        if ($path.EndsWith(".cs", [StringComparison]::OrdinalIgnoreCase)) {
            $requiresCSharpFormat = $true
            $changedCSharpFiles.Add($path)
        }

        if ($path -ceq "EmbodySense.sln" -or $path -ceq "Directory.Build.props" -or $path -ceq "global.json" -or $path -ceq "NuGet.config" -or $path -ceq ".editorconfig" -or $path -ceq ".gitattributes" -or $path.EndsWith(".props", [StringComparison]::OrdinalIgnoreCase) -or $path.EndsWith(".targets", [StringComparison]::OrdinalIgnoreCase)) {
            $requiresBuild = $true
            $requiresArchitecture = $true
            foreach ($testProject in $script:QualificationTestProjects) {
                [void]$testProjects.Add($testProject)
                [void]$unfilteredTestProjects.Add($testProject)
                [void]$filteredTestNamespaces.Remove($testProject)
            }
            $classified = $true
        }

        if ($path.EndsWith(".drawio", [StringComparison]::OrdinalIgnoreCase)) {
            $requiresDrawioValidation = $true
        }

        if ($path -ceq "README.md" -or $path -ceq "AGENTS.md" -or $path -ceq ".gitignore" -or $path.StartsWith("docs/", [StringComparison]::Ordinal)) {
            $classified = $true
        }

        if (-not $classified) {
            $unclassifiedPaths.Add($path)
        }
    }

    if ($unclassifiedPaths.Count -gt 0) {
        throw "Qualification has unclassified changed paths: $(@($unclassifiedPaths | Sort-Object) -join ', '). Update the explicit qualification ownership map instead of silently skipping them."
    }
    foreach ($focusedHelperRelevantPath in $focusedHelperRelevantPathSet) {
        if (-not $normalizedPaths.Contains($focusedHelperRelevantPath)) {
            throw "Qualification focused helper relevance named an unchanged path '$focusedHelperRelevantPath'."
        }
    }

    $scheduledTestProjects = @($testProjects | Where-Object { $availableTestProjectSet.Contains($_) } | Sort-Object)
    $testSelections = @(
        foreach ($testProject in $scheduledTestProjects) {
            if ($unfilteredTestProjects.Contains($testProject)) {
                $namespaces = [string[]]::new(0)
            }
            elseif ($filteredTestNamespaces.ContainsKey($testProject)) {
                $namespaces = [string[]]@($filteredTestNamespaces[$testProject] | Sort-Object)
            }
            else {
                throw "Qualification test project '$testProject' has neither full-project nor namespace ownership."
            }

            [pscustomobject]@{
                Project = $testProject
                Namespaces = $namespaces
            }
        }
    )

    return [pscustomobject]@{
        SchemaVersion = 1
        ChangedPaths = @($normalizedPaths | Sort-Object)
        RequiresBuild = $requiresBuild
        RequiresFrontend = $requiresFrontend
        RequiresWorkflowValidation = $requiresWorkflowValidation
        RequiresVerifierContracts = $requiresVerifierContracts
        RequiresCSharpFormat = $requiresCSharpFormat
        RequiresArchitecture = $requiresArchitecture
        RequiresDrawioValidation = $requiresDrawioValidation
        ChangedCSharpFiles = @($changedCSharpFiles | Sort-Object -Unique)
        TestProjects = $scheduledTestProjects
        TestSelections = $testSelections
    }
}
