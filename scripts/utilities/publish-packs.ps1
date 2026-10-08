[CmdletBinding()]
param([switch]$Publish)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$remote = (& git -C $repositoryRoot remote get-url origin).Trim()
if ($remote -notmatch '^https://github\.com/secondshift-dv/naut(?:\.git)?$') { throw 'Pack downloads publish only from the public naut repository.' }
if ((& git -C $repositoryRoot branch --show-current).Trim() -ne 'main') { throw 'Publish from public main.' }
if (@(& git -C $repositoryRoot status --porcelain).Count -gt 0) { throw 'Commit the reviewed source before publication.' }
$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$publicHead = (& gh api repos/secondshift-dv/naut/commits/main --jq '.sha').Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $publicHead) { throw 'Local main must match GitHub main.' }
$output = Join-Path $repositoryRoot 'out/pack-downloads'
& (Join-Path $PSScriptRoot 'package-packs.ps1') -OutputDirectory $output
$catalog = Get-Content -LiteralPath (Join-Path $output 'checksums.json') -Raw | ConvertFrom-Json
$files = @($catalog.packs | ForEach-Object { Join-Path $output $_.file }) + @(Join-Path $output 'checksums.json')
if (-not $Publish) { Write-Host "Prepared $($catalog.packs.Count) packs. Use -Publish after maintainer review."; return }
function Invoke-GitHubJson([string]$Endpoint, [object]$Body, [string]$Method = 'POST') {
    $inputPath = Join-Path $output 'github-request.json'
    [IO.File]::WriteAllText($inputPath, ($Body | ConvertTo-Json -Depth 20))
    $response = & gh api $Endpoint --method $Method --input $inputPath
    if ($LASTEXITCODE -ne 0) { throw "GitHub request failed: $Endpoint" }
    return ($response | ConvertFrom-Json)
}
$parent = (& gh api repos/secondshift-dv/naut/git/ref/heads/gh-pages | ConvertFrom-Json).object.sha
if ($LASTEXITCODE -ne 0) { throw 'The existing gh-pages branch is required.' }
$baseTree = (& gh api "repos/secondshift-dv/naut/git/commits/$parent" --jq '.tree.sha').Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the existing Pages commit.' }
$existingTree = & gh api "repos/secondshift-dv/naut/git/trees/$baseTree`?recursive=1" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $existingTree.truncated) { throw 'Cannot verify the existing Pages tree.' }
$treeEntries = @()
foreach ($file in $files) {
    $path = 'packs/' + [IO.Path]::GetFileName($file)
    $existing = @($existingTree.tree | Where-Object { $_.path -eq $path })
    $blobHash = (& git -C $repositoryRoot hash-object --no-filters $file).Trim()
    if ($path.EndsWith('.ntpack') -and $existing.Count -gt 0 -and $existing[0].sha -ne $blobHash) {
        throw "A different archive is already published at $path. Increase the pack version."
    }
    if ($existing.Count -gt 0 -and $existing[0].sha -eq $blobHash) { continue }
    $blob = Invoke-GitHubJson 'repos/secondshift-dv/naut/git/blobs' @{
        content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file)); encoding = 'base64'
    }
    $treeEntries += @{ path = $path; mode = '100644'; type = 'blob'; sha = $blob.sha }
}
if ($treeEntries.Count -eq 0) { Write-Host 'PACK_DOWNLOADS_ALREADY_CURRENT=YES'; return }
$tree = Invoke-GitHubJson 'repos/secondshift-dv/naut/git/trees' @{ base_tree = $baseTree; tree = $treeEntries }
$commit = Invoke-GitHubJson 'repos/secondshift-dv/naut/git/commits' @{
    message = "Publish reviewed optional packs from $head"; tree = $tree.sha; parents = @($parent)
}
# A non-fast-forward update fails rather than replacing a concurrent Pages publication.
$result = Invoke-GitHubJson 'repos/secondshift-dv/naut/git/refs/heads/gh-pages' @{ sha = $commit.sha; force = $false } 'PATCH'
Write-Host "PACK_PAGES_COMMIT=$($result.object.sha)"
Write-Host 'PACK_DOWNLOADS=https://secondshift-dv.github.io/naut/packs/'
