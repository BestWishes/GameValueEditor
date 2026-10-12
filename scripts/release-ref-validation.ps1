# A release tag remains valid after catalog/documentation commits advance main.
function Assert-AnnotatedReleaseAncestry {
    param([string]$RepositoryRoot, [string]$Tag, [string]$TargetCommitish)
    $branchRef = "refs/heads/$TargetCommitish"
    $tagRef = "refs/tags/$Tag"
    $branchCommit = (& git -C $RepositoryRoot rev-parse --verify "$branchRef^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branchCommit)) { throw "Local branch does not exist: $TargetCommitish" }
    $tagType = (& git -C $RepositoryRoot cat-file -t $tagRef).Trim()
    if ($LASTEXITCODE -ne 0 -or $tagType -ne 'tag') { throw "Local tag $Tag must be an annotated tag." }
    $tagCommit = (& git -C $RepositoryRoot rev-list -n 1 $tagRef).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagCommit)) { throw "Unable to resolve release tag $Tag." }
    & git -C $RepositoryRoot merge-base --is-ancestor $tagCommit $branchCommit
    if ($LASTEXITCODE -ne 0) { throw "Local release tag $Tag is not in the history of $TargetCommitish." }
}

function Assert-MatchingRemoteReleaseTag {
    param([string]$RepositoryRoot, [string]$Tag, $RemoteObject)
    $localObject = (& git -C $RepositoryRoot rev-parse --verify "refs/tags/$Tag").Trim()
    $localType = (& git -C $RepositoryRoot cat-file -t "refs/tags/$Tag").Trim()
    if ($LASTEXITCODE -ne 0 -or $localType -ne 'tag' -or $null -eq $RemoteObject -or
        $RemoteObject.PSObject.Properties.Name -notcontains 'type' -or
        $RemoteObject.PSObject.Properties.Name -notcontains 'sha' -or
        [string]$RemoteObject.type -cne 'tag' -or [string]$RemoteObject.sha -cne $localObject) {
        throw "Remote annotated tag $Tag differs from its verified local tag; refusing publication."
    }
}
