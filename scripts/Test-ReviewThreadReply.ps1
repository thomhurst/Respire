# Offline regression suite for ReviewThreadReply.ps1: needs only PowerShell 7.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReviewThreadReply.ps1')

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Expected, [string]$Message) {
    try { & $Action }
    catch {
        Assert ($_.Exception.Message -like "*$Expected*") "$Message (threw: $($_.Exception.Message))"
        return
    }
    throw "$Message (did not throw)"
}

# The reported bug: a hashtable interpolated into a string loses its content.
$reply = @{ Body = 'Fixed in abc123.' }
$broken = "$reply.Body"
Assert ($broken -eq 'System.Collections.Hashtable.Body') 'Precondition: string expansion reproduces the bug.'
Assert ($null -ne (Test-ReplyBody $broken)) 'Hashtable.Body text must be rejected.'
Assert ($null -ne (Test-ReplyBody "Fixed: $([ordered]@{ a = 1 })")) 'OrderedDictionary text must be rejected.'
Assert ($null -ne (Test-ReplyBody "$([pscustomobject]@{ Body = 'x' })")) 'PSCustomObject text must be rejected.'
Assert ($null -ne (Test-ReplyBody 'System.Object[]')) 'A lone type name must be rejected.'
Assert ($null -ne (Test-ReplyBody "  `n ")) 'A blank body must be rejected.'
Assert ($null -eq (Test-ReplyBody $reply.Body)) 'Real content must be accepted.'
Assert ($null -eq (Test-ReplyBody 'Use `[NotInParallel]`; `System.Collections.Generic.List<T>` is fine.')) 'Code references must be accepted.'

# Payload JSON round-trips characters that shell or PowerShell expansion would alter.
$tricky = "Fixed in ff78781: `$env:X, `"quotes`", back``tick, @{ a = 1 } spaced,`r`n\r\n literal, 50% done."
$payload = New-ReplyPayload $tricky
Assert ((ConvertFrom-Json $payload).body -ceq $tricky) 'Payload must preserve the body exactly.'

# Fake GitHub API: records calls and serves one thread page per request.
function New-FakeGh([object]$StoredBody, [object[]]$Pages, [bool]$Resolves = $true) {
    $state = [pscustomobject]@{ Calls = [Collections.Generic.List[object]]::new(); Pages = [Collections.Queue]::new($Pages) }
    $api = {
        param([string]$Endpoint, [string]$Method = 'GET', [string]$InputJson, [hashtable]$Fields = @{})
        $state.Calls.Add([pscustomobject]@{ Endpoint = $Endpoint; Method = $Method; InputJson = $InputJson; Fields = $Fields })
        if ($Endpoint -like '*/replies') {
            $sent = (ConvertFrom-Json $InputJson).body
            return [pscustomobject]@{ id = 99; body = $(if ($null -ne $StoredBody) { $StoredBody } else { $sent }) }
        }
        if ($Fields.query -like 'mutation*') {
            return [pscustomobject]@{ data = [pscustomobject]@{ resolveReviewThread = [pscustomobject]@{ thread = [pscustomobject]@{ isResolved = $Resolves } } } }
        }
        return [pscustomobject]@{ data = [pscustomobject]@{ repository = [pscustomobject]@{ pullRequest = [pscustomobject]@{ reviewThreads = $state.Pages.Dequeue() } } } }
    }.GetNewClosure()
    return $state, $api
}
function Thread([string]$Id, [long]$FirstComment) {
    [pscustomobject]@{ id = $Id; comments = [pscustomobject]@{ nodes = @([pscustomobject]@{ databaseId = $FirstComment }) } }
}
function Page([object[]]$Nodes, [string]$Next) {
    [pscustomobject]@{ nodes = $Nodes; pageInfo = [pscustomobject]@{ hasNextPage = [bool]$Next; endCursor = $Next } }
}

# Reply only: one POST with the exact JSON body, no thread lookup.
$state, $api = New-FakeGh $null @()
$result = Publish-ReviewThreadReply -Repo 'o/r' -Pr 7 -CommentId 5 -Body $tricky -GhApi $api
Assert ($state.Calls.Count -eq 1) 'Reply without -Resolve must make one call.'
Assert ($state.Calls[0].Endpoint -eq 'repos/o/r/pulls/7/comments/5/replies' -and $state.Calls[0].Method -eq 'POST') 'Reply must post to the thread replies endpoint.'
Assert ((ConvertFrom-Json $state.Calls[0].InputJson).body -ceq $tricky) 'Posted JSON must carry the exact body.'
Assert ($result.ReplyId -eq 99 -and $null -eq $result.ThreadId) 'Reply-only result must report the reply id.'

# Resolve: finds the thread on a later page by its first comment, then resolves it.
$state, $api = New-FakeGh $null @((Page @((Thread 'T1' 1)) 'c1'), (Page @((Thread 'T2' 5)) $null))
$result = Publish-ReviewThreadReply -Repo 'o/r' -Pr 7 -CommentId 5 -Body 'Fixed in abc.' -Resolve -GhApi $api
Assert ($result.ThreadId -eq 'T2') 'Resolve must find the thread on a later page.'
Assert ($state.Calls[2].Fields.cursor -eq 'c1') 'Pagination must pass the previous end cursor.'
Assert ($state.Calls[1].Fields.pr -is [int]) 'The PR number must stay numeric for the GraphQL Int variable.'
Assert ($state.Calls[3].Fields.query -like 'mutation*' -and $state.Calls[3].Fields.id -eq 'T2') 'Resolve must run resolveReviewThread on the found thread.'

# A broken body is rejected before any API call.
$state, $api = New-FakeGh $null @()
Assert-Throws { Publish-ReviewThreadReply -Repo 'o/r' -Pr 7 -CommentId 5 -Body $broken -Resolve -GhApi $api } 'serialized PowerShell object' 'Broken body must be rejected.'
Assert ($state.Calls.Count -eq 0) 'A rejected body must not reach GitHub.'

# A stored body that differs from the request keeps the thread open.
$state, $api = New-FakeGh 'System.Collections.Hashtable.Body' @((Page @((Thread 'T2' 5)) $null))
Assert-Throws { Publish-ReviewThreadReply -Repo 'o/r' -Pr 7 -CommentId 5 -Body 'Fixed in abc.' -Resolve -GhApi $api } 'does not match' 'A mismatched stored body must fail.'
Assert ($state.Calls.Count -eq 1) 'A mismatched reply must not resolve the thread.'

# Line-ending and trailing-whitespace normalization does not count as a mismatch.
$state, $api = New-FakeGh "Fixed in abc.`nLine two." @()
$null = Publish-ReviewThreadReply -Repo 'o/r' -Pr 7 -CommentId 5 -Body "Fixed in abc.`r`nLine two.`r`n" -GhApi $api

