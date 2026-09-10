[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Repository = $env:GITHUB_REPOSITORY,

    [Parameter(Mandatory = $false)]
    [Nullable[int]]$Campaign,

    [Parameter(Mandatory = $false)]
    [switch]$Json,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 1000)]
    [int]$MaximumGraphQlRequests = 64,

    [Parameter(Mandatory = $false)]
    [ValidateRange(1, 1000000)]
    [int]$MaximumCollectedNodes = 10000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Repository) -or $Repository -notmatch '^[^/]+/[^/]+$') {
    throw 'Repository must use the OWNER/REPO form.'
}

$IssuePageSize = 100
$PullRequestPageSize = 100
$NestedLabelLimit = 50
$ClosingReferenceLimit = 100
$repositoryParts = $Repository -split '/', 2
$errors = [System.Collections.Generic.List[object]]::new()
$warnings = [System.Collections.Generic.List[object]]::new()
$pageCache = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
$graphQlRequestCount = 0
$collectedNodeCount = 0
$issuePageCount = 0
$pullRequestPageCount = 0

$issueQuery = @'
query IssueAuditInventory($owner: String!, $name: String!, $after: String) {
  repository(owner: $owner, name: $name) {
    nameWithOwner
    issues(first: 100, after: $after, states: [OPEN, CLOSED], orderBy: {field: CREATED_AT, direction: ASC}) {
      totalCount
      nodes {
        id number title body state stateReason locked
        labels(first: 50) { totalCount nodes { name } pageInfo { hasNextPage endCursor } }
        parent { id number repository { nameWithOwner } }
        blockedBy(first: 1) { totalCount nodes { number } }
      }
      pageInfo { hasNextPage endCursor }
    }
  }
}
'@

$pullRequestQuery = @'
query IssueAuditOpenPullRequests($owner: String!, $name: String!, $after: String) {
  repository(owner: $owner, name: $name) {
    nameWithOwner
    pullRequests(first: 100, after: $after, states: OPEN, orderBy: {field: CREATED_AT, direction: ASC}) {
      totalCount
      nodes {
        id number url
        closingIssuesReferences(first: 100) {
          totalCount
          nodes { id number repository { nameWithOwner } }
          pageInfo { hasNextPage endCursor }
        }
      }
      pageInfo { hasNextPage endCursor }
    }
  }
}
'@

function Add-Finding {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$Target,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $false)][Nullable[int]]$Issue,
        [Parameter(Mandatory = $false)][Nullable[int]]$RelatedIssue,
        [Parameter(Mandatory = $false)][Nullable[int]]$PullRequest
    )

    $Target.Add([pscustomobject][ordered]@{
        code = $Code
        kind = $Kind
        issue = $Issue
        relatedIssue = $RelatedIssue
        pullRequest = $PullRequest
        message = $Message
    })
}

function Invoke-GraphQlPage {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Query,
        [Parameter(Mandatory = $false)][AllowNull()][string]$Cursor
    )

    $cacheKey = "$Operation|$Cursor"
    if ($pageCache.ContainsKey($cacheKey)) {
        return $pageCache[$cacheKey]
    }

    if ($script:graphQlRequestCount -ge $MaximumGraphQlRequests) {
        throw "AIDLC-E001 GraphQL request cap of $MaximumGraphQlRequests would be exceeded."
    }

    $script:graphQlRequestCount++
    $payload = [ordered]@{
        query = $Query
        variables = [ordered]@{ owner = $repositoryParts[0]; name = $repositoryParts[1]; after = $Cursor }
    } | ConvertTo-Json -Depth 20 -Compress

    $output = @($payload | & gh api graphql --input - 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "AIDLC-E003 gh GraphQL request failed: $($output -join [Environment]::NewLine)"
    }

    $text = ($output | ForEach-Object { [string]$_ }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw 'AIDLC-E003 GraphQL returned an empty response.'
    }

    try {
        $response = $text | ConvertFrom-Json -Depth 100
    }
    catch {
        throw "AIDLC-E003 GraphQL returned invalid JSON: $($_.Exception.Message)"
    }

    $responseErrors = @(if ($null -ne $response.PSObject.Properties['errors']) { $response.errors })
    if ($responseErrors.Count -gt 0) {
        throw "AIDLC-E003 GraphQL returned errors: $((@($responseErrors) | ForEach-Object { [string]$_.message }) -join '; ')"
    }
    if ($null -eq $response.data -or $null -eq $response.data.repository) {
        throw 'AIDLC-E003 GraphQL did not return the repository.'
    }
    if ([string]$response.data.repository.nameWithOwner -ne $Repository) {
        throw "AIDLC-E003 GraphQL returned repository '$($response.data.repository.nameWithOwner)' instead of '$Repository'."
    }

    $pageCache[$cacheKey] = $response
    return $response
}

