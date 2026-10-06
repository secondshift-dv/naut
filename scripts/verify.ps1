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
    $userDocs=@(
        'docs/README.md',
        'docs/getting-started.md',
        'docs/import-media.md',
        'docs/face-intelligence.md',
        'docs/profiles.md',
        'docs/vault.md',
        'docs/customization.md',
        'docs/languages.md',
        'docs/figures.md',
        'docs/system-requirements.md',
        'docs/troubleshooting.md'
    )
    $readmeAssets=@(
        'docs/assets/readme/naut-showcase.gif',
        'docs/assets/readme/showcase-home.png',
        'docs/assets/readme/showcase-profile.png',
        'docs/assets/readme/showcase-gallery.png',
        'docs/assets/readme/showcase-settings.png',
        'docs/assets/readme/feature-face-intelligence.svg',
        'docs/assets/readme/feature-3d-figures.svg',
        'docs/assets/readme/feature-smart-import.svg',
        'docs/assets/readme/feature-customization.svg',
        'docs/assets/readme/feature-multilingual.svg',
        'docs/assets/readme/feature-local-vault.svg'
    )
    foreach ($required in @('LICENSE','BRAND-POLICY.md','CONTRIBUTING.md','SECURITY.md','THIRD-PARTY-NOTICES.txt') + $readmeAssets + $readmes + $userDocs) {
        if (-not (Test-Path -LiteralPath (Join-Path $repo $required) -PathType Leaf)) { throw "Missing public contract: $required" }
    }
    foreach ($readme in $readmes) {
        $text=Get-Content -LiteralPath (Join-Path $repo $readme) -Raw
        foreach ($link in $readmes) { if ($text -notmatch [regex]::Escape($link)) { throw "README locale navigation incomplete: $readme -> $link" } }
        foreach ($requiredText in @('wordmark-lockup.png','docs/assets/readme/naut-showcase.gif','feature-face-intelligence.svg','feature-multilingual.svg','GitHub Releases','Source Available')) {
            if ($text -notmatch [regex]::Escape($requiredText)) { throw "README public positioning incomplete: $readme missing $requiredText" }
        }
    }
    $primaryReadme=Get-Content -LiteralPath (Join-Path $repo 'README.md') -Raw
    foreach ($requiredText in @('Download Naut v0.0.2','View Live Demo','Documentation','Feature highlights','Why Naut','System Requirements','Quick Start')) {
        if ($primaryReadme -notmatch [regex]::Escape($requiredText)) { throw "Primary README landing contract missing: $requiredText" }
    }
    $requirements=Get-Content -LiteralPath (Join-Path $repo 'docs/system-requirements.md') -Raw
    foreach ($requiredText in @('Windows 10 64-bit','2 cores','8 GB','16 GB','1.5 GB','1280 × 720','1920 × 1080','Direct3D 11','2048 × 2048')) {
        if ($requirements -notmatch [regex]::Escape($requiredText)) { throw "System requirements contract missing: $requiredText" }
    }
    Write-Host 'README_PRODUCT_LANDING=PASS'
    Write-Host 'README_LOCALE_PARITY=PASS'
    Write-Host 'USER_DOCUMENTATION=PASS'
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
