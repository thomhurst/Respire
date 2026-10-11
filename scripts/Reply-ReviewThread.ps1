<#
.SYNOPSIS
Replies to a PR review thread from a body file and optionally resolves the thread.

.DESCRIPTION
Write the disposition to a file first (Write tool or a quoted here-string), then pass its path.
The body is sent as JSON through gh api --input, so PowerShell never expands it. The script
rejects bodies consisting only of serialized object text such as "System.Collections.Hashtable.Body".
It finds the thread before posting, reuses your matching latest reply on retry, deletes a
mismatched stored reply, and only resolves after confirming the body. Pass -Repo owner/name
when invoking from outside the intended repository; the fallback uses the current directory.

.EXAMPLE
pwsh scripts/Reply-ReviewThread.ps1 -Pr 1440 -CommentId 4241877246 -BodyFile $env:TEMP/reply.md -Resolve
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][int]$Pr,
    # databaseId of the thread's first comment (REST pulls/<N>/comments id).
    [Parameter(Mandatory)][long]$CommentId,
    [Parameter(Mandatory)][string]$BodyFile,
    [string]$Repo,
    [switch]$Resolve
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReviewThreadReply.ps1')

if (-not (Test-Path -LiteralPath $BodyFile -PathType Leaf)) { throw "Body file not found: $BodyFile" }
$body = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $BodyFile).ProviderPath)
if (-not $Repo) {
    $Repo = gh repo view --json nameWithOwner --jq .nameWithOwner
    if ($LASTEXITCODE -ne 0 -or -not $Repo) { throw 'Cannot determine the repository; pass -Repo owner/name.' }
}
$result = Publish-ReviewThreadReply -Repo $Repo -Pr $Pr -CommentId $CommentId -Body $body -Resolve:$Resolve
Write-Host "Posted reply $($result.ReplyId) on PR #$Pr$(if ($result.ThreadId) { "; resolved thread $($result.ThreadId)" })."
