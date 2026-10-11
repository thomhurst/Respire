# Posts a PR review thread reply from a body file and optionally resolves the thread.
# Bodies never pass through PowerShell string expansion: "$reply.Body" expands only $reply
# and posts "System.Collections.Hashtable.Body" instead of the intended disposition.

# Text that PowerShell produces when an object, not its string content, reaches a reply.
$script:SerializedObjectPattern = '^\s*(?:System\.[A-Za-z0-9_.`\[\]]+|@\{[\s\S]*\})\s*$'

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

    $owner, $name = $Repo.Split('/', 2)
    $query = 'query($owner:String!,$name:String!,$pr:Int!,$cursor:String){viewer{login} repository(owner:$owner,name:$name){pullRequest(number:$pr){reviewThreads(first:100,after:$cursor){pageInfo{hasNextPage endCursor} nodes{id comments(first:1){nodes{databaseId}} latestComment:comments(last:1){nodes{databaseId body author{login}}}}}}}}'
    $thread = $null
    $cursor = $null
    do {
        $fields = @{ query = $query; owner = $owner; name = $name; pr = $Pr }
        if ($cursor) { $fields.cursor = $cursor }
        $data = (& $GhApi -Endpoint 'graphql' -Method POST -Fields $fields).data
        $page = $data.repository.pullRequest.reviewThreads
        $match = @($page.nodes | Where-Object { $_.comments.nodes[0].databaseId -eq $CommentId })
        if ($match.Count -gt 0) { $thread = $match[0] }
        $cursor = $page.pageInfo.endCursor
    } while (-not $thread -and $page.pageInfo.hasNextPage)
    if (-not $thread) { throw "No review thread starts with comment $CommentId on PR #$Pr." }

    # A failed resolve or lost POST response can leave the reply live. Reuse our matching
    # latest reply on retry, but never resolve on the strength of another user's comment.
    $latest = @($thread.latestComment.nodes)[0]
    $expectedBody = ConvertTo-ReplyText $Body
    if ($latest -and $latest.databaseId -ne $CommentId -and $data.viewer.login -and
        $latest.author.login -ceq $data.viewer.login -and
        (ConvertTo-ReplyText ([string]$latest.body)) -ceq $expectedBody) {
        $replyId = $latest.databaseId
    }
    else {
        $reply = & $GhApi -Endpoint "repos/$Repo/pulls/$Pr/comments/$CommentId/replies" -Method POST -InputJson (New-ReplyPayload $Body)
        # Remove a corrupt reply before failing; it must not become a disposition.
        if ((ConvertTo-ReplyText ([string]$reply.body)) -cne $expectedBody) {
            $null = & $GhApi -Endpoint "repos/$Repo/pulls/comments/$($reply.id)" -Method DELETE
            throw "Posted reply $($reply.id) does not match the requested body; deleted it and left the thread unresolved."
        }
        $replyId = $reply.id
    }
    if (-not $Resolve) { return [pscustomobject]@{ ReplyId = $replyId; ThreadId = $null } }

    $threadId = $thread.id
    $mutation = 'mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}'
    $resolved = (& $GhApi -Endpoint 'graphql' -Method POST -Fields @{ query = $mutation; id = $threadId }).data.resolveReviewThread.thread.isResolved
    if (-not $resolved) { throw "Thread $threadId did not resolve." }
    return [pscustomobject]@{ ReplyId = $replyId; ThreadId = $threadId }
}
