Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$handoffScriptPath = Join-Path $repoRoot "scripts/delivery-handoff.ps1"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("embodysense-delivery-handoff-{0}" -f [Guid]::NewGuid().ToString("N"))
$powershellPath = (Get-Process -Id $PID).Path
$assertionCount = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:assertionCount++
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)
    Assert-True -Condition ($Actual -ceq $Expected) -Message "$Message Expected '$Expected'. Actual '$Actual'."
}

function Assert-Contains {
    param([object[]]$Actual, [string]$Expected, [string]$Message)
    Assert-True -Condition (@($Actual) -ccontains $Expected) -Message "$Message Missing '$Expected'. Actual '$(@($Actual) -join ',')'."
}

function Assert-NotContainsText {
    param([string]$Actual, [string]$Unexpected, [string]$Message)
    Assert-True -Condition ($Actual.IndexOf($Unexpected, [StringComparison]::Ordinal) -lt 0) -Message "$Message Unexpected '$Unexpected'."
}

function Get-Sha256 {
    param([byte[]]$Bytes)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Invoke-ProcessText {
    param([string]$FileName, [string[]]$Arguments, [hashtable]$Environment = @{})

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $startInfo.Environment[$key] = [string]$Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) { $process.Kill($true); throw "Process timed out: $FileName" }
        $output = $outputTask.GetAwaiter().GetResult()
        $errorOutput = $errorTask.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output; Error = $errorOutput }
    }
    finally { $process.Dispose() }
}

function Invoke-GitTest {
    param([string]$WorkingPath, [string[]]$Arguments)
    $result = Invoke-ProcessText -FileName "git" -Arguments (@("-C", $WorkingPath) + $Arguments)
    if ($result.ExitCode -ne 0) { throw "git failed: $($Arguments -join ' ')" }
    return $result.Output.Trim()
}

function Get-StatusSha256 {
    param([string]$WorkingPath)
    $result = Invoke-ProcessText -FileName "git" -Arguments @("-C", $WorkingPath, "--no-optional-locks", "-c", "core.fsmonitor=false", "status", "--porcelain=v1", "-z", "--untracked-files=all")
    if ($result.ExitCode -ne 0) { throw "git status failed" }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($result.Output.Replace("`r`n", "`n"))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Write-Input {
    param([string]$Name, [Collections.IDictionary]$Value)
    $path = Join-Path $tempRoot "$Name.json"
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
    return $path
}

function Copy-Handoff {
    param([Collections.IDictionary]$Value)
    return (($Value | ConvertTo-Json -Compress -Depth 32) | ConvertFrom-Json -AsHashtable -Depth 32 -NoEnumerate -DateKind String)
}

function Set-Revision {
    param([Collections.IDictionary]$Value, [int]$Revision)
    $Value.documentRevision = $Revision
    $Value.updatedAtUtc = ([DateTimeOffset]::Parse("2026-09-05T22:00:00Z").AddSeconds($Revision)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
}

function Invoke-Handoff {
    param(
        [string]$Operation,
        [string]$RepositoryRoot,
        [string]$InputPath,
        [string]$ExpectedHash,
        [string[]]$ApprovedPaths = @(),
        [hashtable]$Environment = @{}
    )

    $arguments = @("-NoProfile", "-File", $handoffScriptPath, "-Operation", $Operation, "-RepositoryRoot", $RepositoryRoot, "-LockTimeoutMilliseconds", "250")
    if (-not [string]::IsNullOrEmpty($InputPath)) { $arguments += @("-InputPath", $InputPath) }
    if (-not [string]::IsNullOrEmpty($ExpectedHash)) { $arguments += @("-ExpectedDocumentSha256", $ExpectedHash) }
    if ($ApprovedPaths.Count -gt 0) { $arguments += @("-ApprovedProtectedWorktreePath") + $ApprovedPaths }
    $processResult = Invoke-ProcessText -FileName $powershellPath -Arguments $arguments -Environment $Environment
    $parsed = if ([string]::IsNullOrWhiteSpace($processResult.Output)) { $null } else { $processResult.Output.Trim() | ConvertFrom-Json -Depth 32 }
    return [pscustomobject]@{ ExitCode = $processResult.ExitCode; Output = $processResult.Output; Error = $processResult.Error; Value = $parsed }
}

function Start-HandoffWrite {
    param([string]$RepositoryRoot, [string]$InputPath, [string]$ExpectedHash)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $powershellPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @("-NoProfile", "-File", $handoffScriptPath, "-Operation", "Write", "-RepositoryRoot", $RepositoryRoot, "-InputPath", $InputPath, "-ExpectedDocumentSha256", $ExpectedHash, "-LockTimeoutMilliseconds", "1000")) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    return [pscustomobject]@{ Process = $process; OutputTask = $process.StandardOutput.ReadToEndAsync(); ErrorTask = $process.StandardError.ReadToEndAsync() }
}

function Receive-HandoffWrite {
    param([object]$Running)

    try {
        if (-not $Running.Process.WaitForExit(10000)) { $Running.Process.Kill($true); throw "Concurrent handoff writer timed out." }
        $output = $Running.OutputTask.GetAwaiter().GetResult()
        $errorOutput = $Running.ErrorTask.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $Running.Process.ExitCode; Output = $output; Error = $errorOutput; Value = ($output.Trim() | ConvertFrom-Json -Depth 32) }
    }
    finally { $Running.Process.Dispose() }
}