function Get-PaginatedConnection {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][string]$Query,
        [Parameter(Mandatory = $true)][string]$ConnectionName
    )

    $items = [System.Collections.Generic.List[object]]::new()
    $seenIds = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    $seenNumbers = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    $seenCursors = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    $cursor = $null
    $expectedTotal = $null
    $pages = 0

    while ($true) {
        $response = Invoke-GraphQlPage -Operation $Operation -Query $Query -Cursor $cursor
        $connection = $response.data.repository.$ConnectionName
        if ($null -eq $connection -or $null -eq $connection.pageInfo) {
            throw "AIDLC-E003 GraphQL response omitted $ConnectionName or its pageInfo."
        }

        $pages++
        $pageTotal = [int]$connection.totalCount
        if ($null -eq $expectedTotal) {
            $expectedTotal = $pageTotal
        }
        elseif ([int]$expectedTotal -ne $pageTotal) {
            throw "AIDLC-E005 $ConnectionName totalCount changed from $expectedTotal to $pageTotal during pagination."
        }

        foreach ($node in @($connection.nodes)) {
            if ($null -eq $node -or [string]::IsNullOrWhiteSpace([string]$node.id)) {
                throw "AIDLC-E005 $ConnectionName returned a node without an id."
            }
            $idKey = [string]$node.id
            $numberKey = [string][int]$node.number
            if ($seenIds.ContainsKey($idKey) -or $seenNumbers.ContainsKey($numberKey)) {
                throw "AIDLC-E005 $ConnectionName returned duplicate id or number for #$numberKey."
            }
            if ($script:collectedNodeCount -ge $MaximumCollectedNodes) {
                throw "AIDLC-E002 collected-node cap of $MaximumCollectedNodes would be exceeded."
            }
            $script:collectedNodeCount++
            $seenIds[$idKey] = $true
            $seenNumbers[$numberKey] = $true
            $items.Add($node)
        }

        if (-not [bool]$connection.pageInfo.hasNextPage) {
            break
        }

        $nextCursor = [string]$connection.pageInfo.endCursor
        if ([string]::IsNullOrWhiteSpace($nextCursor) -or $nextCursor -eq $cursor -or $seenCursors.ContainsKey($nextCursor)) {
            throw "AIDLC-E004 $ConnectionName returned a missing, repeated, or non-advancing cursor."
        }
        $seenCursors[$nextCursor] = $true
        $cursor = $nextCursor
    }

    if ($items.Count -ne [int]$expectedTotal) {
        throw "AIDLC-E005 $ConnectionName collected $($items.Count) unique nodes but totalCount was $expectedTotal."
    }

    return [pscustomobject]@{ Items = @($items); Pages = $pages }
}

function Get-LabelNames {
    param([Parameter(Mandatory = $true)][object]$Issue)
    return @($Issue.labels.nodes | ForEach-Object { [string]$_.name })
}

function Get-WorkLevel {
    param([Parameter(Mandatory = $true)][object]$Issue)
    $workLabels = @(Get-LabelNames -Issue $Issue | Where-Object { $_.StartsWith('work:', [System.StringComparison]::Ordinal) })
    if ($workLabels.Count -ne 1) { return $null }
    switch ($workLabels[0]) {
        'work:campaign' { return 'Campaign' }
        'work:phase' { return 'Phase' }
        'work:uow' { return 'UOW' }
        'work:bolt' { return 'Bolt' }
        'work:finding' { return 'Finding' }
        default { return 'Unknown' }
    }
}

