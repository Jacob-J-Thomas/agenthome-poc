[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Write", "Read", "Check")]
    [string]$Operation,

    [string]$RepositoryRoot = (Get-Location).Path,

    [string]$InputPath,

    [string]$ExpectedDocumentSha256,

    [string[]]$ApprovedProtectedWorktreePath = @(),

    [ValidateRange(10, 30000)]
    [int]$LockTimeoutMilliseconds = 5000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:MaximumDocumentBytes = 262144
$script:MaximumGitOutputBytes = 65536
$script:GitTimeoutMilliseconds = 2000
$script:SchemaPath = Join-Path $PSScriptRoot "delivery-handoff.schema.json"
$script:Utf8 = [Text.UTF8Encoding]::new($false, $true)
$script:PathComparison = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows) -or [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Get-Sha256 {
    param([Parameter(Mandatory = $true)] [AllowEmptyCollection()] [byte[]]$Bytes)

    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Read-BoundedFileBytes {
    param([Parameter(Mandatory = $true)] [string]$Path)

    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 4096, [IO.FileOptions]::SequentialScan)
    try {
        if ($stream.Length -gt $script:MaximumDocumentBytes) { throw "document-too-large" }
        $buffer = [byte[]]::new($script:MaximumDocumentBytes + 1)
        $count = 0
        while ($count -lt $buffer.Length) {
            $read = $stream.Read($buffer, $count, $buffer.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -gt $script:MaximumDocumentBytes) { throw "document-too-large" }
        $result = [byte[]]::new($count)
        if ($count -gt 0) { [Array]::Copy($buffer, $result, $count) }
        return ,$result
    }
    finally { $stream.Dispose() }
}

function ConvertTo-OrdinalStringSet {
    param([Parameter(Mandatory = $true)] [AllowEmptyCollection()]$Values)

    [string[]]$orderedValues = @($Values | ForEach-Object { [string]$_ })
    [Array]::Sort($orderedValues, [StringComparer]::Ordinal)
    $result = [Collections.Generic.List[string]]::new()
    $previous = $null
    foreach ($value in $orderedValues) {
        if ($null -eq $previous -or -not [string]::Equals($previous, $value, [StringComparison]::Ordinal)) { $result.Add($value) }
        $previous = $value
    }
    return ,$result.ToArray()
}

function Sort-ObjectsByOrdinalPath {
    param([Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]]$Values)

    [string[]]$keys = @($Values | ForEach-Object { [string]$_.path })
    [Array]::Sort[string, object]($keys, $Values, [StringComparer]::Ordinal)
    return ,$Values
}

function Write-DeliveryResult {
    param(
        [Parameter(Mandatory = $true)] [string]$Result,
        [Parameter(Mandatory = $true)] [int]$ExitCode,
        [string[]]$Reasons = @(),
        [string]$DocumentSha256,
        [object]$Handoff,
        [string[]]$RedactionPaths = @()
    )

    $value = [ordered]@{
        schemaVersion = 1
        result = $Result
        authorityIsGrant = $false
        requiresIndependentAuthorityRevalidation = $true
        reasons = ConvertTo-OrdinalStringSet -Values $Reasons
    }
    if (-not [string]::IsNullOrEmpty($DocumentSha256)) { $value.documentSha256 = $DocumentSha256 }
    if ($null -ne $Handoff) { $value.handoff = $Handoff }
    if ($RedactionPaths.Count -gt 0) {
        $value.redactionPaths = ConvertTo-OrdinalStringSet -Values $RedactionPaths
        $value.redactionCount = $value.redactionPaths.Count
    }

    Write-Output ($value | ConvertTo-Json -Compress -Depth 32)
    exit $ExitCode
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)] [string]$WorkingPath,
        [Parameter(Mandatory = $true)] [string[]]$Arguments
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "git"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0"
    [void]$startInfo.ArgumentList.Add("-C")
    [void]$startInfo.ArgumentList.Add($WorkingPath)
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $standardOutputStream = $null
    $standardErrorStream = $null
    $cancellation = $null
    try {
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        $cancellation = [Threading.CancellationTokenSource]::new($script:GitTimeoutMilliseconds)
        [void]$process.Start()
        $standardOutputBuffer = [byte[]]::new($script:MaximumGitOutputBytes + 1)
        $standardErrorBuffer = [byte[]]::new($script:MaximumGitOutputBytes + 1)
        $standardOutputStream = [IO.MemoryStream]::new($standardOutputBuffer, 0, $standardOutputBuffer.Length, $true, $true)
        $standardErrorStream = [IO.MemoryStream]::new($standardErrorBuffer, 0, $standardErrorBuffer.Length, $true, $true)
        $standardOutputStream.SetLength(0)
        $standardErrorStream.SetLength(0)
        $standardOutputTask = $process.StandardOutput.BaseStream.CopyToAsync($standardOutputStream, 4096, $cancellation.Token)
        $standardErrorTask = $process.StandardError.BaseStream.CopyToAsync($standardErrorStream, 4096, $cancellation.Token)
        $exitTask = $process.WaitForExitAsync($cancellation.Token)
        $allTasks = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($exitTask, $standardOutputTask, $standardErrorTask))
        $remainingMilliseconds = [Math]::Max(0, $script:GitTimeoutMilliseconds - [int]$deadline.ElapsedMilliseconds)
        try { $completed = $remainingMilliseconds -gt 0 -and $allTasks.Wait($remainingMilliseconds) } catch { $completed = $false }
        if (-not $completed -or -not $allTasks.IsCompletedSuccessfully -or $standardOutputStream.Length -gt $script:MaximumGitOutputBytes -or $standardErrorStream.Length -gt $script:MaximumGitOutputBytes) {
            try { if (-not $process.HasExited) { $process.Kill($true) } } catch { }
            return [pscustomobject]@{ ExitCode = -1; Output = ""; Bytes = [byte[]]::new(0) }
        }

        $outputBytes = $standardOutputStream.ToArray()
        $output = $script:Utf8.GetString($outputBytes).Replace("`r`n", "`n")
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output; Bytes = $outputBytes }
    }
    catch {
        try { if (-not $process.HasExited) { $process.Kill($true) } } catch { }
        return [pscustomobject]@{ ExitCode = -1; Output = ""; Bytes = [byte[]]::new(0) }
    }
    finally {
        if ($null -ne $cancellation) { $cancellation.Dispose() }
        if ($null -ne $standardOutputStream) { $standardOutputStream.Dispose() }
        if ($null -ne $standardErrorStream) { $standardErrorStream.Dispose() }
        $process.Dispose()
    }
}