function New-FakeGitDirectory {
    if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { return $null }

    $directory = Join-Path $tempRoot "fake-git"
    [void](New-Item -ItemType Directory -Path $directory)
    $actualGit = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    $scriptText = @'
#!/bin/sh
if [ -n "$FAKE_GIT_OPTIONAL_LOCKS_LOG" ]; then
  printf '%s\n' "$GIT_OPTIONAL_LOCKS" >> "$FAKE_GIT_OPTIONAL_LOCKS_LOG"
fi
case " $* " in
  *" remote get-url "*)
    case "$FAKE_GIT_MODE" in
      live-overrun)
        sleep 10
        exit 0
        ;;
      descendant-pipe)
        (sleep 4) &
        exit 0
        ;;
      output-overflow)
        /usr/bin/yes GIT_OVERFLOW_PRIVATE_SENTINEL | /usr/bin/head -c 70000
        exit 0
        ;;
    esac
    ;;
esac
exec __ACTUAL_GIT__ "$@"
'@
    $scriptText = $scriptText.Replace("__ACTUAL_GIT__", $actualGit)
    $path = Join-Path $directory "git"
    [IO.File]::WriteAllText($path, $scriptText, [Text.UTF8Encoding]::new($false))
    $chmod = Invoke-ProcessText -FileName "/bin/chmod" -Arguments @("+x", $path)
    if ($chmod.ExitCode -ne 0) { throw "Failed to make fake Git executable." }
    return $directory
}

function New-Handoff {
    param([string]$ActivePath, [string]$HeadSha)

    return [ordered]@{
        schemaVersion = 1
        documentRevision = 1
        updatedAtUtc = "2026-09-05T22:00:01Z"
        objective = [ordered]@{ summary = "Complete the bounded delivery goal"; sourceDecisionId = "decision-current-goal" }
        activeWork = [ordered]@{ campaign = 784; phase = 791; unitOfWork = 801; bolt = 819; sourceDecisionId = "decision-current-goal" }
        repository = [ordered]@{
            identity = [ordered]@{ provider = "github"; host = "github.com"; owner = "jacob-j-thomas"; name = "agenthome-poc"; sourceEvidenceId = "repository-identity" }
            worktreePath = $ActivePath
            worktreePathEvidenceId = "active-private-clone"
            branch = "codex/test-intent"
            baseSha = $HeadSha
            headSha = $HeadSha
            intendedWriteSet = @("allowed.txt")
        }
        authorityRecord = [ordered]@{
            sourceDecisionIds = @("decision-execution-authority")
            recordedCapabilities = @("source-write")
            expiry = [ordered]@{ kind = "until-goal-completion-or-revocation"; goalReference = "Complete accepted test goal"; status = "active"; terminalEvidenceIds = @() }
            isAuthorityGrant = $false
            requiresIndependentRevalidation = $true
        }
        protectedWorktrees = @()
        nextAction = [ordered]@{ kind = "implement"; description = "Implement the bounded patch"; sourceDecisionId = "decision-current-goal"; requiredCapabilities = @("source-write"); requiredBudgetKeys = @("implementationAttempts") }
        budgets = [ordered]@{
            implementationAttempts = [ordered]@{ limit = 3; consumedAtLeast = 0; historyComplete = $true }
            gateRuns = [ordered]@{ limit = 3; consumedAtLeast = 0; historyComplete = $true }
            fullReviews = [ordered]@{ limit = 1; consumedAtLeast = 0; historyComplete = $true }
            targetedReviews = [ordered]@{ limit = 1; consumedAtLeast = 0; historyComplete = $true }
            thirdReviewForNewP0P1 = [ordered]@{ limit = 1; consumedAtLeast = 0; historyComplete = $true; activated = $false; activationDecisionIds = @(); eligibilityEvidenceIds = @() }
            clinicalRecoveryExtension = [ordered]@{ limit = 1; consumedAtLeast = 0; historyComplete = $true; activated = $false; activationDecisionIds = @(); eligibilityEvidenceIds = @() }
        }
        lastEvidenceAtUtc = "2026-09-05T21:55:00Z"
        evidence = @(
            [ordered]@{ id = "repository-identity"; kind = "repository"; reference = "github.com/Jacob-J-Thomas/agenthome-poc"; observedAtUtc = "2026-09-05T21:54:00Z" },
            [ordered]@{ id = "active-private-clone"; kind = "local-path"; reference = $ActivePath; observedAtUtc = "2026-09-05T21:55:00Z" }
        )
        operativeDecisions = @(
            [ordered]@{ id = "decision-current-goal"; statement = "Current bounded objective"; source = [ordered]@{ kind = "user-instruction"; reference = "current execution prompt"; observedAtUtc = "2026-09-05T21:50:00Z" } },
            [ordered]@{ id = "decision-execution-authority"; statement = "Authority lasts until goal completion or revocation"; source = [ordered]@{ kind = "user-instruction"; reference = "current execution prompt"; observedAtUtc = "2026-09-05T21:50:00Z" } }
        )
        supersededInstructions = @()
    }
}