function Get-BodyParentClaims {
    param([Parameter(Mandatory = $false)][AllowNull()][AllowEmptyString()][string]$Body)
    $claims = [System.Collections.Generic.List[object]]::new()
    foreach ($match in [regex]::Matches($Body, '(?im)^\s*(?:[-*]\s*)?(?:Work level:\s*[^.\r\n]+\.\s*)?Native parent:\s*(?<parent>None|#\d+)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        $value = [string]$match.Groups['parent'].Value
        $claims.Add([pscustomobject]@{ Number = $(if ($value -match '^#(?<n>\d+)$') { [int]$Matches['n'] } else { $null }) })
    }
    foreach ($match in [regex]::Matches($Body, '(?im)^###\s+Parent (?:Campaign|Phase|UOW)\s*$\s*^#(?<number>\d+)\s*$')) {
        $claims.Add([pscustomobject]@{ Number = [int]$match.Groups['number'].Value })
    }
    return @($claims)
}

function Get-AdmissionSection {
    param([Parameter(Mandatory = $false)][AllowNull()][AllowEmptyString()][string]$Body)
    $match = [regex]::Match($Body, '(?ims)^##\s+Admission and placement\s*$\s*(?<content>.*?)(?=^##\s|\z)')
    if (-not $match.Success) { return '' }
    return $match.Groups['content'].Value
}

function Test-HumanInterventionContract {
    param([Parameter(Mandatory = $true)][object]$Issue)
    $section = [regex]::Match([string]$Issue.body, '(?ims)^###\s+Human intervention required\s*$\s*(?<content>.*?)(?=^###\s|\z)')
    if (-not $section.Success) { return $false }
    foreach ($field in @('Human action', 'Human owner', 'Exit evidence')) {
        if ($section.Groups['content'].Value -notmatch "(?im)^\s*-\s*$([regex]::Escape($field)):\s*\S.+$") { return $false }
    }
    return $true
}

function Get-SortedFindings {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Findings)
    return @($Findings | Sort-Object @{ Expression = { if ($null -eq $_.issue) { [int]::MaxValue } else { [int]$_.issue } } }, @{ Expression = { if ($null -eq $_.pullRequest) { [int]::MaxValue } else { [int]$_.pullRequest } } }, code, kind, message)
}

try {
    $issueCollection = Get-PaginatedConnection -Operation 'IssueAuditInventory' -Query $issueQuery -ConnectionName 'issues'
    $issuePageCount = $issueCollection.Pages
    $pullRequestCollection = Get-PaginatedConnection -Operation 'IssueAuditOpenPullRequests' -Query $pullRequestQuery -ConnectionName 'pullRequests'
    $pullRequestPageCount = $pullRequestCollection.Pages
}
catch {
    $message = [string]$_.Exception.Message
    $code = if ($message -match '^(AIDLC-E\d{3})') { $Matches[1] } else { 'AIDLC-E003' }
    Add-Finding -Target $errors -Code $code -Kind 'collection_failed' -Message $message
    $issueCollection = [pscustomobject]@{ Items = @(); Pages = $issuePageCount }
    $pullRequestCollection = [pscustomobject]@{ Items = @(); Pages = $pullRequestPageCount }
}

$issues = @($issueCollection.Items)
$pullRequests = @($pullRequestCollection.Items)
$issueById = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
$issueByNumber = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
$childrenByParentId = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
foreach ($issue in $issues) {
    $issueById[[string]$issue.id] = $issue
    $issueByNumber[[string][int]$issue.number] = $issue
    if ([int]$issue.labels.totalCount -ne (@($issue.labels.nodes)).Count -or [bool]$issue.labels.pageInfo.hasNextPage) {
        Add-Finding -Target $errors -Code 'AIDLC-E006' -Kind 'collection_bound_exceeded' -Issue ([int]$issue.number) -Message "Issue #$($issue.number) has an incomplete labels connection (limit $NestedLabelLimit)."
    }
    if ($null -ne $issue.parent) {
        $parentId = [string]$issue.parent.id
        if (-not $childrenByParentId.ContainsKey($parentId)) { $childrenByParentId[$parentId] = [System.Collections.Generic.List[object]]::new() }
        $childrenByParentId[$parentId].Add($issue)
    }
}