function Resolve-RepositoryRoot {
    param([Parameter(Mandatory = $true)] [string]$Candidate, [bool]$WasExplicit)

    $candidatePath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Candidate).ProviderPath)
    $result = Invoke-Git -WorkingPath $candidatePath -Arguments @("rev-parse", "--show-toplevel")
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.Output)) { throw "repository-unavailable" }
    $root = [IO.Path]::GetFullPath($result.Output.Trim())
    if ($WasExplicit) {
        $prefix = Invoke-Git -WorkingPath $candidatePath -Arguments @("rev-parse", "--show-prefix")
        if ($prefix.ExitCode -ne 0 -or -not [string]::IsNullOrEmpty($prefix.Output.Trim())) { throw "repository-root-mismatch" }
    }
    return $root
}

function Test-FixedStatePathsSafe {
    param([Parameter(Mandatory = $true)] [string[]]$Paths)

    foreach ($path in $Paths) {
        $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
    }
    return $true
}

function Assert-NoDuplicateProperties {
    param([Parameter(Mandatory = $true)] [Text.Json.JsonElement]$Element, [string]$Path = "$")

    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw "duplicate-json-property" }
            Assert-NoDuplicateProperties -Element $property.Value -Path "$Path.$($property.Name)"
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) {
            Assert-NoDuplicateProperties -Element $item -Path "$Path[$index]"
            $index++
        }
    }
}

function Read-JsonHashtable {
    param([Parameter(Mandatory = $true)] [byte[]]$Bytes)

    if ($Bytes.Length -gt $script:MaximumDocumentBytes) { throw "document-too-large" }
    $text = $script:Utf8.GetString($Bytes)
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = 32
    $jsonDocument = [Text.Json.JsonDocument]::Parse($text, $options)
    try { Assert-NoDuplicateProperties -Element $jsonDocument.RootElement } finally { $jsonDocument.Dispose() }
    return $text | ConvertFrom-Json -AsHashtable -Depth 32 -NoEnumerate -DateKind String
}

function Protect-StringValue {
    param([Parameter(Mandatory = $true)] [string]$Value, [Parameter(Mandatory = $true)] [string]$Path, [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [Collections.Generic.List[string]]$RedactionPaths)

    $protected = $Value
    foreach ($entry in @(
        @{ Pattern = "(?im)\b(Authorization|Proxy-Authorization)\s*:\s*[^\r\n]+"; Replacement = '$1: [REDACTED]' },
        @{ Pattern = "(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}"; Replacement = '$1 [REDACTED]' },
        @{ Pattern = "(?i)\b([A-Z][A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|AUTH|_KEY)[A-Z0-9_]*)=([^\s;]+)"; Replacement = '$1=[REDACTED]' },
        @{ Pattern = "(?i)\b(github_pat_|gh[pousr]_|sk-)[A-Za-z0-9_-]{12,}"; Replacement = '$1[REDACTED]' }
    )) {
        $updated = [regex]::Replace($protected, $entry.Pattern, $entry.Replacement)
        if (-not [string]::Equals($updated, $protected, [StringComparison]::Ordinal)) { $RedactionPaths.Add($Path) }
        $protected = $updated
    }

    return $protected
}

function Protect-JsonValue {
    param([Parameter(Mandatory = $true)]$Value, [Parameter(Mandatory = $true)] [string]$Path, [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [Collections.Generic.List[string]]$RedactionPaths)

    if ($Value -is [string]) { return Protect-StringValue -Value $Value -Path $Path -RedactionPaths $RedactionPaths }
    if ($Value -is [Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys) { $result[$key] = Protect-JsonValue -Value $Value[$key] -Path "$Path.$key" -RedactionPaths $RedactionPaths }
        return $result
    }
    if ($Value -is [Collections.IEnumerable]) {
        $items = [Collections.Generic.List[object]]::new()
        $index = 0
        foreach ($item in $Value) {
            $items.Add((Protect-JsonValue -Value $item -Path "$Path[$index]" -RedactionPaths $RedactionPaths))
            $index++
        }
        return ,@($items)
    }
    return $Value
}

function ConvertTo-UtcText {
    param([Parameter(Mandatory = $true)] [string]$Value)

    $parsed = [DateTimeOffset]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
    if (-not $Value.EndsWith("Z", [StringComparison]::Ordinal)) { throw "timestamp-not-utc" }
    return $parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture)
}

function ConvertTo-RepositoryIdentity {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary]$Value)

    return [ordered]@{ provider = ([string]$Value.provider).ToLowerInvariant(); host = ([string]$Value.host).ToLowerInvariant(); owner = ([string]$Value.owner).ToLowerInvariant(); name = ([string]$Value.name).ToLowerInvariant(); sourceEvidenceId = [string]$Value.sourceEvidenceId }
}

