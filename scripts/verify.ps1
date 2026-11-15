[CmdletBinding()]
param([ValidateSet('Source','Compile','Hygiene','Closure','WorkflowPolicy','Updater','ReleaseLayout','Packs')][string]$Scope='Source',[switch]$Offline,[switch]$RequireBuild)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($RequireBuild -and $Scope -ne 'Closure') { throw '-RequireBuild is valid only with -Scope Closure.' }
function Verify-PublicTree {
    $files=@(& git -C $repo ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory public tree' }
    foreach ($file in $files) {
        if ($file -match '(^|/)(AGENTS\.md|ENGINEERING\.md|Naut\.Showcase|work|backup|Migration|bin|obj|dist|out|node_modules)(/|$)|\.bak$') {
            throw "Excluded public file: $file"
        }
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
        'docs/assets/readme/feature-face-intelligence.png',
        'docs/assets/readme/feature-3d-figures.png',
        'docs/assets/readme/feature-smart-import.png',
        'docs/assets/readme/feature-customization.png',
        'docs/assets/readme/feature-multilingual.png',
        'docs/assets/readme/feature-local-vault.png'
    )
    foreach ($required in @('LICENSE','BRAND-POLICY.md','CONTRIBUTING.md','SECURITY.md','THIRD-PARTY-NOTICES.txt') + $readmeAssets + $readmes + $userDocs) {
        if (-not (Test-Path -LiteralPath (Join-Path $repo $required) -PathType Leaf)) { throw "Missing public contract: $required" }
    }
    foreach ($readme in $readmes) {
        $text=Get-Content -LiteralPath (Join-Path $repo $readme) -Raw
        foreach ($link in $readmes) { if ($text -notmatch [regex]::Escape($link)) { throw "README locale navigation incomplete: $readme -> $link" } }
        foreach ($requiredText in @('wordmark-lockup.png','docs/assets/readme/naut-showcase.gif','feature-face-intelligence.png','feature-multilingual.png','GitHub Releases','Source Available')) {
            if ($text -notmatch [regex]::Escape($requiredText)) { throw "README public positioning incomplete: $readme missing $requiredText" }
        }
    }
    $primaryReadme=Get-Content -LiteralPath (Join-Path $repo 'README.md') -Raw
    [xml]$buildProps=Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
    $productVersion=$buildProps.SelectSingleNode('/Project/PropertyGroup/ProductVersion').InnerText
    foreach ($requiredText in @(('Download Naut v'+$productVersion),'View Live Demo','Documentation','Feature highlights','Why Naut','System Requirements','Quick Start')) {
        if ($primaryReadme -notmatch [regex]::Escape($requiredText)) { throw "Primary README landing contract missing: $requiredText" }
    }
    $requirements=Get-Content -LiteralPath (Join-Path $repo 'docs/system-requirements.md') -Raw
    foreach ($requiredText in @('Windows 10 64-bit','2 cores','8 GB','16 GB','3 GB','1280 × 720','1920 × 1080','Direct3D 11','2048 × 2048')) {
        if ($requirements -notmatch [regex]::Escape($requiredText)) { throw "System requirements contract missing: $requiredText" }
    }
    Write-Host 'README_PRODUCT_LANDING=PASS'
    Write-Host 'README_LOCALE_PARITY=PASS'
    Write-Host 'USER_DOCUMENTATION=PASS'
    Write-Host 'PUBLIC_TREE=PASS'
}
function Verify-WorkflowPolicy {
    $workflowRoot = Join-Path $repo '.github/workflows'
    $workflows = @(Get-ChildItem -LiteralPath $workflowRoot -File | Where-Object { $_.Extension -in '.yml', '.yaml' })
    foreach ($required in 'build.yml', 'verify.yml', 'packs.yml', 'release.yml') {
        if ($required -notin $workflows.Name) { throw "Missing public workflow: $required" }
    }
    foreach ($workflow in $workflows) {
        $lines = @(Get-Content -LiteralPath $workflow.FullName)
        $content = $lines -join "`n"
        $isRelease = $workflow.Name -ceq 'release.yml'
        if ($content -match '(?m)^  (?:pull_request_target|workflow_run):|(?im)^[ \t]*permissions:[ \t]*[^\s#]') {
            throw "Unexpected event or permissions syntax in $($workflow.Name)"
        }
        if ($content -notmatch '(?m)^permissions:\r?\n  contents: read\s*$') {
            throw "Explicit read-only default required in $($workflow.Name)"
        }
        $job = ''
        $inJobs = $false
        foreach ($line in $lines) {
            if ($line -eq 'jobs:') { $inJobs = $true }
            if ($inJobs -and $line -match '^  ([\w-]+):\s*$') { $job = $Matches[1] }
            if ($line -match '^\s*(?:-\s+)?uses:\s*([^\s#]+)' -and $Matches[1] -notmatch '^(\./|.+@[a-fA-F0-9]{40}$)') {
                throw "Unpinned action in $($workflow.Name)"
            }
            if ($line -match '^(\s+)([\w-]+):\s*["'']?write["'']?\s*(?:#.*)?$') {
                if (-not ($isRelease -and $job -ceq 'publish' -and $Matches[1].Length -eq 6 -and $Matches[2] -in 'actions', 'contents')) {
                    throw "Unexpected write permission in $($workflow.Name) job '$job'"
                }
            }
            if ($line -match '\bsecrets\.' -and
                -not ($isRelease -and $job -ceq 'publish' -and
                    $line -match '^\s+(GH_TOKEN|NEUTERRADISE_UPDATE_SIGNING_KEY_PEM): \$\{\{ secrets\.\w+ \}\}\s*$' -and
                    ($line -match 'GH_TOKEN: \$\{\{ secrets\.GITHUB_TOKEN \}\}' -or
                     $line -match 'NEUTERRADISE_UPDATE_SIGNING_KEY_PEM: \$\{\{ secrets\.NEUTERRADISE_UPDATE_SIGNING_KEY_PEM \}\}'))) {
                throw "Unexpected repository secret reference in $($workflow.Name) job '$job'"
            }
        }
        switch ($workflow.Name) {
            'verify.yml' {
                if ($content -notmatch '(?m)^    name: source-verify\s*$' -or
                    $content -notmatch '(?m)^  pull_request:\s*$' -or $content -notmatch '(?m)^  push:\s*$' -or
                    $content -notmatch 'scripts/verify\.ps1 -Scope Source' -or $content -match 'scripts/(?:build|release)\.ps1') {
                    throw 'Source Verify must keep its required check name and source-only push/PR validation.'
                }
            }
            'packs.yml' {
                if ($content -notmatch 'scripts/verify\.ps1 -Scope Packs' -or $content -match 'publish-packs|scripts/(?:build|release)\.ps1') {
                    throw 'Optional Packs must validate packs without publishing.'
                }
            }
            'release.yml' {
                $events = [regex]::Match($content, '(?ms)^on:\n(.*?)(?=^\S)').Groups[1].Value
                if (([regex]::Matches($events, '(?m)^  [\w-]+:').Value -join ',') -cne '  workflow_dispatch:' -or
                    $content -notmatch "(?m)^    if: .+github\.ref == 'refs/heads/main'" -or
                    $content -notmatch 'scripts/verify\.ps1 -Scope WorkflowPolicy' -or
                    $content -notmatch '(?m)^    needs: build\s*$') {
                    throw 'Release must be manual from main and publish only after the checked build.'
                }
            }
        }
    }
    Write-Host 'PUBLIC_WORKFLOW_POLICY=PASS'
}
if ($Scope -in @('Source','Hygiene','Closure')) { & (Join-Path $PSScriptRoot 'verification/verify-documentation.ps1') }
if ($Scope -in @('Source','Hygiene','Closure')) { Verify-PublicTree }
if ($Scope -in @('Source','WorkflowPolicy','Closure')) { Verify-WorkflowPolicy }
if ($Scope -in @('Source','Compile')) { & (Join-Path $PSScriptRoot 'verification/verify-compile.ps1') -Offline:$Offline }
if ($Scope -eq 'Updater') { & (Join-Path $PSScriptRoot 'verification/verify-updater.ps1') -Offline:$Offline }
if ($Scope -eq 'ReleaseLayout') {
    & (Join-Path $PSScriptRoot 'verification/verify-release-layout.ps1') -Offline:$Offline
}
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
if ($Scope -eq 'Packs') { & (Join-Path $PSScriptRoot 'verification/verify-packs.ps1') -Offline:$Offline }
Write-Host "VERIFY_SCOPE=$Scope"
Write-Host 'VERIFY=PASS'