$scope = [System.Collections.Generic.List[object]]::new()
$scopeIds = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
function Add-Descendants {
    param([Parameter(Mandatory = $true)][object]$Root)
    $queue = [System.Collections.Generic.Queue[object]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        $key = [string]$current.id
        if ($scopeIds.ContainsKey($key)) { continue }
        $scopeIds[$key] = $true
        $scope.Add($current)
        if ($childrenByParentId.ContainsKey($key)) {
            foreach ($child in @($childrenByParentId[$key])) { $queue.Enqueue($child) }
        }
    }
}

$campaignRoots = @($issues | Where-Object { (Get-WorkLevel -Issue $_) -eq 'Campaign' -and $null -eq $_.parent } | Sort-Object number)
if ($null -ne $Campaign) {
    $campaignKey = [string][int]$Campaign
    if (-not $issueByNumber.ContainsKey($campaignKey)) {
        Add-Finding -Target $errors -Code 'AIDLC-E011' -Kind 'campaign_scope_invalid' -Issue ([int]$Campaign) -Message "Scoped Campaign #$Campaign was not found."
    }
    else {
        $selectedCampaign = $issueByNumber[$campaignKey]
        if ((Get-WorkLevel -Issue $selectedCampaign) -ne 'Campaign' -or $null -ne $selectedCampaign.parent) {
            Add-Finding -Target $errors -Code 'AIDLC-E011' -Kind 'campaign_scope_invalid' -Issue ([int]$Campaign) -Message "Scoped issue #$Campaign is not a parentless Campaign."
        }
        Add-Descendants -Root $selectedCampaign
    }
}
else {
    foreach ($root in $campaignRoots) { Add-Descendants -Root $root }
    foreach ($issue in $issues) {
        if ((@(Get-LabelNames -Issue $issue | Where-Object { $_.StartsWith('work:', [System.StringComparison]::Ordinal) })).Count -gt 0 -and -not $scopeIds.ContainsKey([string]$issue.id)) {
            $scopeIds[[string]$issue.id] = $true
            $scope.Add($issue)
        }
    }
}

