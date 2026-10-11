# Posts a PR review thread reply from a body file and optionally resolves the thread.
# Bodies never pass through PowerShell string expansion: "$reply.Body" expands only $reply
# and posts "System.Collections.Hashtable.Body" instead of the intended disposition.

# Text that PowerShell produces when an object, not its string content, reaches a reply.
$script:SerializedObjectPattern = '^\s*System\.[A-Za-z0-9_.`\[\]]+\s*$|System\.Collections\.(Hashtable|Specialized\.OrderedDictionary)|System\.Management\.Automation\.PSCustomObject|@\{[A-Za-z_]+='

function Test-ReplyBody {
    param([AllowNull()][AllowEmptyString()][string]$Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return 'Reply body is empty.' }
    if ($Body -match $script:SerializedObjectPattern) {
        return "Reply body contains serialized PowerShell object text ('$($Matches[0].Trim())'). Pass the string content, not the object."
    }
    return $null
}

function ConvertTo-ReplyText {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    return ($Text -replace "`r`n", "`n").TrimEnd()
}

function New-ReplyPayload {
    param([Parameter(Mandatory)][string]$Body)
    return [ordered]@{ body = $Body } | ConvertTo-Json -Compress
}

# Default GitHub API invoker. Tests replace it through -GhApi.
function Invoke-GhApiJson {
    param(
        [Parameter(Mandatory)][string]$Endpoint,
        [string]$Method = 'GET',
        [string]$InputJson,
        [hashtable]$Fields = @{}
    )
    $arguments = @('api', $Endpoint, '--method', $Method)
    # -F sends numbers as GraphQL Int values; -f keeps strings, including queries, literal.
    foreach ($key in $Fields.Keys) {
        $value = $Fields[$key]
        $arguments += @($(if ($value -is [int] -or $value -is [long]) { '-F' } else { '-f' }), "$key=$value")
    }
    $inputFile = $null
    try {
        if (-not [string]::IsNullOrEmpty($InputJson)) {
            $inputFile = [IO.Path]::GetTempFileName()
            [IO.File]::WriteAllText($inputFile, $InputJson, [Text.UTF8Encoding]::new($false))
            $arguments += @('--input', $inputFile)
        }
        $output = & gh @arguments
        if ($LASTEXITCODE -ne 0) { throw "gh api $Endpoint failed (exit $LASTEXITCODE)." }
        return ($output -join "`n") | ConvertFrom-Json
    }
    finally {
        if ($inputFile) { Remove-Item -LiteralPath $inputFile -ErrorAction SilentlyContinue }
    }
}

function Publish-ReviewThreadReply {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][int]$Pr,
        [Parameter(Mandatory)][long]$CommentId,
        [Parameter(Mandatory)][string]$Body,
        [switch]$Resolve,
        [scriptblock]$GhApi = ${function:Invoke-GhApiJson}
    )
    $problem = Test-ReplyBody $Body
    if ($problem) { throw $problem }

    $reply = & $GhApi -Endpoint "repos/$Repo/pulls/$Pr/comments/$CommentId/replies" -Method POST -InputJson (New-ReplyPayload $Body)
    # Confirm GitHub stored the intended text before resolving on the strength of it.
    if ((ConvertTo-ReplyText ([string]$reply.body)) -cne (ConvertTo-ReplyText $Body)) {
        throw "Posted reply $($reply.id) does not match the requested body; leaving the thread unresolved."
    }
    if (-not $Resolve) { return [pscustomobject]@{ ReplyId = $reply.id; ThreadId = $null } }

    $owner, $name = $Repo.Split('/', 2)
    $query = 'query($owner:String!,$name:String!,$pr:Int!,$cursor:String){repository(owner:$owner,name:$name){pullRequest(number:$pr){reviewThreads(first:100,after:$cursor){pageInfo{hasNextPage endCursor} nodes{id comments(first:1){nodes{databaseId}}}}}}}'
    $threadId = $null
    $cursor = $null
    do {
        $fields = @{ query = $query; owner = $owner; name = $name; pr = $Pr }
        if ($cursor) { $fields.cursor = $cursor }
        $page = (& $GhApi -Endpoint 'graphql' -Method POST -Fields $fields).data.repository.pullRequest.reviewThreads
        $match = @($page.nodes | Where-Object { $_.comments.nodes[0].databaseId -eq $CommentId })
        if ($match.Count -gt 0) { $threadId = $match[0].id }
        $cursor = $page.pageInfo.endCursor
    } while (-not $threadId -and $page.pageInfo.hasNextPage)
    if (-not $threadId) { throw "No review thread starts with comment $CommentId on PR #$Pr." }

    $mutation = 'mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}'
    $resolved = (& $GhApi -Endpoint 'graphql' -Method POST -Fields @{ query = $mutation; id = $threadId }).data.resolveReviewThread.thread.isResolved
    if (-not $resolved) { throw "Thread $threadId did not resolve." }
    return [pscustomobject]@{ ReplyId = $reply.id; ThreadId = $threadId }
}