function ConvertTo-Source {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary]$Value)

    return [ordered]@{ kind = [string]$Value.kind; reference = [string]$Value.reference; observedAtUtc = ConvertTo-UtcText -Value ([string]$Value.observedAtUtc) }
}

function ConvertTo-Budget {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary]$Value, [switch]$Exceptional)

    $result = [ordered]@{ limit = [int]$Value.limit; consumedAtLeast = [int]$Value.consumedAtLeast; historyComplete = [bool]$Value.historyComplete }
    if ($Exceptional) {
        $result.activated = [bool]$Value.activated
        $result.activationDecisionIds = ConvertTo-OrdinalStringSet -Values $Value.activationDecisionIds
        $result.eligibilityEvidenceIds = ConvertTo-OrdinalStringSet -Values $Value.eligibilityEvidenceIds
    }
    return $result
}

function ConvertTo-CanonicalHandoff {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary]$Value)

    $expiry = [ordered]@{ kind = [string]$Value.authorityRecord.expiry.kind }
    if ($expiry.kind -ceq "utc") { $expiry.expiresAtUtc = ConvertTo-UtcText -Value ([string]$Value.authorityRecord.expiry.expiresAtUtc) }
    else { $expiry.goalReference = [string]$Value.authorityRecord.expiry.goalReference }
    $expiry.status = [string]$Value.authorityRecord.expiry.status
    $expiry.terminalEvidenceIds = ConvertTo-OrdinalStringSet -Values $Value.authorityRecord.expiry.terminalEvidenceIds

    $evidence = @($Value.evidence | ForEach-Object {
        $item = [ordered]@{ id = [string]$_.id; kind = [string]$_.kind; reference = [string]$_.reference; observedAtUtc = ConvertTo-UtcText -Value ([string]$_.observedAtUtc) }
        if ($_.Contains("sha256")) { $item.sha256 = ([string]$_.sha256).ToLowerInvariant() }
        $item
    })
    $decisions = @($Value.operativeDecisions | ForEach-Object { [ordered]@{ id = [string]$_.id; statement = [string]$_.statement; source = ConvertTo-Source -Value $_.source } })
    $superseded = @($Value.supersededInstructions | ForEach-Object { [ordered]@{ id = [string]$_.id; summary = [string]$_.summary; source = ConvertTo-Source -Value $_.source; supersededByDecisionId = [string]$_.supersededByDecisionId } })
    [object[]]$protectedWorktrees = @($Value.protectedWorktrees | ForEach-Object { [ordered]@{ path = [IO.Path]::GetFullPath([string]$_.path); pathEvidenceId = [string]$_.pathEvidenceId; repositoryIdentity = ConvertTo-RepositoryIdentity -Value $_.repositoryIdentity; headSha = ([string]$_.headSha).ToLowerInvariant(); statusSha256 = ([string]$_.statusSha256).ToLowerInvariant(); observedAtUtc = ConvertTo-UtcText -Value ([string]$_.observedAtUtc); reason = [string]$_.reason } })
    $protectedWorktrees = Sort-ObjectsByOrdinalPath -Values $protectedWorktrees

    return [ordered]@{
        schemaVersion = 1
        documentRevision = [int]$Value.documentRevision
        updatedAtUtc = ConvertTo-UtcText -Value ([string]$Value.updatedAtUtc)
        objective = [ordered]@{ summary = [string]$Value.objective.summary; sourceDecisionId = [string]$Value.objective.sourceDecisionId }
        activeWork = [ordered]@{ campaign = [int]$Value.activeWork.campaign; phase = [int]$Value.activeWork.phase; unitOfWork = [int]$Value.activeWork.unitOfWork; bolt = [int]$Value.activeWork.bolt; sourceDecisionId = [string]$Value.activeWork.sourceDecisionId }
        repository = [ordered]@{
            identity = ConvertTo-RepositoryIdentity -Value $Value.repository.identity
            worktreePath = [IO.Path]::GetFullPath([string]$Value.repository.worktreePath)
            worktreePathEvidenceId = [string]$Value.repository.worktreePathEvidenceId
            branch = [string]$Value.repository.branch
            baseSha = ([string]$Value.repository.baseSha).ToLowerInvariant()
            headSha = ([string]$Value.repository.headSha).ToLowerInvariant()
            intendedWriteSet = ConvertTo-OrdinalStringSet -Values @($Value.repository.intendedWriteSet | ForEach-Object { ([string]$_).Replace("\", "/") })
        }
        authorityRecord = [ordered]@{
            sourceDecisionIds = ConvertTo-OrdinalStringSet -Values $Value.authorityRecord.sourceDecisionIds
            recordedCapabilities = ConvertTo-OrdinalStringSet -Values $Value.authorityRecord.recordedCapabilities
            expiry = $expiry
            isAuthorityGrant = $false
            requiresIndependentRevalidation = $true
        }
        protectedWorktrees = $protectedWorktrees
        nextAction = [ordered]@{
            kind = [string]$Value.nextAction.kind
            description = [string]$Value.nextAction.description
            sourceDecisionId = [string]$Value.nextAction.sourceDecisionId
            requiredCapabilities = ConvertTo-OrdinalStringSet -Values $Value.nextAction.requiredCapabilities
            requiredBudgetKeys = ConvertTo-OrdinalStringSet -Values $Value.nextAction.requiredBudgetKeys
        }
        budgets = [ordered]@{
            implementationAttempts = ConvertTo-Budget -Value $Value.budgets.implementationAttempts
            gateRuns = ConvertTo-Budget -Value $Value.budgets.gateRuns
            fullReviews = ConvertTo-Budget -Value $Value.budgets.fullReviews
            targetedReviews = ConvertTo-Budget -Value $Value.budgets.targetedReviews
            thirdReviewForNewP0P1 = ConvertTo-Budget -Value $Value.budgets.thirdReviewForNewP0P1 -Exceptional
            clinicalRecoveryExtension = ConvertTo-Budget -Value $Value.budgets.clinicalRecoveryExtension -Exceptional
        }
        lastEvidenceAtUtc = ConvertTo-UtcText -Value ([string]$Value.lastEvidenceAtUtc)
        evidence = $evidence
        operativeDecisions = $decisions
        supersededInstructions = $superseded
    }
}

function ConvertTo-CanonicalBytes {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary]$Value)

    return $script:Utf8.GetBytes(($Value | ConvertTo-Json -Compress -Depth 32) + "`n")
}