$levelById = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
foreach ($issue in @($scope)) {
    $number = [int]$issue.number
    $labels = @(Get-LabelNames -Issue $issue)
    $workLabels = @($labels | Where-Object { $_.StartsWith('work:', [System.StringComparison]::Ordinal) })
    $level = Get-WorkLevel -Issue $issue
    $levelById[[string]$issue.id] = $level

    if ($workLabels.Count -ne 1 -or $level -eq 'Unknown') {
        Add-Finding -Target $errors -Code 'AIDLC-E010' -Kind 'work_label_invalid' -Issue $number -Message "Issue #$number must have exactly one recognized work:* label."
    }

    if (-not [bool]$issue.locked) {
        Add-Finding -Target $errors -Code 'AIDLC-E018' -Kind 'issue_unlocked' -Issue $number -Message "Governed issue #$number must be locked."
    }
    if ([int]$issue.blockedBy.totalCount -gt 0) {
        $blocker = @($issue.blockedBy.nodes | Select-Object -First 1)
        $related = if ($blocker.Count -gt 0) { [Nullable[int]][int]$blocker[0].number } else { $null }
        Add-Finding -Target $errors -Code 'AIDLC-E019' -Kind 'native_blocked_by_forbidden' -Issue $number -RelatedIssue $related -Message "Issue #$number uses native blocked by; technical prerequisites belong in its contract."
    }

    $statuses = @($labels | Where-Object { $_.StartsWith('status:', [System.StringComparison]::Ordinal) })
    if ([string]$issue.state -eq 'OPEN') {
        foreach ($prefix in @('type:', 'domain:', 'status:')) {
            $matches = @($labels | Where-Object { $_.StartsWith($prefix, [System.StringComparison]::Ordinal) })
            if ($matches.Count -ne 1) {
                Add-Finding -Target $errors -Code 'AIDLC-E016' -Kind 'label_dimension_invalid' -Issue $number -Message "Open issue #$number must have exactly one $prefix label."
            }
        }
        $allowedStatuses = switch ($level) {
            'Campaign' { @('status:tracking') }
            'Phase' { @('status:tracking') }
            'UOW' { @('status:tracking') }
            'Bolt' { @('status:needs-spec', 'status:queued', 'status:ready', 'status:in-progress', 'status:deferred', 'status:blocked') }
            'Finding' { @('status:needs-spec', 'status:deferred', 'status:blocked') }
            default { @() }
        }
        if ($statuses.Count -eq 1 -and $allowedStatuses -notcontains $statuses[0]) {
            Add-Finding -Target $errors -Code 'AIDLC-E017' -Kind 'status_invalid' -Issue $number -Message "Issue #$number has status '$($statuses[0])', which is invalid for level '$level'."
        }
        if ($statuses -contains 'status:blocked' -and -not (Test-HumanInterventionContract -Issue $issue)) {
            Add-Finding -Target $errors -Code 'AIDLC-E020' -Kind 'human_intervention_contract_missing' -Issue $number -Message "Blocked issue #$number lacks a populated Human intervention required contract."
        }
    }
    elseif ($statuses.Count -gt 0) {
        Add-Finding -Target $errors -Code 'AIDLC-E017' -Kind 'closed_status_present' -Issue $number -Message "Closed issue #$number retains status label(s): $($statuses -join ', ')."
    }

    $parent = $issue.parent
    $parentLevel = if ($null -ne $parent -and $issueById.ContainsKey([string]$parent.id)) { Get-WorkLevel -Issue $issueById[[string]$parent.id] } else { $null }
    $expectedParentLevel = switch ($level) { 'Phase' { 'Campaign' }; 'UOW' { 'Phase' }; 'Bolt' { 'UOW' }; default { $null } }
    if ($level -in @('Campaign', 'Finding')) {
        if ($null -ne $parent) {
            Add-Finding -Target $errors -Code 'AIDLC-E011' -Kind 'root_parent_invalid' -Issue $number -RelatedIssue ([int]$parent.number) -Message "$level #$number must be parentless."
        }
    }
    elseif ($level -in @('Phase', 'UOW', 'Bolt')) {
        $parentIdentityInvalid = $null -ne $parent -and $issueById.ContainsKey([string]$parent.id) -and [int]$parent.number -ne [int]$issueById[[string]$parent.id].number
        if ($null -eq $parent -or [string]$parent.repository.nameWithOwner -ne $Repository -or -not $issueById.ContainsKey([string]$parent.id) -or $parentIdentityInvalid -or $parentLevel -ne $expectedParentLevel) {
            $related = if ($null -ne $parent) { [Nullable[int]][int]$parent.number } else { $null }
            Add-Finding -Target $errors -Code 'AIDLC-E012' -Kind 'parent_level_invalid' -Issue $number -RelatedIssue $related -Message "$level #$number must have one native $expectedParentLevel parent in $Repository."
        }
    }

    if ($level -in @('Bolt', 'Finding') -and $childrenByParentId.ContainsKey([string]$issue.id) -and (@($childrenByParentId[[string]$issue.id])).Count -gt 0) {
        Add-Finding -Target $errors -Code 'AIDLC-E015' -Kind 'leaf_has_children' -Issue $number -Message "$level #$number must not have native children."
    }

    $nativeParentNumber = if ($null -eq $parent) { $null } else { [Nullable[int]][int]$parent.number }
    foreach ($claim in @(Get-BodyParentClaims -Body ([string]$issue.body))) {
        if (($null -eq $claim.Number -and $null -ne $nativeParentNumber) -or ($null -ne $claim.Number -and ($null -eq $nativeParentNumber -or [int]$claim.Number -ne [int]$nativeParentNumber))) {
            Add-Finding -Target $errors -Code 'AIDLC-E013' -Kind 'body_parent_mismatch' -Issue $number -RelatedIssue $nativeParentNumber -Message "Issue #$number has a documented parent claim that disagrees with native parentage."
        }
    }
}

