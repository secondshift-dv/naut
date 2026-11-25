[CmdletBinding()]
param([switch]$Publish, [switch]$Prepared)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Prepared -and -not $Publish) { throw '-Prepared requires -Publish.' }
$signingKey = if ($Publish) { $env:NEUTERRADISE_UPDATE_SIGNING_KEY_PEM } else { $null }
if ($Publish) {
    if ([string]::IsNullOrWhiteSpace($signingKey)) { throw 'The pinned publisher signing key is required.' }
    Remove-Item Env:NEUTERRADISE_UPDATE_SIGNING_KEY_PEM
}
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$remote = (& git -C $repositoryRoot remote get-url origin).Trim()
if ($remote -notmatch '^https://github\.com/secondshift-dv/naut(?:\.git)?$') { throw 'Pack downloads publish only from the public naut repository.' }
if ((& git -C $repositoryRoot branch --show-current).Trim() -ne 'main') { throw 'Publish from public main.' }
if (@(& git -C $repositoryRoot status --porcelain).Count -gt 0) { throw 'Commit the reviewed source before publication.' }
$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($Prepared -and ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne 'secondshift-dv/naut' -or
    $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne 'refs/heads/main' -or
    $env:GITHUB_SHA -cne $head)) { throw 'Prepared publication requires the exact manually dispatched public main commit.' }
$publicHead = (& gh api repos/secondshift-dv/naut/commits/main --jq '.sha').Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $publicHead) { throw 'Local main must match GitHub main.' }
$output = Join-Path $repositoryRoot 'out/pack-downloads'
if (-not $Prepared) { & (Join-Path $PSScriptRoot 'package-packs.ps1') -OutputDirectory $output }
$catalog = Get-Content -LiteralPath (Join-Path $output 'checksums.json') -Raw | ConvertFrom-Json
$source = Get-Content -LiteralPath (Join-Path $repositoryRoot 'packs/catalog.json') -Raw | ConvertFrom-Json
if ($catalog.schemaVersion -ne 1 -or @($catalog.packs).Count -ne @($source.packs).Count) { throw 'Unexpected pack registry inventory.' }
$files = @()
foreach ($entry in $source.packs) {
    $matches = @($catalog.packs | Where-Object { $_.slug -ceq $entry.slug })
    if ($matches.Count -ne 1) { throw 'Missing or duplicate official pack entry.' }
    $pack = $matches[0]
    $name = "$($entry.slug)-$($entry.version).ntpack"
    if ($entry.slug -notmatch '^[a-z0-9-]+$' -or $entry.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
        $pack.packId -cne $entry.packId -or $pack.version -cne $entry.version -or $pack.file -cne $name -or
        $pack.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $pack.contentHash -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Prepared registry does not match the reviewed source collection.'
    }
    $manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot "packs/$($entry.slug)/pack.json") -Raw | ConvertFrom-Json
    if ($pack.minContractVersion -ne $manifest.minContractVersion -or
        @($pack.predecessors).Count -ne @($entry.predecessors).Count) { throw 'Prepared compatibility metadata mismatch.' }
    foreach ($previous in $entry.predecessors) {
        if (@($pack.predecessors | Where-Object { $_.version -ceq $previous.version -and $_.contentHash -ceq $previous.contentHash }).Count -ne 1) {
            throw 'Prepared predecessor metadata mismatch.'
        }
    }
    $file = Join-Path $output $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
        (Get-Item -LiteralPath $file).Length -ne [long]$pack.bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pack.sha256) {
        throw "Prepared archive integrity mismatch: $name"
    }
    $files += $file
}
$files += Join-Path $output 'checksums.json'
if (-not $Publish) { Write-Host "Prepared $($catalog.packs.Count) packs. Use -Publish after maintainer review."; return }
. (Join-Path $PSScriptRoot 'publisher-signature.ps1')
$signaturePath = Join-Path $output 'checksums-signature.json'
New-UpdatePublisherSignature -ManifestPath (Join-Path $output 'checksums.json') -SignaturePath $signaturePath `
    -ContractPath (Join-Path $repositoryRoot 'release-contract.json') -PrivateKeyPem $signingKey
$signingKey = $null
function Invoke-GitHubJson([string]$Endpoint, [object]$Body, [string]$Method = 'POST') {
    $inputPath = Join-Path $output 'github-request.json'
    [IO.File]::WriteAllText($inputPath, ($Body | ConvertTo-Json -Depth 20))
        try { $response = & gh api $Endpoint --method $Method --input $inputPath }
    finally { Remove-Item -LiteralPath $inputPath -Force }
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