function Test-Schema {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary]$Value)

    $json = $Value | ConvertTo-Json -Compress -Depth 32
    return $json | Test-Json -SchemaFile $script:SchemaPath -ErrorAction SilentlyContinue -WarningAction SilentlyContinue
}

function Test-UniqueIds {
    param([Parameter(Mandatory = $true)]$Values)

    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Values) { if (-not $set.Add([string]$value.id)) { return $false } }
    return $true
}

function Test-ReferenceUri {
    param([Parameter(Mandatory = $true)] [string]$Reference)

    if ($Reference -notmatch "^[A-Za-z][A-Za-z0-9+.-]*://") { return $true }
    $uri = $null
    if (-not [Uri]::TryCreate($Reference, [UriKind]::Absolute, [ref]$uri)) { return $false }
    return [string]::IsNullOrEmpty($uri.UserInfo) -and [string]::IsNullOrEmpty($uri.Query) -and [string]::IsNullOrEmpty($uri.Fragment)
}

function Get-SemanticErrors {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary]$Value)

    $errors = [Collections.Generic.List[string]]::new()
    if (-not (Test-UniqueIds -Values $Value.evidence)) { $errors.Add("duplicate_evidence_id") }
    if (-not (Test-UniqueIds -Values $Value.operativeDecisions)) { $errors.Add("duplicate_decision_id") }
    if (-not (Test-UniqueIds -Values $Value.supersededInstructions)) { $errors.Add("duplicate_superseded_id") }

    $evidenceIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $evidenceById = @{}
    foreach ($item in $Value.evidence) {
        [void]$evidenceIds.Add([string]$item.id)
        $evidenceById[[string]$item.id] = $item
        if (-not (Test-ReferenceUri -Reference ([string]$item.reference))) { $errors.Add("unsafe_evidence_reference") }
    }
    $decisionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Value.operativeDecisions) {
        [void]$decisionIds.Add([string]$item.id)
        if (-not (Test-ReferenceUri -Reference ([string]$item.source.reference))) { $errors.Add("unsafe_decision_reference") }
    }

    $supersededIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Value.supersededInstructions) {
        [void]$supersededIds.Add([string]$item.id)
        if ($decisionIds.Contains([string]$item.id)) { $errors.Add("operative_decision_is_superseded") }
    }

    foreach ($decisionReference in @($Value.objective.sourceDecisionId, $Value.activeWork.sourceDecisionId, $Value.nextAction.sourceDecisionId) + @($Value.authorityRecord.sourceDecisionIds)) {
        if (-not $decisionIds.Contains([string]$decisionReference)) { $errors.Add("missing_operative_decision") }
        if ($supersededIds.Contains([string]$decisionReference)) { $errors.Add("superseded_instruction_is_operative") }
    }
    foreach ($item in $Value.supersededInstructions) {
        if (-not $decisionIds.Contains([string]$item.supersededByDecisionId)) { $errors.Add("missing_superseding_decision") }
        if (-not (Test-ReferenceUri -Reference ([string]$item.source.reference))) { $errors.Add("unsafe_superseded_reference") }
    }

    foreach ($evidenceReference in @($Value.repository.identity.sourceEvidenceId, $Value.repository.worktreePathEvidenceId) + @($Value.authorityRecord.expiry.terminalEvidenceIds)) {
        if (-not $evidenceIds.Contains([string]$evidenceReference)) { $errors.Add("missing_evidence") }
    }
    $repositoryIdentityEvidence = $evidenceById[[string]$Value.repository.identity.sourceEvidenceId]
    $expectedRepositoryReference = "$($Value.repository.identity.host)/$($Value.repository.identity.owner)/$($Value.repository.identity.name)"
    if ($null -eq $repositoryIdentityEvidence -or $repositoryIdentityEvidence.kind -cne "repository" -or -not [string]::Equals([string]$repositoryIdentityEvidence.reference, $expectedRepositoryReference, [StringComparison]::OrdinalIgnoreCase)) { $errors.Add("repository_identity_evidence_mismatch") }
    $worktreePathEvidence = $evidenceById[[string]$Value.repository.worktreePathEvidenceId]
    if ($null -eq $worktreePathEvidence -or $worktreePathEvidence.kind -cne "local-path" -or -not [string]::Equals([IO.Path]::GetFullPath([string]$worktreePathEvidence.reference), [IO.Path]::GetFullPath([string]$Value.repository.worktreePath), $script:PathComparison)) { $errors.Add("worktree_path_evidence_mismatch") }
    $protectedPathComparer = if ($script:PathComparison -eq [StringComparison]::OrdinalIgnoreCase) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $protectedPathSet = [Collections.Generic.HashSet[string]]::new($protectedPathComparer)
    foreach ($protected in $Value.protectedWorktrees) {
        foreach ($evidenceReference in @($protected.pathEvidenceId, $protected.repositoryIdentity.sourceEvidenceId)) {
            if (-not $evidenceIds.Contains([string]$evidenceReference)) { $errors.Add("missing_evidence") }
        }
        if (-not $protectedPathSet.Add([IO.Path]::GetFullPath([string]$protected.path))) { $errors.Add("duplicate_protected_worktree") }
        $protectedPathEvidence = $evidenceById[[string]$protected.pathEvidenceId]
        if ($null -eq $protectedPathEvidence -or $protectedPathEvidence.kind -cne "local-path" -or -not [string]::Equals([IO.Path]::GetFullPath([string]$protectedPathEvidence.reference), [IO.Path]::GetFullPath([string]$protected.path), $script:PathComparison)) { $errors.Add("protected_path_evidence_mismatch") }
        $protectedIdentityEvidence = $evidenceById[[string]$protected.repositoryIdentity.sourceEvidenceId]
        $expectedProtectedReference = "$($protected.repositoryIdentity.host)/$($protected.repositoryIdentity.owner)/$($protected.repositoryIdentity.name)"
        if ($null -eq $protectedIdentityEvidence -or $protectedIdentityEvidence.kind -cne "repository" -or -not [string]::Equals([string]$protectedIdentityEvidence.reference, $expectedProtectedReference, [StringComparison]::OrdinalIgnoreCase)) { $errors.Add("protected_identity_evidence_mismatch") }
    }

    if ($Value.authorityRecord.expiry.status -ceq "active" -and @($Value.authorityRecord.expiry.terminalEvidenceIds).Count -ne 0) { $errors.Add("active_authority_has_terminal_evidence") }
    if ($Value.authorityRecord.expiry.status -cne "active" -and @($Value.authorityRecord.expiry.terminalEvidenceIds).Count -eq 0) { $errors.Add("terminal_authority_missing_evidence") }
    if ($Value.budgets.fullReviews.limit -ne 1 -or $Value.budgets.targetedReviews.limit -ne 1) { $errors.Add("ordinary_review_budget_invalid") }

    foreach ($budgetName in @("implementationAttempts", "gateRuns", "fullReviews", "targetedReviews", "thirdReviewForNewP0P1", "clinicalRecoveryExtension")) {
        $budget = $Value.budgets[$budgetName]
        if ($budget.consumedAtLeast -gt $budget.limit) { $errors.Add("budget_consumed_above_limit") }
    }
    foreach ($budgetName in @("thirdReviewForNewP0P1", "clinicalRecoveryExtension")) {
        $budget = $Value.budgets[$budgetName]
        if ($budget.activated) {
            if (@($budget.activationDecisionIds).Count -eq 0 -or @($budget.eligibilityEvidenceIds).Count -eq 0) { $errors.Add("exceptional_budget_missing_activation_evidence") }
            foreach ($id in $budget.activationDecisionIds) { if (-not $decisionIds.Contains([string]$id)) { $errors.Add("missing_operative_decision") } }
            foreach ($id in $budget.eligibilityEvidenceIds) { if (-not $evidenceIds.Contains([string]$id)) { $errors.Add("missing_evidence") } }
        }
        elseif (@($budget.activationDecisionIds).Count -ne 0 -or @($budget.eligibilityEvidenceIds).Count -ne 0) { $errors.Add("inactive_exceptional_budget_has_activation_evidence") }
    }

    $capabilities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($capability in $Value.authorityRecord.recordedCapabilities) { [void]$capabilities.Add([string]$capability) }
    foreach ($capability in $Value.nextAction.requiredCapabilities) { if (-not $capabilities.Contains([string]$capability)) { $errors.Add("capability_not_recorded") } }

    foreach ($path in $Value.repository.intendedWriteSet) {
        $text = [string]$path
        if ($text.StartsWith("/", [StringComparison]::Ordinal) -or $text -match "^[A-Za-z]:" -or $text.Contains("\") -or $text.Contains("//") -or $text.EndsWith("/", [StringComparison]::Ordinal) -or $text -match "(^|/)\.{1,2}(/|$)" -or $text -match "^\.git(/|$)" -or $text -match "^\.aidlc/local(/|$)") { $errors.Add("invalid_intended_path") }
    }

    $previousEvidenceTime = [DateTimeOffset]::MinValue
    foreach ($item in $Value.evidence) {
        $time = [DateTimeOffset]::Parse([string]$item.observedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        if ($time -lt $previousEvidenceTime) { $errors.Add("evidence_chronology_invalid") }
        $previousEvidenceTime = $time
    }
    $previousDecisionTime = [DateTimeOffset]::MinValue
    foreach ($item in $Value.operativeDecisions) {
        $time = [DateTimeOffset]::Parse([string]$item.source.observedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        if ($time -lt $previousDecisionTime) { $errors.Add("decision_chronology_invalid") }
        $previousDecisionTime = $time
    }

    return @($errors | Sort-Object -Unique -CaseSensitive)
}

function Read-ValidatedHandoff {
    param([Parameter(Mandatory = $true)] [string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return [pscustomobject]@{ Exists = $false; Valid = $false; Reasons = @("handoff_missing") } }
    try {
        $bytes = Read-BoundedFileBytes -Path $Path
        $parsed = Read-JsonHashtable -Bytes $bytes
        if (-not $parsed.Contains("schemaVersion") -or [int]$parsed.schemaVersion -ne 1) { return [pscustomobject]@{ Exists = $true; Valid = $false; Reasons = @("schema_unsupported") } }
        if (-not (Test-Schema -Value $parsed)) { return [pscustomobject]@{ Exists = $true; Valid = $false; Reasons = @("handoff_invalid") } }
        $canonical = ConvertTo-CanonicalHandoff -Value $parsed
        $semanticErrors = @(Get-SemanticErrors -Value $canonical)
        if ($semanticErrors.Count -ne 0) { return [pscustomobject]@{ Exists = $true; Valid = $false; Reasons = @("handoff_invalid") } }
        $canonicalBytes = ConvertTo-CanonicalBytes -Value $canonical
        if ($bytes.Length -ne $canonicalBytes.Length -or (Get-Sha256 -Bytes $bytes) -cne (Get-Sha256 -Bytes $canonicalBytes)) { return [pscustomobject]@{ Exists = $true; Valid = $false; Reasons = @("handoff_invalid") } }
        return [pscustomobject]@{ Exists = $true; Valid = $true; Reasons = @(); Bytes = $bytes; Sha256 = Get-Sha256 -Bytes $bytes; Handoff = $canonical }
    }
    catch {
        return [pscustomobject]@{ Exists = $true; Valid = $false; Reasons = @("handoff_invalid") }
    }
}

function Get-RepositoryIdentity {
    param([Parameter(Mandatory = $true)] [string]$Path)

    $result = Invoke-Git -WorkingPath $Path -Arguments @("remote", "get-url", "--all", "origin")
    if ($result.ExitCode -ne 0) { return $null }
    $identities = [Collections.Generic.List[string]]::new()
    foreach ($line in @($result.Output -split "`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $match = [regex]::Match($line.Trim(), "^(?:(?:https?|ssh)://(?:[^/@]+@)?(?<host>[^/:]+)[:/]|(?:[^@/:]+@)?(?<host>[^/:]+):)(?<owner>[^/]+)/(?<name>[^/]+?)(?:\.git)?$")
        if (-not $match.Success) { return $null }
        $identityHost = $match.Groups["host"].Value.ToLowerInvariant()
        $owner = $match.Groups["owner"].Value.ToLowerInvariant()
        $name = $match.Groups["name"].Value.ToLowerInvariant()
        $identities.Add("github|$identityHost|$owner|$name")
    }
    $unique = @($identities | Sort-Object -Unique -CaseSensitive)
    if ($unique.Count -ne 1) { return $null }
    $parts = $unique[0] -split "\|"
    return [ordered]@{ provider = $parts[0]; host = $parts[1]; owner = $parts[2]; name = $parts[3] }
}

function Test-RepositoryIdentityEqual {
    param([Parameter(Mandatory = $true)] [Collections.IDictionary]$Actual, [Parameter(Mandatory = $true)] [Collections.IDictionary]$Expected)

    return $Actual.provider -ceq $Expected.provider -and $Actual.host -ceq $Expected.host -and $Actual.owner -ceq $Expected.owner -and $Actual.name -ceq $Expected.name
}

function Get-WorktreeStatus {
    param([Parameter(Mandatory = $true)] [string]$Path)

    $result = Invoke-Git -WorkingPath $Path -Arguments @("--no-optional-locks", "-c", "core.fsmonitor=false", "status", "--porcelain=v1", "-z", "--untracked-files=all")
    if ($result.ExitCode -ne 0) { return $null }
    $bytes = $result.Bytes
    $paths = [Collections.Generic.List[string]]::new()
    $records = @($result.Output -split "`0")
    for ($index = 0; $index -lt $records.Count; $index++) {
        $record = $records[$index]
        if ([string]::IsNullOrEmpty($record)) { continue }
        if ($record.Length -lt 4) { return $null }
        $status = $record.Substring(0, 2)
        $paths.Add($record.Substring(3).Replace("\", "/"))
        if ($status.Contains("R") -or $status.Contains("C")) {
            $index++
            if ($index -ge $records.Count -or [string]::IsNullOrEmpty($records[$index])) { return $null }
            $paths.Add($records[$index].Replace("\", "/"))
        }
    }
    return [pscustomobject]@{ Sha256 = Get-Sha256 -Bytes $bytes; Paths = @($paths) }
}

function Invoke-HandoffCheck {
    param([Parameter(Mandatory = $true)] [string]$Root, [Parameter(Mandatory = $true)]$Current)

    $handoff = $Current.Handoff
    $reasons = [Collections.Generic.List[string]]::new()
    $normalizedRoot = [IO.Path]::GetFullPath($Root)
    if (-not [string]::Equals($normalizedRoot, [IO.Path]::GetFullPath([string]$handoff.repository.worktreePath), $script:PathComparison)) { $reasons.Add("worktree_mismatch") }

    $identity = Get-RepositoryIdentity -Path $Root
    if ($null -eq $identity -or -not (Test-RepositoryIdentityEqual -Actual $identity -Expected $handoff.repository.identity)) { $reasons.Add("repository_identity_mismatch") }
    $head = Invoke-Git -WorkingPath $Root -Arguments @("rev-parse", "HEAD")
    if ($head.ExitCode -ne 0 -or $head.Output.Trim() -cne $handoff.repository.headSha) { $reasons.Add("repository_head_mismatch") }
    $branch = Invoke-Git -WorkingPath $Root -Arguments @("symbolic-ref", "--short", "-q", "HEAD")
    if ($branch.ExitCode -ne 0 -or $branch.Output.Trim() -cne $handoff.repository.branch) { $reasons.Add("branch_mismatch") }
    $baseCommit = Invoke-Git -WorkingPath $Root -Arguments @("cat-file", "-e", "$($handoff.repository.baseSha)^{commit}")
    $headCommit = Invoke-Git -WorkingPath $Root -Arguments @("cat-file", "-e", "$($handoff.repository.headSha)^{commit}")
    $ancestry = Invoke-Git -WorkingPath $Root -Arguments @("merge-base", "--is-ancestor", $handoff.repository.baseSha, $handoff.repository.headSha)
    if ($baseCommit.ExitCode -ne 0 -or $headCommit.ExitCode -ne 0 -or $ancestry.ExitCode -ne 0) { $reasons.Add("base_not_ancestor") }

    $status = Get-WorktreeStatus -Path $Root
    if ($null -eq $status) { $reasons.Add("worktree_scope_conflict") }
    else {
        $allowedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($path in $handoff.repository.intendedWriteSet) { [void]$allowedPaths.Add([string]$path) }
        foreach ($path in $status.Paths) { if (-not $allowedPaths.Contains([string]$path)) { $reasons.Add("worktree_scope_conflict") } }
    }

    $expiry = $handoff.authorityRecord.expiry
    if ($expiry.status -ceq "completed") { $reasons.Add("authority_scope_completed") }
    elseif ($expiry.status -ceq "revoked") { $reasons.Add("authority_revoked") }
    elseif ($expiry.kind -ceq "utc") {
        $expiresAt = [DateTimeOffset]::Parse([string]$expiry.expiresAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        if ([DateTimeOffset]::UtcNow -ge $expiresAt) { $reasons.Add("authority_expired") }
    }

    $capabilities = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($capability in $handoff.authorityRecord.recordedCapabilities) { [void]$capabilities.Add([string]$capability) }
    foreach ($capability in $handoff.nextAction.requiredCapabilities) { if (-not $capabilities.Contains([string]$capability)) { $reasons.Add("capability_not_recorded") } }
    foreach ($budgetName in $handoff.nextAction.requiredBudgetKeys) {
        $budget = $handoff.budgets[[string]$budgetName]
        if (-not $budget.historyComplete) { $reasons.Add("budget_history_incomplete") }
        if ($budget.consumedAtLeast -ge $budget.limit) { $reasons.Add("budget_exhausted") }
        if ($budgetName -cin @("thirdReviewForNewP0P1", "clinicalRecoveryExtension") -and -not $budget.activated) { $reasons.Add("exceptional_budget_not_activated") }
    }

    $pathComparer = if ($script:PathComparison -eq [StringComparison]::OrdinalIgnoreCase) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $approvedPaths = [Collections.Generic.HashSet[string]]::new($pathComparer)
    foreach ($path in $ApprovedProtectedWorktreePath) { [void]$approvedPaths.Add([IO.Path]::GetFullPath($path)) }
    foreach ($protected in $handoff.protectedWorktrees) {
        $protectedPath = [IO.Path]::GetFullPath([string]$protected.path)
        if ([string]::Equals($protectedPath, $normalizedRoot, $script:PathComparison)) { $reasons.Add("protected_worktree_selected") }
        if (-not $approvedPaths.Contains($protectedPath)) {
            $reasons.Add("protected_worktree_read_not_authorized")
            continue
        }
        if (-not (Test-Path -LiteralPath $protectedPath -PathType Container)) {
            $reasons.Add("protected_worktree_unavailable")
            continue
        }
        $protectedTopLevel = Invoke-Git -WorkingPath $protectedPath -Arguments @("rev-parse", "--show-toplevel")
        if ($protectedTopLevel.ExitCode -ne 0 -or -not [string]::Equals([IO.Path]::GetFullPath($protectedTopLevel.Output.Trim()), $protectedPath, $script:PathComparison)) {
            $reasons.Add("protected_worktree_unavailable")
            continue
        }
        $protectedIdentity = Get-RepositoryIdentity -Path $protectedPath
        if ($null -eq $protectedIdentity -or -not (Test-RepositoryIdentityEqual -Actual $protectedIdentity -Expected $protected.repositoryIdentity)) { $reasons.Add("protected_repository_identity_mismatch") }
        $protectedHead = Invoke-Git -WorkingPath $protectedPath -Arguments @("rev-parse", "HEAD")
        if ($protectedHead.ExitCode -ne 0 -or $protectedHead.Output.Trim() -cne $protected.headSha) { $reasons.Add("protected_worktree_head_changed") }
        $protectedStatus = Get-WorktreeStatus -Path $protectedPath
        if ($null -eq $protectedStatus -or $protectedStatus.Sha256 -cne $protected.statusSha256) { $reasons.Add("protected_worktree_status_changed") }
    }

    $uniqueReasons = @($reasons | Sort-Object -Unique -CaseSensitive)
    if ($uniqueReasons.Count -eq 0) { Write-DeliveryResult -Result "current" -ExitCode 0 -DocumentSha256 $Current.Sha256 }
    Write-DeliveryResult -Result "paused" -ExitCode 2 -Reasons $uniqueReasons -DocumentSha256 $Current.Sha256
}

try {
    if (-not (Test-Path -LiteralPath $script:SchemaPath -PathType Leaf)) { throw "schema-missing" }
    $wasExplicit = $PSBoundParameters.ContainsKey("RepositoryRoot")
    $root = Resolve-RepositoryRoot -Candidate $RepositoryRoot -WasExplicit $wasExplicit
    $localDirectory = Join-Path $root ".aidlc/local"
    $handoffPath = Join-Path $localDirectory "delivery-handoff.json"
    $lockPath = Join-Path $localDirectory "delivery-handoff.lock"
    $fixedStatePaths = @((Join-Path $root ".aidlc"), $localDirectory, $handoffPath, $lockPath)
    if (-not (Test-FixedStatePathsSafe -Paths $fixedStatePaths)) { Write-DeliveryResult -Result "paused" -ExitCode 2 -Reasons @("unsafe_local_state_path") }

    if ($Operation -ceq "Read" -or $Operation -ceq "Check") {
        $current = Read-ValidatedHandoff -Path $handoffPath
        if (-not $current.Valid) { Write-DeliveryResult -Result "paused" -ExitCode 2 -Reasons $current.Reasons }
        if ($Operation -ceq "Read") { Write-DeliveryResult -Result "read" -ExitCode 0 -DocumentSha256 $current.Sha256 -Handoff $current.Handoff }
        Invoke-HandoffCheck -Root $root -Current $current
    }

    if ([string]::IsNullOrWhiteSpace($InputPath) -or [string]::IsNullOrWhiteSpace($ExpectedDocumentSha256)) { Write-DeliveryResult -Result "invalid" -ExitCode 1 -Reasons @("write_arguments_required") }
    if ($ExpectedDocumentSha256 -cne "absent" -and $ExpectedDocumentSha256 -cnotmatch "^[0-9a-f]{64}$") { Write-DeliveryResult -Result "invalid" -ExitCode 1 -Reasons @("expected_hash_invalid") }
    if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) { Write-DeliveryResult -Result "invalid" -ExitCode 1 -Reasons @("input_missing") }

    try {
        $inputBytes = Read-BoundedFileBytes -Path ([IO.Path]::GetFullPath($InputPath))
        $inputValue = Read-JsonHashtable -Bytes $inputBytes
        $redactionPaths = [Collections.Generic.List[string]]::new()
        $protectedValue = Protect-JsonValue -Value $inputValue -Path "$" -RedactionPaths $redactionPaths
        if (-not (Test-Schema -Value $protectedValue)) { Write-DeliveryResult -Result "invalid" -ExitCode 1 -Reasons @("handoff_invalid") }
        $canonical = ConvertTo-CanonicalHandoff -Value $protectedValue
        $semanticErrors = @(Get-SemanticErrors -Value $canonical)
        if ($semanticErrors.Count -ne 0) { Write-DeliveryResult -Result "invalid" -ExitCode 1 -Reasons $semanticErrors }
        $canonicalBytes = ConvertTo-CanonicalBytes -Value $canonical
        if ($canonicalBytes.Length -gt $script:MaximumDocumentBytes) { Write-DeliveryResult -Result "invalid" -ExitCode 1 -Reasons @("document_too_large") }
    }
    catch {
        Write-DeliveryResult -Result "invalid" -ExitCode 1 -Reasons @("handoff_invalid")
    }

    $aidlcDirectory = Join-Path $root ".aidlc"
    [void](New-Item -ItemType Directory -Path $localDirectory -Force)
    if (-not (Test-FixedStatePathsSafe -Paths $fixedStatePaths)) { Write-DeliveryResult -Result "paused" -ExitCode 2 -Reasons @("unsafe_local_state_path") }

    $lockStream = $null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $lockStream -and $stopwatch.ElapsedMilliseconds -lt $LockTimeoutMilliseconds) {
        try { $lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] { Start-Sleep -Milliseconds 25 }
    }
    if ($null -eq $lockStream) { Write-DeliveryResult -Result "busy" -ExitCode 2 -Reasons @("handoff_busy") }

    $tempPath = $null
    try {
        $current = Read-ValidatedHandoff -Path $handoffPath
        if ($current.Exists -and -not $current.Valid) { Write-DeliveryResult -Result "paused" -ExitCode 2 -Reasons @("current_handoff_invalid") }
        $actualHash = if ($current.Exists) { $current.Sha256 } else { "absent" }
        if ($actualHash -cne $ExpectedDocumentSha256) { Write-DeliveryResult -Result "conflict" -ExitCode 2 -Reasons @("cas_conflict") -DocumentSha256 $(if ($actualHash -ceq "absent") { $null } else { $actualHash }) }
        $expectedRevision = if ($current.Exists) { [int]$current.Handoff.documentRevision + 1 } else { 1 }
        if ($canonical.documentRevision -ne $expectedRevision) { Write-DeliveryResult -Result "conflict" -ExitCode 2 -Reasons @("revision_conflict") -DocumentSha256 $(if ($actualHash -ceq "absent") { $null } else { $actualHash }) }

        $tempPath = Join-Path $localDirectory ("delivery-handoff.{0}.tmp" -f [Guid]::NewGuid().ToString("N"))
        $writeStream = [IO.FileStream]::new($tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try {
            $writeStream.Write($canonicalBytes, 0, $canonicalBytes.Length)
            $writeStream.Flush($true)
        }
        finally { $writeStream.Dispose() }
        [IO.File]::Move($tempPath, $handoffPath, $true)
        $tempPath = $null
        $written = Read-ValidatedHandoff -Path $handoffPath
        if (-not $written.Valid -or $written.Sha256 -cne (Get-Sha256 -Bytes $canonicalBytes)) { throw "write-readback-failed" }
        Write-DeliveryResult -Result "written" -ExitCode 0 -DocumentSha256 $written.Sha256 -RedactionPaths @($redactionPaths)
    }
    finally {
        if ($null -ne $lockStream) { $lockStream.Dispose() }
        if ($null -ne $tempPath -and (Test-Path -LiteralPath $tempPath -PathType Leaf)) { Remove-Item -LiteralPath $tempPath -Force }
    }
}
catch {
    Write-DeliveryResult -Result "error" -ExitCode 1 -Reasons @("operation_failed")
}