foreach ($issue in @($scope)) {
    $number = [int]$issue.number
    $visited = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    $chain = [System.Collections.Generic.List[int]]::new()
    $current = $issue
    while ($null -ne $current.parent) {
        $parentId = [string]$current.parent.id
        if ($visited.ContainsKey($parentId)) {
            Add-Finding -Target $errors -Code 'AIDLC-E012' -Kind 'parent_cycle' -Issue $number -Message "Issue #$number has a cycle in its native parent chain."
            break
        }
        $visited[$parentId] = $true
        if (-not $issueById.ContainsKey($parentId)) { break }
        $current = $issueById[$parentId]
        $chain.Add([int]$current.number)
        if ([string]$issue.state -eq 'OPEN' -and [string]$current.state -eq 'CLOSED' -and (Get-WorkLevel -Issue $current) -in @('Campaign', 'Phase', 'UOW')) {
            Add-Finding -Target $errors -Code 'AIDLC-E014' -Kind 'open_below_closed' -Issue $number -RelatedIssue ([int]$current.number) -Message "Open issue #$number is below closed $(Get-WorkLevel -Issue $current) #$($current.number)."
        }
        if ($chain.Count -gt 3) {
            Add-Finding -Target $errors -Code 'AIDLC-E012' -Kind 'maximum_depth_exceeded' -Issue $number -Message "Issue #$number is more than three edges below a Campaign."
            break
        }
    }

    $ancestryMatch = [regex]::Match([string]$issue.body, '(?im)^\s*(?:[-*]\s*)?Current ancestry:\s*(?<chain>[^\r\n]+)$')
    if ($ancestryMatch.Success) {
        $claimed = @([regex]::Matches($ancestryMatch.Groups['chain'].Value, '#(?<number>\d+)') | ForEach-Object { [int]$_.Groups['number'].Value })
        $actual = @($chain)
        if (($claimed -join ',') -ne ($actual -join ',')) {
            Add-Finding -Target $errors -Code 'AIDLC-E013' -Kind 'body_ancestry_mismatch' -Issue $number -Message "Issue #$number has a Current ancestry claim that disagrees with native parentage."
        }
    }
}

foreach ($uow in @($scope | Where-Object { (Get-WorkLevel -Issue $_) -eq 'UOW' -and [string]$_.state -eq 'OPEN' })) {
    $children = @(if ($childrenByParentId.ContainsKey([string]$uow.id)) { $childrenByParentId[[string]$uow.id] })
    $bolts = @($children | Where-Object { (Get-WorkLevel -Issue $_) -eq 'Bolt' })
    $activeBolts = @($bolts | Where-Object { [string]$_.state -eq 'OPEN' })
    if ($activeBolts.Count -gt 12) {
        Add-Finding -Target $errors -Code 'AIDLC-E021' -Kind 'active_bolt_limit_exceeded' -Issue ([int]$uow.number) -Message "Open UOW #$($uow.number) has $($activeBolts.Count) active Bolts; maximum is 12."
    }
    if ($bolts.Count -lt 2) {
        $admission = Get-AdmissionSection -Body ([string]$uow.body)
        $status = @(Get-LabelNames -Issue $uow | Where-Object { $_.StartsWith('status:', [System.StringComparison]::Ordinal) })
        if ($bolts.Count -eq 0 -and $status -contains 'status:tracking' -and $admission -match 'Intentionally dormant with zero Bolts') {
            Add-Finding -Target $warnings -Code 'AIDLC-W001' -Kind 'dormant_uow_zero_bolts' -Issue ([int]$uow.number) -Message "UOW #$($uow.number) is intentionally dormant with zero Bolts."
        }
        elseif ($status -contains 'status:tracking' -and $admission -match 'Explicit decomposition-needed posture') {
            Add-Finding -Target $warnings -Code 'AIDLC-W002' -Kind 'uow_decomposition_pending' -Issue ([int]$uow.number) -Message "UOW #$($uow.number) has an explicit decomposition-needed posture."
        }
        else {
            Add-Finding -Target $errors -Code 'AIDLC-E024' -Kind 'uow_under_decomposed' -Issue ([int]$uow.number) -Message "Open UOW #$($uow.number) has fewer than two Bolts without a declared dormant or decomposition posture."
        }
    }
}

