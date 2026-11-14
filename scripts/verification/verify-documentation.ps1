[CmdletBinding()]
param([string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$issues = [Collections.Generic.List[string]]::new()
$tracked = @(& git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate documentation files.' }

function Read-Document([string]$relative) {
    $path = Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    try { return $utf8.GetString([IO.File]::ReadAllBytes($path)) }
    catch { [void]$issues.Add("$($relative): invalid UTF-8 or unreadable file"); return $null }
}

$documents = @($tracked | Where-Object {
    $_ -match '^README(?:\.[A-Za-z-]+)?\.md$' -or
    $_ -match '^docs/(?!work/)(?!assets/).+\.(?:md|txt)$' -or
    $_ -match '^packs/(?:README\.md|[^/]+/README\.(?:md|txt))$' -or
    $_ -in @('CONTRIBUTING.md','SECURITY.md','BRAND-POLICY.md','.github/PULL_REQUEST_TEMPLATE.md','THIRD-PARTY-NOTICES.txt','.github/ISSUE_TEMPLATE/bug.yml')
})

function Test-MarkdownAnchor([string]$file, [string]$fragment) {
    if (-not $fragment -or [IO.Path]::GetExtension($file) -ine '.md') { return $true }
    $expected = [Uri]::UnescapeDataString($fragment).ToLowerInvariant()
    $source = $utf8.GetString([IO.File]::ReadAllBytes($file))
    if ($source -match ('(?i)<a\s+(?:[^>]*\s)?(?:id|name)=["'']' + [regex]::Escape($expected) + '["'']')) { return $true }
    $seen = @{}
    foreach ($line in ($source -split '\r?\n')) {
        if ($line -notmatch '^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$') { continue }
        $heading = $Matches[1] -replace '<[^>]+>', '' -replace '[*_~]', ''
        $slug = ($heading.ToLowerInvariant() -replace '[^\p{L}\p{Nd}\s_-]', '' -replace '\s+', '-').Trim('-')
        if (-not $slug) { continue }
        if ($seen.ContainsKey($slug)) { $seen[$slug]++; $slug = "$slug-$($seen[$slug])" }
        else { $seen[$slug] = 0 }
        if ($slug -ceq $expected) { return $true }
    }
    return $false
}

foreach ($relative in $documents) {
    $content = Read-Document $relative
    if ($null -eq $content) { continue }
    $lines = $content -split '\r?\n'
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '[\x00-\x08\x0B\x0C\x0E-\x1F]' -or
            $line.Contains([string][char]0xFFFD) -or
            $line -match '\?{2,}|\s\?\s|(?m)^\s*\?\s*$' -or
            $line -match '\u00C3[\u0080-\u00BF]|\u00C2[\u0080-\u00BF]|\u00E2(?:\u20AC|\u2122|\u0153|\u017E|\u201A)') {
            [void]$issues.Add("$($relative):$($i + 1) damaged text, unexpected question-mark separator, or encoding artifact")
        }
    }
    $directory = Split-Path -Parent (Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    foreach ($match in [regex]::Matches($content, '!\[[^\]]*\]\(([^)]+)\)|(?<!!)\[[^\]]+\]\(([^)]+)\)')) {
        $value = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        if ($value -match '^(?:[a-z][a-z0-9+.-]*:|//|/)') { continue }
        $parts = $value -split '#', 2
        $target = ($parts[0] -split '\?', 2)[0]
        $anchor = if ($parts.Count -gt 1) { $parts[1] } else { '' }
        $resolved = if ($target) { Join-Path $directory ([Uri]::UnescapeDataString($target)) } else { Join-Path $root $relative }
        if (-not (Test-Path -LiteralPath $resolved)) {
            [void]$issues.Add("$($relative): broken relative link '$value'")
        } elseif (-not (Test-MarkdownAnchor -file $resolved -fragment $anchor)) {
            [void]$issues.Add("$($relative): missing Markdown anchor '$value'")
        }
    }
}

[xml]$props = [IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'), $utf8)
$version = $props.SelectSingleNode('/Project/PropertyGroup/ProductVersion').InnerText
$catalogPath = Join-Path $root 'packs/catalog.json'
if (Test-Path -LiteralPath $catalogPath) {
    $catalog = [IO.File]::ReadAllText($catalogPath, $utf8) | ConvertFrom-Json
    $overview = Read-Document 'packs/README.md'
    foreach ($pack in $catalog.packs) {
        $heading = "## $($pack.name) - $($pack.version) source"
        if ($null -eq $overview -or -not $overview.Contains($heading)) {
            [void]$issues.Add("packs/README.md: missing canonical pack heading '$heading'")
        }
        $source = @("packs/$($pack.slug)/README.md", "packs/$($pack.slug)/README.txt") |
            Where-Object { Test-Path -LiteralPath (Join-Path $root $_) } |
            Select-Object -First 1
        if (-not $source) {
            [void]$issues.Add("Missing README for '$($pack.name)'")
        } else {
            $copy = Read-Document $source
            if ($null -eq $copy -or -not $copy.Contains([string]$pack.name) -or -not $copy.Contains([string]$pack.version)) {
                [void]$issues.Add("$source must identify '$($pack.name)' and version '$($pack.version)'")
            }
        }
    }
}

if (Test-Path -LiteralPath (Join-Path $root 'README.id.md')) {
    $tagUrl = "https://github.com/secondshift-dv/naut/releases/tag/v$version"
    foreach ($relative in @('README.md','README.de.md','README.es.md','README.fr.md','README.id.md','README.ja.md','README.ko.md','README.zh-Hans.md','docs/getting-started.md')) {
        $copy = Read-Document $relative
        if ($null -eq $copy -or -not $copy.Contains($tagUrl)) {
            [void]$issues.Add("$($relative): current download URL must target v$version")
        }
        if ($null -ne $copy -and $copy -match ('github\.com/secondshift-dv/naut/releases/tag/v(?!' + [regex]::Escape($version) + '\b)\d+\.\d+\.\d+')) {
            [void]$issues.Add("$($relative): stale GitHub release download link")
        }
    }
}

# The source notice is version-neutral; exact component inventory is packaged.
$noticePath = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
    [void]$issues.Add('THIRD-PARTY-NOTICES.txt: source compliance reference is missing')
} else {
    $notice = Read-Document 'THIRD-PARTY-NOTICES.txt'
    if ($null -ne $notice) {
        foreach ($marker in @(
            'Third-Party Runtime Notices',
            'runtime/THIRD-PARTY-NOTICES.txt',
            'runtime/LICENSES/',
            'scripts/packaging/package-win-x64.ps1',
            'scripts/packaging/runtime-third-party.lock.json',
            'scripts/packaging/ffmpeg/ffmpeg-runtime.lock.json',
            'release-contract.json'
        )) {
            if (-not $notice.Contains($marker)) {
                [void]$issues.Add("THIRD-PARTY-NOTICES.txt: missing authority '$marker'")
            }
        }
        if ($notice -match '(?i)\bnaut\s+v\d+\.\d+\.\d+\b') {
            [void]$issues.Add('THIRD-PARTY-NOTICES.txt: static source reference must not pin a product version')
        }
    }
}

# HTML references are also used by the localized GitHub README pages.
foreach ($relative in $documents) {
    $content = Read-Document $relative
    if ($null -eq $content) { continue }
    foreach ($link in [regex]::Matches($content, '(?i)\b(?:href|src|srcset)\s*=\s*["''](?<value>[^"'']+)["'']')) {
        foreach ($target in ($link.Groups['value'].Value -split '\s*,\s*')) {
            $candidate = ($target -split '\s+', 2)[0]
            if ($candidate -match '^(?:[a-z][a-z0-9+.-]*:|//|/|#)' -or -not $candidate) { continue }
            $pathOnly = ($candidate -split '[?#]', 2)[0]
            if (-not $pathOnly) { continue }
            $directory = Split-Path -Parent (Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
            try {
                $resolved = Join-Path $directory ([Uri]::UnescapeDataString($pathOnly))
                if (-not (Test-Path -LiteralPath $resolved)) {
                    [void]$issues.Add("$($relative): broken HTML local reference '$candidate'")
                } elseif ($candidate.Contains('#') -and
                          -not (Test-MarkdownAnchor -file $resolved -fragment (($candidate -split '#', 2)[1]))) {
                    [void]$issues.Add("$($relative): missing HTML-linked Markdown anchor '$candidate'")
                }
            } catch {
                [void]$issues.Add("$($relative): invalid HTML local reference '$candidate'")
            }
        }
    }
}

# Public issue templates must not suggest an old product version.
$bugTemplatePath = Join-Path $root '.github/ISSUE_TEMPLATE/bug.yml'
if (Test-Path -LiteralPath $bugTemplatePath -PathType Leaf) {
    $bugTemplate = Read-Document '.github/ISSUE_TEMPLATE/bug.yml'
    if ($null -ne $bugTemplate -and $bugTemplate -match '(?m)^\s*placeholder:\s*v?\d+\.\d+\.\d+\s*$') {
        [void]$issues.Add('.github/ISSUE_TEMPLATE/bug.yml: version placeholder must be neutral')
    }
}

$requirementsPath = Join-Path $root 'docs/system-requirements.md'
if (Test-Path -LiteralPath $requirementsPath -PathType Leaf) {
    $requirements = Read-Document 'docs/system-requirements.md'
    if ($null -ne $requirements -and $requirements -notmatch ('(?m)^# Naut v' + [regex]::Escape($version) + ' System Requirements\s*$')) {
        [void]$issues.Add("docs/system-requirements.md: title must match v$version")
    }
}

# Keep stable user navigation separate from version-specific release history.
$navigationPath = Join-Path $root 'docs/README.md'
if (Test-Path -LiteralPath $navigationPath -PathType Leaf) {
    $navigation = Read-Document 'docs/README.md'
    if ($null -ne $navigation) {
        $releaseNavigation = @([regex]::Matches($navigation, '(?m)^-\s+\[Release Notes\]\(releases/README\.md\)'))
        if ($releaseNavigation.Count -ne 1) {
            [void]$issues.Add('docs/README.md: exactly one Release Notes link to releases/README.md is required')
        }
        if ($navigation -match '(?i)\breleases/v\d+\.\d+\.\d+\.md\b') {
            [void]$issues.Add('docs/README.md: individual versions belong only in docs/releases/README.md')
        }
        $usingNaut = [regex]::Match($navigation, '(?ms)^## Using Naut\s*\r?\n(?<items>.*?)(?=^## |\z)')
        if (-not $usingNaut.Success) {
            [void]$issues.Add('docs/README.md: missing Using Naut navigation')
        } else {
            $entries = @([regex]::Matches($usingNaut.Groups['items'].Value, '(?m)^-\s+\[(?<label>[^\]]+)\]\([^)]+\)') |
                ForEach-Object { $_.Groups['label'].Value })
            if ($entries.Count -lt 2 -or $entries[0] -cne 'Getting Started' -or
                $entries[1] -cne 'FAQ & Troubleshooting') {
                [void]$issues.Add('docs/README.md: Getting Started and FAQ & Troubleshooting must be the first two navigation links')
            }
            if ($entries -cnotcontains 'Release Notes') {
                [void]$issues.Add('docs/README.md: Release Notes must appear in the user navigation')
            }
        }
    }
}

$releaseIndex = Read-Document 'docs/releases/README.md'
$releaseFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'docs/releases') -Filter 'v*.md' -File |
    Where-Object { $_.Name -cmatch '^v\d+\.\d+\.\d+\.md$' } |
    Sort-Object -Property @{ Expression = { [version]($_.BaseName.Substring(1)) }; Descending = $true })
$expectedNotes = @($releaseFiles | ForEach-Object { $_.Name })
if ($expectedNotes.Count -eq 0 -or $expectedNotes[0] -cne "v$version.md") {
    [void]$issues.Add("docs/releases: newest notes must correspond to ProductVersion v$version")
}
if ($null -ne $releaseIndex) {
    $releaseEntries = @([regex]::Matches($releaseIndex, '(?m)^-\s+\[v(?<version>\d+\.\d+\.\d+)\]\((?<file>v\d+\.\d+\.\d+\.md)\)\s*$'))
    $listedNotes = @($releaseEntries | ForEach-Object { $_.Groups['file'].Value })
    $allNoteLinks = @([regex]::Matches($releaseIndex, '\]\(v\d+\.\d+\.\d+\.md\)'))
    if ($allNoteLinks.Count -ne $releaseEntries.Count) {
        [void]$issues.Add('docs/releases/README.md: use one canonical [vX.Y.Z](vX.Y.Z.md) entry per release')
    }
    foreach ($entry in $releaseEntries) {
        if ($entry.Groups['file'].Value -cne "v$($entry.Groups['version'].Value).md") {
            [void]$issues.Add('docs/releases/README.md: release link label and filename must match')
        }
    }
    if (($listedNotes -join '|') -cne ($expectedNotes -join '|')) {
        [void]$issues.Add('docs/releases/README.md: list every versioned note exactly once, newest first')
    }
}

if ($issues.Count -gt 0) {
    foreach ($issue in $issues) { [Console]::Error.WriteLine($issue) }
    throw "Documentation verification failed: $($issues.Count) issue(s)."
}
Write-Output "DOCUMENTATION=PASS; files=$($documents.Count); version=$version"
