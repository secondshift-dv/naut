[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][string]$OpenCvFfmpegSourceCompanionPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$lockPath = Join-Path $repoRoot 'scripts/packaging/runtime-third-party.lock.json'
$licenseSourceRoot = Join-Path $repoRoot 'scripts/packaging/licenses'
$runtime = Join-Path $InstallRoot 'runtime'
$licenses = Join-Path $runtime 'LICENSES'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json

if ($lock.schemaVersion -ne 1) { throw 'Runtime third-party lock schema is invalid.' }

function Get-Sha256Lower {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Hash {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Label
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label is missing: $Path" }
    $actual = Get-Sha256Lower -Path $Path
    if ($actual -cne $Expected) { throw "$Label SHA-256 mismatch. Expected $Expected but got $actual." }
}

function Get-PackageRoot {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$Version
    )
    $nugetRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
    }
    else {
        Join-Path $env:USERPROFILE '.nuget/packages'
    }
    $root = Join-Path $nugetRoot "$($PackageId.ToLowerInvariant())/$Version"
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Restored NuGet package is missing: $PackageId $Version"
    }
    return $root
}

function Get-UniqueFile {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Label
    )
    $matches = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $Name)
    if ($matches.Count -ne 1) { throw "$Label expected exactly one '$Name'; got $($matches.Count)." }
    return $matches[0]
}

# Tracked notices produced by the licensing audit are immutable inputs.
foreach ($notice in @(
    @{ File = [string]$lock.opencvFfmpeg.licenseFile; Hash = [string]$lock.opencvFfmpeg.licenseSha256; Label = 'OpenCV FFmpeg license' },
    @{ File = [string]$lock.icu.licenseFile; Hash = [string]$lock.icu.licenseSha256; Label = 'ICU license' },
    @{ File = [string]$lock.unoFonts.licenseFile; Hash = [string]$lock.unoFonts.licenseSha256; Label = 'Uno font license' }
)) {
    Assert-Hash -Path (Join-Path $licenseSourceRoot $notice.File) -Expected $notice.Hash -Label "$($notice.Label) source"
    Assert-Hash -Path (Join-Path $licenses $notice.File) -Expected $notice.Hash -Label "$($notice.Label) packaged copy"
}

# LGPL source availability for OpenCV's separately shipped videoio FFmpeg plugin.
Assert-Hash -Path $OpenCvFfmpegSourceCompanionPath -Expected ([string]$lock.opencvFfmpeg.sourceCompanionSha256) -Label 'OpenCV FFmpeg corresponding source'

$openCvRoot = Get-PackageRoot -PackageId ([string]$lock.opencvFfmpeg.packageId) -Version ([string]$lock.opencvFfmpeg.packageVersion)
$openCvPackagePlugin = Get-UniqueFile -Root $openCvRoot -Name ([string]$lock.opencvFfmpeg.deployedName) -Label 'OpenCV FFmpeg package'
Assert-Hash -Path $openCvPackagePlugin.FullName -Expected ([string]$lock.opencvFfmpeg.sha256) -Label 'OpenCV FFmpeg package binary'
$openCvInstalledPlugin = Get-UniqueFile -Root $runtime -Name ([string]$lock.opencvFfmpeg.deployedName) -Label 'OpenCV FFmpeg installed runtime'
Assert-Hash -Path $openCvInstalledPlugin.FullName -Expected ([string]$lock.opencvFfmpeg.sha256) -Label 'OpenCV FFmpeg installed binary'

$nuspec = Get-UniqueFile -Root $openCvRoot -Name '*.nuspec' -Label 'OpenCV runtime package metadata'
$nuspecText = Get-Content -LiteralPath $nuspec.FullName -Raw
if ($nuspecText -notmatch [regex]::Escape([string]$lock.opencvFfmpeg.packageRepositoryCommit)) {
    throw 'OpenCV runtime NuGet repository commit differs from the compliance lock.'
}

# Native runtime binaries audited from the actual package payload.
foreach ($entry in $lock.icu.binaryHashes.PSObject.Properties) {
    $path = Join-Path $runtime $entry.Name
    Assert-Hash -Path $path -Expected ([string]$entry.Value) -Label "ICU runtime $($entry.Name)"
}
Assert-Hash -Path (Join-Path $runtime ([string]$lock.skiaSharp.binaryName)) -Expected ([string]$lock.skiaSharp.binarySha256) -Label 'SkiaSharp native runtime'
Assert-Hash -Path (Join-Path $runtime ([string]$lock.harfBuzzSharp.binaryName)) -Expected ([string]$lock.harfBuzzSharp.binarySha256) -Label 'HarfBuzzSharp native runtime'