foreach ($aggregate in @($scope | Where-Object { (Get-WorkLevel -Issue $_) -in @('Campaign', 'Phase', 'UOW') -and [string]$_.state -eq 'OPEN' })) {
    $children = @(if ($childrenByParentId.ContainsKey([string]$aggregate.id)) { $childrenByParentId[[string]$aggregate.id] })
    $openChildren = @($children | Where-Object { [string]$_.state -eq 'OPEN' })
    if ($children.Count -gt 0 -and $openChildren.Count -eq 0) {
        Add-Finding -Target $warnings -Code 'AIDLC-W003' -Kind 'parent_acceptance_review' -Issue ([int]$aggregate.number) -Message "Open $(Get-WorkLevel -Issue $aggregate) #$($aggregate.number) has only closed native children and needs outcome review."
    }
}

$closersByBolt = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
foreach ($pullRequest in $pullRequests) {
    $closing = $pullRequest.closingIssuesReferences
    if ([int]$closing.totalCount -ne (@($closing.nodes)).Count -or [bool]$closing.pageInfo.hasNextPage) {
        Add-Finding -Target $errors -Code 'AIDLC-E006' -Kind 'collection_bound_exceeded' -PullRequest ([int]$pullRequest.number) -Message "PR #$($pullRequest.number) has an incomplete closing-issue connection (limit $ClosingReferenceLimit)."
        continue
    }
    foreach ($target in @($closing.nodes)) {
        if ($null -ne $Campaign -and -not $scopeIds.ContainsKey([string]$target.id)) { continue }
        $targetIssue = if ([string]$target.repository.nameWithOwner -eq $Repository -and $issueById.ContainsKey([string]$target.id)) { $issueById[[string]$target.id] } else { $null }
        if ($null -eq $targetIssue -or (Get-WorkLevel -Issue $targetIssue) -ne 'Bolt') {
            Add-Finding -Target $errors -Code 'AIDLC-E022' -Kind 'closing_target_invalid' -Issue ([int]$target.number) -PullRequest ([int]$pullRequest.number) -Message "Open PR #$($pullRequest.number) closes issue #$($target.number), which is not a local Bolt."
            continue
        }
        $boltKey = [string]$target.id
        if (-not $closersByBolt.ContainsKey($boltKey)) { $closersByBolt[$boltKey] = [System.Collections.Generic.List[int]]::new() }
        $closersByBolt[$boltKey].Add([int]$pullRequest.number)
    }
}
foreach ($boltId in $closersByBolt.Keys) {
    if ($closersByBolt[$boltId].Count -gt 1) {
        $bolt = $issueById[$boltId]
        Add-Finding -Target $errors -Code 'AIDLC-E023' -Kind 'multiple_open_closers' -Issue ([int]$bolt.number) -Message "Bolt #$($bolt.number) has multiple open closing PRs: $($closersByBolt[$boltId] -join ', ')."
    }
}

$sortedErrors = @(Get-SortedFindings -Findings @($errors))
$sortedWarnings = @(Get-SortedFindings -Findings @($warnings))
$result = [ordered]@{
    schemaVersion = 1
    readOnly = $true
    repository = $Repository
    scope = [ordered]@{ mode = $(if ($null -eq $Campaign) { 'all' } else { 'campaign' }); campaign = $Campaign }
    collection = [ordered]@{
        issuePages = $issuePageCount
        pullRequestPages = $pullRequestPageCount
        graphQlRequests = $graphQlRequestCount
        maximumGraphQlRequests = $MaximumGraphQlRequests
        collectedIssues = $issues.Count
        collectedOpenPullRequests = $pullRequests.Count
        collectedNodes = $collectedNodeCount
        maximumCollectedNodes = $MaximumCollectedNodes
    }
    campaigns = @($campaignRoots | ForEach-Object { [int]$_.number })
    auditedIssues = $scope.Count
    errors = $sortedErrors
    warnings = $sortedWarnings
}

if ($Json) {
    $result | ConvertTo-Json -Depth 20
}
else {
    Write-Host "Audited $($scope.Count) governed issues across $($campaignRoots.Count) Campaign(s) in $Repository."
    foreach ($warning in $sortedWarnings) { Write-Warning "[$($warning.code)] $($warning.message)" }
    foreach ($errorRecord in $sortedErrors) { Write-Error "[$($errorRecord.code)] $($errorRecord.message)" -ErrorAction Continue }
    if ($sortedErrors.Count -eq 0) { Write-Host "Issue governance audit passed with $($sortedWarnings.Count) warning(s)." }
}

if ($sortedErrors.Count -gt 0) { exit 1 }
