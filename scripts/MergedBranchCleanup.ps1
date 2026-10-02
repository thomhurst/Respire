# Record remote cleanup before owner release removes the worktree registration.
function Get-RemoteUrlFingerprint {
    param([Parameter(Mandatory)][string]$RemoteUrl)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($RemoteUrl)))
}

function Set-MergedBranchCleanup {
    param(
        [Parameter(Mandatory)][string]$Worktree,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$ExpectedHead,
        [Parameter(Mandatory)][string]$RemoteUrl
    )
    $checkedOutBranch = git -C $Worktree symbolic-ref -q HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or $checkedOutBranch -cne "refs/heads/$Branch") {
        Write-Host 'Preserving remote branch: selected checkout does not hold the merged branch.'
        return
    }
    $checkedOutHead = git -C $Worktree rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or $checkedOutHead -cne $ExpectedHead) {
        Write-Host 'Preserving remote branch: selected checkout head changed.'
        return
    }
    $gitDirectory = git -C $Worktree rev-parse --absolute-git-dir 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record deferred remote branch cleanup.' }
    @{ Branch = $Branch; ExpectedHead = $ExpectedHead; RemoteUrlHash = (Get-RemoteUrlFingerprint $RemoteUrl) } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $gitDirectory 'respire-merged-branch.json')
}

function Remove-MergedRemoteBranch {
    param(
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Branch,
        [Parameter(Mandatory)][string]$ExpectedHead,
        [Parameter(Mandatory)][string]$RemoteUrlHash
    )
    # Never apply a deferred deletion to a changed origin or an unvalidated ref.
    $currentRemote = @(git -C $Repo remote get-url --push --all origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or $currentRemote.Count -ne 1 -or
        (Get-RemoteUrlFingerprint $currentRemote[0]) -cne $RemoteUrlHash) {
        Write-Host "Preserving merged remote branch '$Branch': origin changed or could not be inspected."
        return
    }
    $remoteUrl = $currentRemote[0]
    git -C $Repo check-ref-format "refs/heads/$Branch" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0 -or $ExpectedHead -notmatch '^[0-9a-f]{40}$') {
        Write-Host 'Preserving remote branch: invalid deferred cleanup metadata.'
        return
    }
    $remoteRef = "refs/heads/$Branch"
    git -C $Repo ls-remote --exit-code $remoteUrl $remoteRef 2>$null | Out-Null
    if ($LASTEXITCODE -eq 2) { return } # Repository auto-delete may have removed it already.
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Preserving merged remote branch '$Branch': remote inspection failed."
        return
    }
    # Pin the destination URL and old SHA. Do not follow another configured push URL.
    git -C $Repo push "--force-with-lease=${remoteRef}:$ExpectedHead" $remoteUrl ":$remoteRef" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Preserving merged remote branch '$Branch': deletion failed or the remote tip changed."
    }
}