try {
    [void](New-Item -ItemType Directory -Path $tempRoot)
    $originPath = Join-Path $tempRoot "origin.git"
    $activePath = Join-Path $tempRoot "active"
    $protectedPath = Join-Path $tempRoot "protected"
    $init = Invoke-ProcessText -FileName "git" -Arguments @("init", "--bare", "--initial-branch", "main", $originPath)
    Assert-Equal -Actual $init.ExitCode -Expected 0 -Message "Bare origin initialization failed."
    $clone = Invoke-ProcessText -FileName "git" -Arguments @("clone", $originPath, $activePath)
    Assert-Equal -Actual $clone.ExitCode -Expected 0 -Message "Active clone failed."
    $activePath = [IO.Path]::GetFullPath((Invoke-GitTest -WorkingPath $activePath -Arguments @("rev-parse", "--show-toplevel")))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("config", "user.email", "delivery-handoff@example.invalid"))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("config", "user.name", "Delivery Handoff Test"))
    [IO.File]::WriteAllText((Join-Path $activePath "README.md"), "fixture`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $activePath ".gitignore"), "/.aidlc/local/`n", [Text.UTF8Encoding]::new($false))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("add", "README.md", ".gitignore"))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("commit", "-m", "fixture"))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("branch", "-M", "codex/test-intent"))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("push", "origin", "HEAD:main"))
    $protectedClone = Invoke-ProcessText -FileName "git" -Arguments @("clone", $originPath, $protectedPath)
    Assert-Equal -Actual $protectedClone.ExitCode -Expected 0 -Message "Protected clone failed."
    $protectedPath = [IO.Path]::GetFullPath((Invoke-GitTest -WorkingPath $protectedPath -Arguments @("rev-parse", "--show-toplevel")))
    [void](Invoke-GitTest -WorkingPath $protectedPath -Arguments @("config", "user.email", "delivery-handoff@example.invalid"))
    [void](Invoke-GitTest -WorkingPath $protectedPath -Arguments @("config", "user.name", "Delivery Handoff Test"))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("remote", "set-url", "origin", "https://github.com/Jacob-J-Thomas/agenthome-poc.git"))
    [void](Invoke-GitTest -WorkingPath $protectedPath -Arguments @("remote", "set-url", "origin", "https://github.com/Jacob-J-Thomas/agenthome-poc.git"))

    $headSha = Invoke-GitTest -WorkingPath $activePath -Arguments @("rev-parse", "HEAD")
    $handoff = New-Handoff -ActivePath $activePath -HeadSha $headSha
    $handoff.nextAction.kind = "stop"
    $handoff.nextAction.description = "Await obsolete human GO"
    $handoff.nextAction.requiredCapabilities = @()
    $handoff.nextAction.requiredBudgetKeys = @()
    $initialInput = Write-Input -Name "initial" -Value $handoff
    $initialWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath $initialInput -ExpectedHash "absent"
    Assert-Equal -Actual $initialWrite.ExitCode -Expected 0 -Message "Initial write failed. $($initialWrite.Output) $($initialWrite.Error)"
    Assert-Equal -Actual $initialWrite.Value.result -Expected "written" -Message "Initial write result was wrong."
    $hash1 = [string]$initialWrite.Value.documentSha256
    $handoffPath = Join-Path $activePath ".aidlc/local/delivery-handoff.json"
    $canonical1 = [IO.File]::ReadAllBytes($handoffPath)

    [IO.File]::Delete($handoffPath)
    $deterministicWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath $initialInput -ExpectedHash "absent"
    Assert-Equal -Actual $deterministicWrite.ExitCode -Expected 0 -Message "Deterministic replay write failed."
    Assert-True -Condition ($canonical1.Length -eq ([IO.File]::ReadAllBytes($handoffPath)).Length) -Message "Canonical replay length changed."
    Assert-Equal -Actual ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($handoffPath)))) -Expected ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($canonical1))) -Message "Canonical replay bytes changed."
    Assert-True -Condition ($canonical1[0] -ne 0xEF) -Message "Canonical JSON unexpectedly used a UTF-8 BOM."
    Assert-Equal -Actual $canonical1[-1] -Expected 10 -Message "Canonical JSON must end with one LF."

    $revision2 = Copy-Handoff -Value $handoff
    Set-Revision -Value $revision2 -Revision 2
    $revision2.nextAction.kind = "implement"
    $revision2.nextAction.description = "Implement current intent"
    $revision2.nextAction.requiredCapabilities = @("source-write")
    $revision2.nextAction.requiredBudgetKeys = @("implementationAttempts")
    $revision2.supersededInstructions = @([ordered]@{ id = "old-planning-pause"; summary = "Stop after planning and await human GO"; source = [ordered]@{ kind = "historical-instruction"; reference = "prior task"; observedAtUtc = "2026-09-04T00:00:00Z" }; supersededByDecisionId = "decision-current-goal" })
    $revision2Input = Write-Input -Name "revision2" -Value $revision2
    $revision2Write = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath $revision2Input -ExpectedHash $hash1
    Assert-Equal -Actual $revision2Write.ExitCode -Expected 0 -Message "Revision 2 CAS failed. $($revision2Write.Output) $($revision2Write.Error)"
    $hash2 = [string]$revision2Write.Value.documentSha256
    $revision2Bytes = [IO.File]::ReadAllBytes($handoffPath)

    $stalePause = Copy-Handoff -Value $handoff
    Set-Revision -Value $stalePause -Revision 2
    $staleInput = Write-Input -Name "stale-pause" -Value $stalePause
    $staleWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath $staleInput -ExpectedHash $hash1
    Assert-Equal -Actual $staleWrite.ExitCode -Expected 2 -Message "Stale pause writer did not stop."
    Assert-Contains -Actual $staleWrite.Value.reasons -Expected "cas_conflict" -Message "Stale pause conflict reason was absent."
    Assert-Equal -Actual (Get-Sha256 -Bytes ([IO.File]::ReadAllBytes($handoffPath))) -Expected (Get-Sha256 -Bytes $revision2Bytes) -Message "Stale pause changed winning bytes."

    $read = Invoke-Handoff -Operation "Read" -RepositoryRoot $activePath
    Assert-Equal -Actual $read.ExitCode -Expected 0 -Message "Read failed."
    Assert-Equal -Actual $read.Value.handoff.documentRevision -Expected 2 -Message "Read did not expose winning revision."
    Assert-Equal -Actual $read.Value.handoff.nextAction.kind -Expected "implement" -Message "Superseded pause became active."
    Assert-True -Condition (-not $read.Value.authorityIsGrant -and $read.Value.requiresIndependentAuthorityRevalidation) -Message "Read projected authority as a grant."
    $check = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Equal -Actual $check.ExitCode -Expected 0 -Message "Current handoff check failed. $($check.Output) $($check.Error)"

    $badExtra = Copy-Handoff -Value $revision2
    $badExtra.unexpected = "GITHUB_TOKEN=must-not-leak"
    Set-Revision -Value $badExtra -Revision 3
    $badExtraResult = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "bad-extra" -Value $badExtra) -ExpectedHash $hash2
    Assert-Equal -Actual $badExtraResult.ExitCode -Expected 1 -Message "Extra property was accepted."
    Assert-NotContainsText -Actual ($badExtraResult.Output + $badExtraResult.Error) -Unexpected "must-not-leak" -Message "Invalid output leaked a secret."
    Assert-Equal -Actual (Get-Sha256 -Bytes ([IO.File]::ReadAllBytes($handoffPath))) -Expected (Get-Sha256 -Bytes $revision2Bytes) -Message "Invalid input changed current bytes."

    $duplicatePath = Join-Path $tempRoot "duplicate.json"
    [IO.File]::WriteAllText($duplicatePath, '{"schemaVersion":1,"schemaVersion":1}', [Text.UTF8Encoding]::new($false))
    $duplicateResult = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath $duplicatePath -ExpectedHash $hash2
    Assert-Equal -Actual $duplicateResult.ExitCode -Expected 1 -Message "Duplicate JSON property was accepted."

    $badPath = Copy-Handoff -Value $revision2
    Set-Revision -Value $badPath -Revision 3
    $badPath.repository.intendedWriteSet = @("../escape")
    $badPathResult = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "bad-path" -Value $badPath) -ExpectedHash $hash2
    Assert-Equal -Actual $badPathResult.ExitCode -Expected 1 -Message "Traversal path was accepted."

    $oversizedInputPath = Join-Path $tempRoot "oversized-input.json"
    $oversizedInputStream = [IO.File]::Open($oversizedInputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $oversizedInputStream.SetLength(262145) } finally { $oversizedInputStream.Dispose() }
    $oversizedInputResult = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath $oversizedInputPath -ExpectedHash $hash2
    Assert-Equal -Actual $oversizedInputResult.ExitCode -Expected 1 -Message "Oversized sparse input was accepted."
    Assert-Equal -Actual (Get-Sha256 -Bytes ([IO.File]::ReadAllBytes($handoffPath))) -Expected (Get-Sha256 -Bytes $revision2Bytes) -Message "Oversized input changed current bytes."

    $savedBytes = [IO.File]::ReadAllBytes($handoffPath)
    [IO.File]::WriteAllText($handoffPath, "{broken", [Text.UTF8Encoding]::new($false))
    $invalidCurrent = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath $revision2Input -ExpectedHash $hash2
    Assert-Equal -Actual $invalidCurrent.ExitCode -Expected 2 -Message "Invalid current handoff was overwritten."
    Assert-Contains -Actual $invalidCurrent.Value.reasons -Expected "current_handoff_invalid" -Message "Invalid current reason was absent."
    Assert-Equal -Actual ([IO.File]::ReadAllText($handoffPath)) -Expected "{broken" -Message "Invalid current bytes changed."
    [IO.File]::WriteAllBytes($handoffPath, $savedBytes)

    $oversizedCurrentStream = [IO.File]::Open($handoffPath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $oversizedCurrentStream.SetLength(262145) } finally { $oversizedCurrentStream.Dispose() }
    $oversizedRead = Invoke-Handoff -Operation "Read" -RepositoryRoot $activePath
    Assert-Equal -Actual $oversizedRead.ExitCode -Expected 2 -Message "Oversized current handoff did not pause Read."
    Assert-Contains -Actual $oversizedRead.Value.reasons -Expected "handoff_invalid" -Message "Oversized current Read reason was absent."
    $oversizedCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Equal -Actual $oversizedCheck.ExitCode -Expected 2 -Message "Oversized current handoff did not pause Check."
    Assert-Contains -Actual $oversizedCheck.Value.reasons -Expected "handoff_invalid" -Message "Oversized current Check reason was absent."
    [IO.File]::WriteAllBytes($handoffPath, $savedBytes)

    [IO.File]::WriteAllText((Join-Path $activePath "outside.txt"), "outside`n", [Text.UTF8Encoding]::new($false))
    $outsideCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Equal -Actual $outsideCheck.ExitCode -Expected 2 -Message "Outside dirty path did not pause."
    Assert-Contains -Actual $outsideCheck.Value.reasons -Expected "worktree_scope_conflict" -Message "Outside dirty path reason was absent."
    [IO.File]::Delete((Join-Path $activePath "outside.txt"))
    [IO.File]::WriteAllText((Join-Path $activePath "allowed.txt"), "allowed`n", [Text.UTF8Encoding]::new($false))
    $allowedCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Equal -Actual $allowedCheck.ExitCode -Expected 0 -Message "Intended dirty path was rejected."
    [IO.File]::Delete((Join-Path $activePath "allowed.txt"))

    [IO.File]::WriteAllText((Join-Path $activePath "head-change.txt"), "head changed`n", [Text.UTF8Encoding]::new($false))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("add", "head-change.txt"))
    [void](Invoke-GitTest -WorkingPath $activePath -Arguments @("commit", "-m", "advance head"))
    $staleHeadCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Equal -Actual $staleHeadCheck.ExitCode -Expected 2 -Message "Stale repository head did not pause."
    Assert-Contains -Actual $staleHeadCheck.Value.reasons -Expected "repository_head_mismatch" -Message "Stale head reason was absent."
    $advancedHead = Invoke-GitTest -WorkingPath $activePath -Arguments @("rev-parse", "HEAD")
    $currentHead = Copy-Handoff -Value $revision2
    Set-Revision -Value $currentHead -Revision 3
    $currentHead.repository.headSha = $advancedHead
    $currentHeadWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "current-head" -Value $currentHead) -ExpectedHash $hash2
    Assert-Equal -Actual $currentHeadWrite.ExitCode -Expected 0 -Message "Current head update did not persist."
    $hash3 = [string]$currentHeadWrite.Value.documentSha256
    Assert-Equal -Actual (Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath).ExitCode -Expected 0 -Message "Updated exact head did not validate."

    $expired = Copy-Handoff -Value $currentHead
    Set-Revision -Value $expired -Revision 4
    $expired.authorityRecord.expiry = [ordered]@{ kind = "utc"; expiresAtUtc = "2000-01-01T00:00:00Z"; status = "active"; terminalEvidenceIds = @() }
    $expiredWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "expired" -Value $expired) -ExpectedHash $hash3
    Assert-Equal -Actual $expiredWrite.ExitCode -Expected 0 -Message "UTC expiry variant did not write."
    $hash4 = [string]$expiredWrite.Value.documentSha256
    $expiredCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Contains -Actual $expiredCheck.Value.reasons -Expected "authority_expired" -Message "Expired authority did not pause."

    $missingTerminal = Copy-Handoff -Value $expired
    Set-Revision -Value $missingTerminal -Revision 5
    $missingTerminal.authorityRecord.expiry = [ordered]@{ kind = "until-goal-completion-or-revocation"; goalReference = "Complete accepted test goal"; status = "completed"; terminalEvidenceIds = @() }
    $missingTerminalResult = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "missing-terminal" -Value $missingTerminal) -ExpectedHash $hash4
    Assert-Equal -Actual $missingTerminalResult.ExitCode -Expected 1 -Message "Terminal authority without evidence was accepted."

    $completed = Copy-Handoff -Value $expired
    Set-Revision -Value $completed -Revision 5
    $completed.evidence += [ordered]@{ id = "authority-completed"; kind = "user-instruction"; reference = "goal completion observation"; observedAtUtc = "2026-09-05T21:56:00Z" }
    $completed.authorityRecord.expiry = [ordered]@{ kind = "until-goal-completion-or-revocation"; goalReference = "Complete accepted test goal"; status = "completed"; terminalEvidenceIds = @("authority-completed") }
    $completedWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "completed" -Value $completed) -ExpectedHash $hash4
    Assert-Equal -Actual $completedWrite.ExitCode -Expected 0 -Message "Evidenced authority completion did not persist."
    $hash5 = [string]$completedWrite.Value.documentSha256
    $completedCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Contains -Actual $completedCheck.Value.reasons -Expected "authority_scope_completed" -Message "Evidenced completion did not pause."

    $restored = Copy-Handoff -Value $currentHead
    Set-Revision -Value $restored -Revision 6
    $restoredWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "restored" -Value $restored) -ExpectedHash $hash5
    Assert-Equal -Actual $restoredWrite.ExitCode -Expected 0 -Message "Goal-lifecycle authority did not restore without a date."
    $hash6 = [string]$restoredWrite.Value.documentSha256
    Assert-Equal -Actual (Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath).ExitCode -Expected 0 -Message "Goal-lifecycle authority manufactured a time pause."

    $budget = Copy-Handoff -Value $restored
    Set-Revision -Value $budget -Revision 7
    $budget.nextAction.kind = "review"
    $budget.nextAction.requiredBudgetKeys = @("targetedReviews")
    $budget.budgets.targetedReviews.consumedAtLeast = 1
    $budgetWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "budget" -Value $budget) -ExpectedHash $hash6
    Assert-Equal -Actual $budgetWrite.ExitCode -Expected 0 -Message "Exhausted budget record did not persist."
    $hash7 = [string]$budgetWrite.Value.documentSha256
    $budgetCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Contains -Actual $budgetCheck.Value.reasons -Expected "budget_exhausted" -Message "Consumed targeted review did not pause."
    Assert-Equal -Actual $budget.budgets.targetedReviews.limit -Expected 1 -Message "Ordinary targeted review limit drifted."

    $exceptional = Copy-Handoff -Value $budget
    Set-Revision -Value $exceptional -Revision 8
    $exceptional.nextAction.requiredBudgetKeys = @("thirdReviewForNewP0P1")
    $exceptionalWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "exceptional" -Value $exceptional) -ExpectedHash $hash7
    Assert-Equal -Actual $exceptionalWrite.ExitCode -Expected 0 -Message "Inactive exceptional record did not persist."
    $hash8 = [string]$exceptionalWrite.Value.documentSha256
    $exceptionalCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Contains -Actual $exceptionalCheck.Value.reasons -Expected "exceptional_budget_not_activated" -Message "Inactive exceptional budget appeared free."

    $protected = Copy-Handoff -Value $exceptional
    Set-Revision -Value $protected -Revision 9
    $protected.nextAction.kind = "implement"
    $protected.nextAction.requiredBudgetKeys = @("implementationAttempts")
    $protectedHead = Invoke-GitTest -WorkingPath $protectedPath -Arguments @("rev-parse", "HEAD")
    $protectedStatus = Get-StatusSha256 -WorkingPath $protectedPath
    $protected.evidence += [ordered]@{ id = "protected-path"; kind = "local-path"; reference = $protectedPath; observedAtUtc = "2026-09-05T21:56:00Z" }
    $protected.protectedWorktrees = @([ordered]@{ path = $protectedPath; pathEvidenceId = "protected-path"; repositoryIdentity = [ordered]@{ provider = "github"; host = "github.com"; owner = "jacob-j-thomas"; name = "agenthome-poc"; sourceEvidenceId = "repository-identity" }; headSha = $protectedHead; statusSha256 = $protectedStatus; observedAtUtc = "2026-09-05T21:56:00Z"; reason = "owned by another task" })
    $protectedWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath (Write-Input -Name "protected" -Value $protected) -ExpectedHash $hash8
    Assert-Equal -Actual $protectedWrite.ExitCode -Expected 0 -Message "Protected external clone record did not persist. $($protectedWrite.Output)"
    $hash9 = [string]$protectedWrite.Value.documentSha256
    $unapprovedCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Contains -Actual $unapprovedCheck.Value.reasons -Expected "protected_worktree_read_not_authorized" -Message "Unapproved external path was read."
    $protectedIndexPath = Invoke-GitTest -WorkingPath $protectedPath -Arguments @("rev-parse", "--git-path", "index")
    if (-not [IO.Path]::IsPathFullyQualified($protectedIndexPath)) { $protectedIndexPath = Join-Path $protectedPath $protectedIndexPath }
    $protectedIndexBytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($protectedIndexPath))
    $protectedCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath -ApprovedPaths @($protectedPath)
    Assert-Equal -Actual $protectedCheck.ExitCode -Expected 0 -Message "Approved independent protected clone did not validate. $($protectedCheck.Output)"
    Assert-Equal -Actual (Invoke-GitTest -WorkingPath $protectedPath -Arguments @("rev-parse", "HEAD")) -Expected $protectedHead -Message "Protected HEAD changed during Check."
    Assert-Equal -Actual (Get-StatusSha256 -WorkingPath $protectedPath) -Expected $protectedStatus -Message "Protected status changed during Check."
    $protectedIndexHash = Get-Sha256 -Bytes ([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($protectedIndexPath)))
    Assert-Equal -Actual $protectedIndexHash -Expected (Get-Sha256 -Bytes $protectedIndexBytes) -Message "Protected index bytes changed during Check."
    $activeWorktreeList = Invoke-GitTest -WorkingPath $activePath -Arguments @("worktree", "list", "--porcelain")
    Assert-NotContainsText -Actual $activeWorktreeList -Unexpected $protectedPath -Message "Protected clone unexpectedly belonged to active clone registry."

    [IO.File]::WriteAllText((Join-Path $protectedPath "protected-dirty.txt"), "dirty`n", [Text.UTF8Encoding]::new($false))
    $protectedDirtyCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath -ApprovedPaths @($protectedPath)
    Assert-Contains -Actual $protectedDirtyCheck.Value.reasons -Expected "protected_worktree_status_changed" -Message "Protected status drift was missed."
    [IO.File]::Delete((Join-Path $protectedPath "protected-dirty.txt"))
    [void](Invoke-GitTest -WorkingPath $protectedPath -Arguments @("remote", "set-url", "origin", "https://github.com/example/different.git"))
    $protectedIdentityCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath -ApprovedPaths @($protectedPath)
    Assert-Contains -Actual $protectedIdentityCheck.Value.reasons -Expected "protected_repository_identity_mismatch" -Message "Protected identity drift was missed."
    [void](Invoke-GitTest -WorkingPath $protectedPath -Arguments @("remote", "set-url", "origin", "https://github.com/Jacob-J-Thomas/agenthome-poc.git"))

    [IO.File]::WriteAllText((Join-Path $protectedPath "protected-head.txt"), "advance`n", [Text.UTF8Encoding]::new($false))
    [void](Invoke-GitTest -WorkingPath $protectedPath -Arguments @("add", "protected-head.txt"))
    [void](Invoke-GitTest -WorkingPath $protectedPath -Arguments @("commit", "-m", "advance protected head"))
    $protectedHeadCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath -ApprovedPaths @($protectedPath)
    Assert-Contains -Actual $protectedHeadCheck.Value.reasons -Expected "protected_worktree_head_changed" -Message "Protected HEAD drift was missed."

    $movedProtectedPath = "$protectedPath-moved"
    [IO.Directory]::Move($protectedPath, $movedProtectedPath)
    $protectedUnavailableCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath -ApprovedPaths @($protectedPath)
    Assert-Contains -Actual $protectedUnavailableCheck.Value.reasons -Expected "protected_worktree_unavailable" -Message "Unavailable protected clone was missed."
    [IO.Directory]::Move($movedProtectedPath, $protectedPath)

    $redacted = Copy-Handoff -Value $protected
    Set-Revision -Value $redacted -Revision 10
    $redacted.objective.summary = "Authorization: Bearer header-secret-value GITHUB_TOKEN=environment-secret github_pat_token-secret-value"
    $redacted.nextAction.description = 'Literal $(touch should-never-run) remains inert'
    $redactedInput = Write-Input -Name "redacted" -Value $redacted
    $redactedWrite = Invoke-Handoff -Operation "Write" -RepositoryRoot $activePath -InputPath $redactedInput -ExpectedHash $hash9 -Environment @{ EMBODYSENSE_UNRELATED_SECRET = "process-environment-secret" }
    Assert-Equal -Actual $redactedWrite.ExitCode -Expected 0 -Message "Redacted write failed. $($redactedWrite.Output) $($redactedWrite.Error)"
    $persistedText = [IO.File]::ReadAllText($handoffPath)
    foreach ($secret in @("header-secret-value", "environment-secret", "token-secret-value", "process-environment-secret")) { Assert-NotContainsText -Actual ($persistedText + $redactedWrite.Output) -Unexpected $secret -Message "Redaction leaked a sentinel." }
    Assert-True -Condition ($redactedWrite.Value.redactionCount -gt 0) -Message "Redaction count was not reported."
    Assert-True -Condition (-not (Test-Path -LiteralPath (Join-Path $activePath "should-never-run"))) -Message "Next action was executed as code."

    $currentBytes = [IO.File]::ReadAllBytes($handoffPath)
    $unsupported = Copy-Handoff -Value $redacted
    $unsupported.schemaVersion = 2
    [IO.File]::WriteAllText($handoffPath, ($unsupported | ConvertTo-Json -Compress -Depth 32), [Text.UTF8Encoding]::new($false))
    $unsupportedRead = Invoke-Handoff -Operation "Read" -RepositoryRoot $activePath
    Assert-Equal -Actual $unsupportedRead.ExitCode -Expected 2 -Message "Unsupported schema did not pause."
    Assert-Contains -Actual $unsupportedRead.Value.reasons -Expected "schema_unsupported" -Message "Unsupported schema reason was absent."
    [IO.File]::WriteAllBytes($handoffPath, $currentBytes)

    $concurrentA = Copy-Handoff -Value $redacted
    $concurrentB = Copy-Handoff -Value $redacted
    Set-Revision -Value $concurrentA -Revision 11
    Set-Revision -Value $concurrentB -Revision 11
    $concurrentA.nextAction.description = "Concurrent candidate A"
    $concurrentB.nextAction.description = "Concurrent candidate B"
    $concurrentAPath = Write-Input -Name "concurrent-a" -Value $concurrentA
    $concurrentBPath = Write-Input -Name "concurrent-b" -Value $concurrentB
    $runningA = Start-HandoffWrite -RepositoryRoot $activePath -InputPath $concurrentAPath -ExpectedHash ([string]$redactedWrite.Value.documentSha256)
    $runningB = Start-HandoffWrite -RepositoryRoot $activePath -InputPath $concurrentBPath -ExpectedHash ([string]$redactedWrite.Value.documentSha256)
    $resultA = Receive-HandoffWrite -Running $runningA
    $resultB = Receive-HandoffWrite -Running $runningB
    Assert-Equal -Actual ((@($resultA.ExitCode, $resultB.ExitCode) | Sort-Object) -join ",") -Expected "0,2" -Message "Concurrent CAS did not produce one winner and one loser."
    Assert-True -Condition (@($resultA.Value.result, $resultB.Value.result) -ccontains "written") -Message "Concurrent CAS had no complete winner."
    Assert-True -Condition (@($resultA.Value.result, $resultB.Value.result) -ccontains "conflict") -Message "Concurrent CAS loser did not observe the winning hash."
    $concurrentRead = Invoke-Handoff -Operation "Read" -RepositoryRoot $activePath
    Assert-Equal -Actual $concurrentRead.ExitCode -Expected 0 -Message "Concurrent CAS left an invalid document."
    Assert-Equal -Actual $concurrentRead.Value.handoff.documentRevision -Expected 11 -Message "Concurrent CAS final revision was not complete."
    Assert-True -Condition ($concurrentRead.Value.handoff.nextAction.description -cin @("Concurrent candidate A", "Concurrent candidate B")) -Message "Concurrent CAS mixed candidate bytes."

    $fakeGitDirectory = New-FakeGitDirectory
    if ($null -ne $fakeGitDirectory) {
        $fakePath = "$fakeGitDirectory$([IO.Path]::PathSeparator)$env:PATH"
        $optionalLocksLog = Join-Path $tempRoot "fake-git-optional-locks.log"
        $optionalLocksResult = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath -Environment @{ PATH = $fakePath; FAKE_GIT_OPTIONAL_LOCKS_LOG = $optionalLocksLog; GIT_OPTIONAL_LOCKS = "1" }
        Assert-Equal -Actual $optionalLocksResult.ExitCode -Expected 2 -Message "Optional-lock observation Check did not retain its unrelated protected-worktree pause."
        $observedOptionalLocks = @([IO.File]::ReadAllLines($optionalLocksLog))
        Assert-True -Condition ($observedOptionalLocks.Count -gt 0) -Message "Fake Git did not observe any child process."
        Assert-True -Condition (@($observedOptionalLocks | Where-Object { $_ -cne "0" }).Count -eq 0) -Message "Spawned Git inherited caller optional-lock value instead of process-local zero."
        foreach ($mode in @("live-overrun", "descendant-pipe", "output-overflow")) {
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $boundedGitResult = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath -Environment @{ PATH = $fakePath; FAKE_GIT_MODE = $mode }
            $timer.Stop()
            Assert-Equal -Actual $boundedGitResult.ExitCode -Expected 2 -Message "Fake Git mode '$mode' did not pause safely."
            Assert-Contains -Actual $boundedGitResult.Value.reasons -Expected "repository_identity_mismatch" -Message "Fake Git mode '$mode' did not become unavailable."
            Assert-True -Condition ($timer.ElapsedMilliseconds -lt 6000) -Message "Fake Git mode '$mode' exceeded the bounded deadline."
            Assert-NotContainsText -Actual ($boundedGitResult.Output + $boundedGitResult.Error) -Unexpected "GIT_OVERFLOW_PRIVATE_SENTINEL" -Message "Fake Git mode '$mode' emitted captured private output."
        }
    }

    $outsideHandoffPath = Join-Path $tempRoot "outside-handoff.json"
    [IO.File]::Move($handoffPath, $outsideHandoffPath)
    [void][IO.File]::CreateSymbolicLink($handoffPath, $outsideHandoffPath)
    $outsideHandoffBytes = [IO.File]::ReadAllBytes($outsideHandoffPath)
    $symlinkRead = Invoke-Handoff -Operation "Read" -RepositoryRoot $activePath
    Assert-Equal -Actual $symlinkRead.ExitCode -Expected 2 -Message "Symlinked handoff did not pause Read."
    Assert-Contains -Actual $symlinkRead.Value.reasons -Expected "unsafe_local_state_path" -Message "Symlinked handoff Read reason was absent."
    $symlinkCheck = Invoke-Handoff -Operation "Check" -RepositoryRoot $activePath
    Assert-Equal -Actual $symlinkCheck.ExitCode -Expected 2 -Message "Symlinked handoff did not pause Check."
    Assert-Contains -Actual $symlinkCheck.Value.reasons -Expected "unsafe_local_state_path" -Message "Symlinked handoff Check reason was absent."
    Assert-Equal -Actual (Get-Sha256 -Bytes ([IO.File]::ReadAllBytes($outsideHandoffPath))) -Expected (Get-Sha256 -Bytes $outsideHandoffBytes) -Message "Symlink rejection changed outside bytes."
    [IO.File]::Delete($handoffPath)
    [IO.File]::Move($outsideHandoffPath, $handoffPath)

    $localDirectory = Split-Path -Parent $handoffPath
    $outsideLocalDirectory = Join-Path $tempRoot "outside-local"
    [IO.Directory]::Move($localDirectory, $outsideLocalDirectory)
    [void][IO.Directory]::CreateSymbolicLink($localDirectory, $outsideLocalDirectory)
    $outsideAncestorBytes = [IO.File]::ReadAllBytes((Join-Path $outsideLocalDirectory "delivery-handoff.json"))
    $ancestorRead = Invoke-Handoff -Operation "Read" -RepositoryRoot $activePath
    Assert-Equal -Actual $ancestorRead.ExitCode -Expected 2 -Message "Symlinked local-state ancestor did not pause Read."
    Assert-Contains -Actual $ancestorRead.Value.reasons -Expected "unsafe_local_state_path" -Message "Symlinked ancestor reason was absent."
    $outsideAncestorHash = Get-Sha256 -Bytes ([IO.File]::ReadAllBytes((Join-Path $outsideLocalDirectory "delivery-handoff.json")))
    Assert-Equal -Actual $outsideAncestorHash -Expected (Get-Sha256 -Bytes $outsideAncestorBytes) -Message "Ancestor rejection changed outside bytes."
    [IO.Directory]::Delete($localDirectory)
    [IO.Directory]::Move($outsideLocalDirectory, $localDirectory)

    Write-Output "DELIVERY_HANDOFF_TESTS_PASSED assertions=$assertionCount"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
