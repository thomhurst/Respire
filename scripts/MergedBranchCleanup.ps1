# Record remote cleanup before owner release removes the worktree registration.
function Set-MergedBranchCleanup {
    param(
        [Parameter(Mandatory)][string]$Worktree,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$ExpectedHead,
        [Parameter(Mandatory)][string]$RemoteUrl
    )
    $gitDirectory = git -C $Worktree rev-parse --absolute-git-dir 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record deferred remote branch cleanup.' }
    @{ Branch = $Branch; ExpectedHead = $ExpectedHead; RemoteUrl = $RemoteUrl } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $gitDirectory 'respire-merged-branch.json')
}

function Remove-MergedRemoteBranch {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$ExpectedHead,
        [Parameter(Mandatory)][string]$RemoteUrl
    )
    # Never apply a deferred deletion to a changed origin or an unvalidated ref.
    $currentRemote = @(git -C $Repo remote get-url --push --all origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or $currentRemote.Count -ne 1 -or $currentRemote[0] -cne $RemoteUrl) {
        Write-Host "Preserving merged remote branch '$Branch': origin changed or could not be inspected."
        return
    }
    git -C $Repo check-ref-format "refs/heads/$Branch" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0 -or $ExpectedHead -notmatch '^[0-9a-f]{40}$') {
        Write-Host 'Preserving remote branch: invalid deferred cleanup metadata.'
        return
    }
    $remoteRef = "refs/heads/$Branch"
    git -C $Repo ls-remote --exit-code $RemoteUrl $remoteRef 2>$null | Out-Null
    if ($LASTEXITCODE -eq 2) { return } # Repository auto-delete may have removed it already.
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Preserving merged remote branch '$Branch': remote inspection failed."
        return
    }
    # Pin the destination URL and old SHA. Do not follow another configured push URL.
    git -C $Repo push "--force-with-lease=${remoteRef}:$ExpectedHead" $RemoteUrl ":$remoteRef" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Preserving merged remote branch '$Branch': deletion failed or the remote tip changed."
    }
}
