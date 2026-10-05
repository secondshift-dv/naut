[CmdletBinding()]
param([ValidateSet('Source','Compile','Hygiene','Closure','WorkflowPolicy')][string]$Scope='Source',[switch]$Offline,[switch]$RequireBuild)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
function Verify-PublicTree {
    $files=@(& git -C $repo ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory public tree' }
    foreach ($file in $files) {
        if ($file -match '(^|/)(AGENTS\.md|ENGINEERING\.md|Naut\.Showcase|work|backup|Migration|bin|obj|dist|out|node_modules)(/|$)|\.(pem|pfx|p12|key|db|sqlite|bak)$') { throw "Excluded public file: $file" }
        $bytes=[IO.File]::ReadAllBytes((Join-Path $repo $file))
        if ($bytes -contains 0) { continue }
        if ([Text.Encoding]::UTF8.GetString($bytes) -match '-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----|\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,})\b') { throw "Secret candidate in $file; value suppressed" }
    }
    $readmes=@('README.md','README.de.md','README.es.md','README.fr.md','README.id.md','README.ja.md','README.ko.md','README.zh-Hans.md')
    foreach ($required in @('LICENSE','BRAND-POLICY.md','CONTRIBUTING.md','SECURITY.md','THIRD-PARTY-NOTICES.txt') + $readmes) {
        if (-not (Test-Path -LiteralPath (Join-Path $repo $required) -PathType Leaf)) { throw "Missing public contract: $required" }
    }
    foreach ($readme in $readmes) {
        $text=Get-Content -LiteralPath (Join-Path $repo $readme) -Raw
        foreach ($link in $readmes) { if ($text -notmatch [regex]::Escape($link)) { throw "README locale navigation incomplete: $readme -> $link" } }
        if ($text -notmatch 'wordmark-lockup\.png' -or $text -notmatch 'Source Available' -or $text -notmatch 'GitHub Releases') {
            throw "README public positioning incomplete: $readme"
        }
    }
    Write-Host 'README_LOCALE_PARITY=PASS'
    Write-Host 'PUBLIC_TREE=PASS'
}
function Verify-WorkflowPolicy {
    foreach ($workflow in Get-ChildItem -LiteralPath (Join-Path $repo '.github/workflows') -File -Filter '*.yml') {
        $text=Get-Content -LiteralPath $workflow.FullName -Raw
        if ($text -match 'pull_request_target|permissions:\s*write-all') { throw "Unsafe public workflow: $($workflow.Name)" }
        foreach ($line in $text -split '\r?\n') {
            if ($line -match '^\s*uses:\s*([^\s]+)' -and $Matches[1] -notmatch '^\./|@[a-f0-9]{40}$') { throw "Unpinned action: $($workflow.Name)" }
        }
        if ($workflow.Name -ne 'release.yml' -and $text -match 'UPDATE_SIGNING_KEY|contents:\s*write|id-token:\s*write|attestations:\s*write') { throw "Unexpected publication privilege: $($workflow.Name)" }
    }
    Write-Host 'PUBLIC_WORKFLOW_POLICY=PASS'
}
if ($Scope -in @('Source','Hygiene','Closure')) { Verify-PublicTree }
if ($Scope -in @('Source','WorkflowPolicy','Closure')) { Verify-WorkflowPolicy }
if ($Scope -in @('Source','Compile')) { & (Join-Path $PSScriptRoot 'verification/verify-compile.ps1') -Offline:$Offline }
if ($Scope -eq 'Closure') {
    if (@(& git -C $repo status --porcelain).Count) { throw 'Public worktree is not clean' }
    if ((& git -C $repo rev-parse HEAD).Trim() -ne (& git -C $repo rev-parse origin/main).Trim()) { throw 'Public main differs from origin/main' }
    if ($RequireBuild) {
        $provenance=Get-Content -LiteralPath (Join-Path $repo 'dist/build-provenance.json') -Raw | ConvertFrom-Json
        if ($provenance.sourceHead -ne (& git -C $repo rev-parse HEAD).Trim()) { throw 'Package provenance does not match HEAD' }
        $zip=Join-Path $repo ('dist/'+$provenance.zipFileName)
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $provenance.zipSha256) { throw 'Package checksum mismatch' }
    }
}
Write-Host "VERIFY_SCOPE=$Scope"
Write-Host 'VERIFY=PASS'