# Unknown thread and a failed mutation both fail loudly.
$state, $api = New-FakeGh $null @((Page @((Thread 'T1' 1)) $null))
Assert-Throws { Publish-ReviewThreadReply -Repo 'o/r' -Pr 7 -CommentId 5 -Body 'Fixed.' -Resolve -GhApi $api } 'No review thread' 'Missing thread must fail.'
$state, $api = New-FakeGh $null @((Page @((Thread 'T2' 5)) $null)) $false
Assert-Throws { Publish-ReviewThreadReply -Repo 'o/r' -Pr 7 -CommentId 5 -Body 'Fixed.' -Resolve -GhApi $api } 'did not resolve' 'Unresolved mutation result must fail.'

# The default invoker: field-only calls send no --input (an empty one replaces the GraphQL
# query), numbers use -F, and a JSON body travels in an --input file.
function gh {
    $script:ghArgs = @($args)
    $inputIndex = [Array]::IndexOf($script:ghArgs, '--input')
    $script:ghInput = if ($inputIndex -ge 0) { [IO.File]::ReadAllText($script:ghArgs[$inputIndex + 1]) } else { $null }
    $global:LASTEXITCODE = 0
    '{"id":1,"body":"ok"}'
}
$null = Invoke-GhApiJson -Endpoint 'graphql' -Method POST -Fields @{ query = 'query($pr:Int!){x}'; pr = 7 }
Assert ($script:ghArgs -notcontains '--input') 'Field-only calls must not send --input.'
Assert ($script:ghArgs -contains 'query=query($pr:Int!){x}') 'The query must pass unexpanded.'
Assert ($script:ghArgs[[Array]::IndexOf($script:ghArgs, 'pr=7') - 1] -ceq '-F') 'Numbers must use -F.'
$null = Invoke-GhApiJson -Endpoint 'repos/o/r/pulls/7/comments/5/replies' -Method POST -InputJson (New-ReplyPayload $tricky)
Assert ((ConvertFrom-Json $script:ghInput).body -ceq $tricky) 'The --input file must carry the exact JSON body.'
Remove-Item Function:gh

Write-Host 'ReviewThreadReply tests passed.'
