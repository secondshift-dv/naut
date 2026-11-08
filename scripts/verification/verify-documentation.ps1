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
    $_ -in @('CONTRIBUTING.md','SECURITY.md','BRAND-POLICY.md','.github/PULL_REQUEST_TEMPLATE.md')
})

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
        $target = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        $target = ($target -split '#', 2)[0]
        if (-not $target -or $target -match '^(?:[a-z][a-z0-9+.-]*:|//|/|#)' ) { continue }
        $resolved = Join-Path $directory ([Uri]::UnescapeDataString($target))
        if (-not (Test-Path -LiteralPath $resolved)) {
            [void]$issues.Add("$($relative): broken relative link '$target'")
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
    $index = Read-Document 'docs/README.md'
    if ($null -eq $index -or -not $index.Contains("[Naut v$version release notes](releases/v$version.md)")) {
        [void]$issues.Add("docs/README.md: current release must lead the release list (v$version)")
    }
}

if ($issues.Count -gt 0) {
    foreach ($issue in $issues) { [Console]::Error.WriteLine($issue) }
    throw "Documentation verification failed: $($issues.Count) issue(s)."
}
Write-Output "DOCUMENTATION=PASS; files=$($documents.Count); version=$version"
