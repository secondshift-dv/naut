[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [string]$NotesPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[xml]$props = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
$version = $props.SelectSingleNode('/Project/PropertyGroup/ProductVersion').InnerText
if ([string]::IsNullOrWhiteSpace($NotesPath)) {
    $NotesPath = Join-Path $RepositoryRoot "docs/releases/v$version.md"
}
if (-not (Test-Path -LiteralPath $NotesPath -PathType Leaf)) {
    throw "Release notes are required: docs/releases/v$version.md. See docs/release-notes.md."
}
$text = [IO.File]::ReadAllText($NotesPath).Replace("`r`n", "`n")
if ($text -notmatch ('(?m)^# Naut v' + [regex]::Escape($version) + '\s*$')) {
    throw 'Release notes title must match ProductVersion in Directory.Build.props.'
}
if ($text -match '(?i)\b(TODO|TBD|FIXME)\b|<!--') {
    throw 'Release notes must contain finished copy without placeholders or template comments.'
}
foreach ($heading in @('Highlights', 'Download and update', 'Notes')) {
    $section = [regex]::Match($text, '(?ms)^## ' + [regex]::Escape($heading) + '\s*\n(.*?)(?=^## |\z)')
    if (-not $section.Success -or $section.Groups[1].Value.Trim() -notmatch '[A-Za-z]') {
        throw "Release notes require a populated '$heading' section."
    }
}
$highlights = [regex]::Match($text, '(?ms)^## Highlights\s*\n(.*?)(?=^## |\z)').Groups[1].Value
if ($highlights -notmatch '(?m)^- \S') {
    throw 'Highlights must include at least one concrete user-facing change.'
}
$download = "https://github.com/secondshift-dv/naut/releases/download/v$version/naut-v$version-win-x64.zip"
$downloadSection = [regex]::Match($text, '(?ms)^## Download and update\s*\n(.*?)(?=^## |\z)').Groups[1].Value
if (-not $downloadSection.Contains($download)) {
    throw 'Download and update must link to the ZIP for this exact release version.'
}
Write-Output "RELEASE_NOTES=v$version PASS"