# Package license/notices must match both the lock and the packaged copies.
foreach ($component in @(
    @{ Name='SkiaSharp'; Id=[string]$lock.skiaSharp.packageId; Version=[string]$lock.skiaSharp.packageVersion; License=[string]$lock.skiaSharp.licenseSha256; Notices=[string]$lock.skiaSharp.noticesSha256 },
    @{ Name='HarfBuzzSharp'; Id=[string]$lock.harfBuzzSharp.packageId; Version=[string]$lock.harfBuzzSharp.packageVersion; License=[string]$lock.harfBuzzSharp.licenseSha256; Notices=[string]$lock.harfBuzzSharp.noticesSha256 },
    @{ Name='DotNet-Runtime'; Id=[string]$lock.dotnetRuntime.packageId; Version=[string]$lock.dotnetRuntime.packageVersion; License=[string]$lock.dotnetRuntime.licenseSha256; Notices=[string]$lock.dotnetRuntime.noticesSha256 }
)) {
    $root = Get-PackageRoot -PackageId $component.Id -Version $component.Version
    $licenseName = if ($component.Name -eq 'DotNet-Runtime') { 'LICENSE.TXT' } else { 'LICENSE.txt' }
    $noticesName = if ($component.Name -eq 'DotNet-Runtime') { 'THIRD-PARTY-NOTICES.TXT' } else { 'THIRD-PARTY-NOTICES.txt' }
    Assert-Hash -Path (Join-Path $root $licenseName) -Expected $component.License -Label "$($component.Name) package license"
    Assert-Hash -Path (Join-Path $root $noticesName) -Expected $component.Notices -Label "$($component.Name) package notices"
    Assert-Hash -Path (Join-Path $licenses "$($component.Name)-LICENSE.txt") -Expected $component.License -Label "$($component.Name) packaged license"
    Assert-Hash -Path (Join-Path $licenses "$($component.Name)-THIRD-PARTY-NOTICES.txt") -Expected $component.Notices -Label "$($component.Name) packaged notices"
}

# Uno font assets must be byte-for-byte the files from the exact locked NuGet packages.
$unoFontCount = 0
foreach ($package in @($lock.unoFonts.packages)) {
    $root = Get-PackageRoot -PackageId ([string]$package.id) -Version ([string]$package.version)
    $fonts = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Extension -in @('.ttf', '.otf') })
    if ($fonts.Count -eq 0) { throw "No font assets were found in $($package.id) $($package.version)." }
    foreach ($sourceFont in $fonts) {
        $installed = @(Get-ChildItem -LiteralPath $runtime -Recurse -File -Filter $sourceFont.Name |
            Where-Object { $_.FullName -match 'Uno\.Fonts\.(OpenSans|Fluent)' })
        if ($installed.Count -ne 1) {
            throw "Expected exactly one packaged Uno font '$($sourceFont.Name)'; got $($installed.Count)."
        }
        $sourceHash = Get-Sha256Lower -Path $sourceFont.FullName
        Assert-Hash -Path $installed[0].FullName -Expected $sourceHash -Label "Uno font $($sourceFont.Name)"
        $unoFontCount++
    }
}
if ($unoFontCount -ne 38) { throw "Locked Uno font inventory expected 38 files; verified $unoFontCount." }

# Built-in presentation fonts retain their own file-level provenance and OFL copies.
$builtInSource = Join-Path $repoRoot 'src/Neuterradise.App/Assets/Presentation/BuiltIn/Fonts'
$builtInRuntime = Join-Path $runtime 'Assets/Presentation/BuiltIn/Fonts'
$fontProof = Get-Content -LiteralPath (Join-Path $builtInSource 'provenance.json') -Raw | ConvertFrom-Json
$runtimeBuiltInFonts = @(Get-ChildItem -LiteralPath $builtInRuntime -File | Where-Object { $_.Extension -in @('.ttf', '.otf') })
if ($runtimeBuiltInFonts.Count -ne @($fontProof.fonts).Count) {
    throw "Built-in font inventory drift. Expected $(@($fontProof.fonts).Count), got $($runtimeBuiltInFonts.Count)."
}
foreach ($font in @($fontProof.fonts)) {
    Assert-Hash -Path (Join-Path $builtInRuntime ([string]$font.file)) -Expected ([string]$font.sha256) -Label "Built-in font $($font.file)"
    $sourceLicense = Join-Path $builtInSource ([string]$font.license)
    $runtimeLicense = Join-Path $builtInRuntime ([string]$font.license)
    $expectedLicenseHash = Get-Sha256Lower -Path $sourceLicense
    Assert-Hash -Path $runtimeLicense -Expected $expectedLicenseHash -Label "Built-in font license $($font.license)"
}
$sourceProofHash = Get-Sha256Lower -Path (Join-Path $builtInSource 'provenance.json')
Assert-Hash -Path (Join-Path $builtInRuntime 'provenance.json') -Expected $sourceProofHash -Label 'Built-in font provenance'

Write-Host 'RUNTIME_NATIVE_LICENSE_INVENTORY=PASS'
Write-Host 'OPENCV_FFMPEG_CORRESPONDING_SOURCE=PASS'
Write-Host 'UNO_FONTS_VERIFIED=38'
Write-Host "BUILTIN_FONTS_VERIFIED=$(@($fontProof.fonts).Count)"
