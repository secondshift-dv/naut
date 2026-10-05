[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][string]$SourceCompanionPath,
    [string]$FixtureRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repoRoot 'scripts/packaging/ffmpeg-policy.ps1')
$lockPath = Join-Path $repoRoot 'scripts/packaging/ffmpeg/ffmpeg-runtime.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$runtime = Join-Path $InstallRoot 'runtime'
$ffmpeg = Join-Path $runtime 'tools/ffmpeg.exe'
$ffprobe = Join-Path $runtime 'tools/ffprobe.exe'
$proof = Assert-FfmpegPolicy -FfmpegPath $ffmpeg -FfprobePath $ffprobe -ProvenancePath (Join-Path $runtime 'deployment/ffmpeg-provenance.json') -LicensePath (Join-Path $runtime 'LICENSES/FFmpeg-LGPL-3.0.txt') -ArchiveSha256 $lock.archiveSha256 -LockPath $lockPath
foreach ($notice in $proof.licenseHashes.PSObject.Properties) {
    $path = Join-Path $runtime ('LICENSES/' + $notice.Name)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $notice.Value) { throw "Required FFmpeg license/copyright notice drift: $($notice.Name)" }
}
if ((Get-FileHash -LiteralPath (Join-Path $runtime 'LICENSES/FFmpeg-THIRD-PARTY-NOTICES.txt') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lock.noticesSha256) { throw 'FFmpeg third-party notices drift.' }
$deployment = Get-Content -LiteralPath (Join-Path $runtime 'deployment/artifacts.json') -Raw | ConvertFrom-Json
foreach ($tool in @('ffmpeg', 'ffprobe')) {
    $matches = @($deployment.artifacts | Where-Object {
        $null -ne $_.PSObject.Properties['toolId'] -and $_.toolId -eq $tool
    })
    if ($matches.Count -ne 1 -or $matches[0].license -ne $proof.license -or
        $matches[0].sha256 -cne $proof.binaryHashes.($tool+'.exe')) { throw "FFmpeg deployment metadata drift: $tool" }
}
$source = @($deployment.sources | Where-Object {
    $null -ne $_.PSObject.Properties['sourceId'] -and $_.sourceId -eq 'naut-ffmpeg-8.1.3-win64-lgpl'
})
if ($source.Count -ne 1 -or $source[0].license -ne $proof.license -or $source[0].sha256 -cne $lock.archiveSha256) { throw 'FFmpeg deployment source metadata drift.' }
if ((Get-FileHash -LiteralPath $SourceCompanionPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lock.sourceCompanionSha256) { throw 'FFmpeg corresponding-source archive hash mismatch.' }
$zip = [IO.Compression.ZipFile]::OpenRead($SourceCompanionPath)
try {
    foreach ($sourceInput in $proof.inputs.sources) {
        if ([string]::IsNullOrWhiteSpace($sourceInput.archive) -or
            $null -eq $zip.GetEntry($sourceInput.archive) -or
            $proof.sourceFiles.($sourceInput.archive) -cne $sourceInput.sha256) {
            throw "Declared exact source archive missing or hash record inconsistent: $($sourceInput.name)"
        }
    }
    foreach ($file in $proof.sourceFiles.PSObject.Properties) {
        $entry = $zip.GetEntry($file.Name)
        if ($null -eq $entry) { throw "Corresponding source input missing: $($file.Name)" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($hash -cne $file.Value) { throw "Corresponding source input hash mismatch: $($file.Name)" }
    }
    foreach ($required in @('evidence/config-1.mak', 'evidence/config-2.mak', 'evidence/ffmpeg-1.map', 'evidence/ffmpeg-2.map', 'evidence/ffprobe-1.map', 'evidence/ffprobe-2.map', 'evidence/debian-packages.tsv', 'evidence/compiler.txt', 'build-ffmpeg.sh', 'ffmpeg-build.py', 'ffmpeg-build-inputs.json', 'recipe-commit.txt')) {
        $entry = $zip.GetEntry($required)
        if ($null -eq $entry -or $entry.Length -eq 0) { throw "Corresponding build provenance missing: $required" }
    }
}
finally { $zip.Dispose() }
if ([string]::IsNullOrWhiteSpace($FixtureRoot)) { $FixtureRoot = Join-Path $repoRoot 'out/ffmpeg-check' }
New-Item -ItemType Directory -Path $FixtureRoot -Force | Out-Null
$inputPath = Join-Path $FixtureRoot 'input.mp4'
$jpegPath = Join-Path $FixtureRoot 'frame.jpg'
$hoverPath = Join-Path $FixtureRoot 'hover.mp4'
function Invoke-FfmpegChecked([string[]]$Arguments) {
    & $ffmpeg @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg capability command failed.' }
}
Invoke-FfmpegChecked @('-nostdin','-hide_banner','-v','error','-y','-f','lavfi','-i','testsrc2=size=160x90:rate=10:duration=2','-an','-c:v','mpeg4','-pix_fmt','yuv420p',$inputPath)
Invoke-FfmpegChecked @('-nostdin','-hide_banner','-v','error','-y','-i',$inputPath,'-frames:v','1','-an','-c:v','mjpeg','-threads','2',$jpegPath)
Invoke-FfmpegChecked @('-nostdin','-hide_banner','-v','error','-y','-i',$inputPath,'-t','5.8','-map','0:v:0','-an','-c:v','libopenh264','-threads','2','-pix_fmt','yuv420p','-movflags','+faststart',$hoverPath)
$combinedJpeg = Join-Path $FixtureRoot 'combined.jpg'
$combinedHover = Join-Path $FixtureRoot 'combined.mp4'
$filter = "[0:v]setpts=PTS-STARTPTS,split=2[thumbsrc][hoversrc];[thumbsrc]select='gte(t,0)',scale='min(1024,iw)':'min(1024,ih)':force_original_aspect_ratio=decrease[thumb];[hoversrc]scale='min(1280,iw)':'min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p[hover]"
Invoke-FfmpegChecked @('-nostdin','-hide_banner','-v','error','-y','-ss','0','-i',$inputPath,'-filter_complex',$filter,'-map','[thumb]','-frames:v','1','-an','-c:v','mjpeg','-threads','2',$combinedJpeg,'-map','[hover]','-t','5.8','-an','-c:v','libopenh264','-threads','2','-movflags','+faststart',$combinedHover)
$probeOutput = (& $ffprobe -v error -show_streams -show_format -of json $hoverPath | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'FFprobe metadata capability failed.' }
$metadata = $probeOutput | ConvertFrom-Json
if (@($metadata.streams).Count -ne 1 -or $metadata.streams[0].codec_name -ne 'h264' -or [double]$metadata.format.duration -gt 6) { throw 'Hover metadata violates Naut encoder contract.' }
Invoke-FfmpegChecked @('-nostdin','-hide_banner','-v','error','-i',$hoverPath,'-f','null','-')
if ((Get-Item -LiteralPath $jpegPath).Length -le 0) { throw 'MJPEG frame encoding produced no image.' }
$bmpPipe = Join-Path $FixtureRoot 'decode-pipe.bmp'
Invoke-FfmpegChecked @('-nostdin','-hide_banner','-v','error','-y','-i',$hoverPath,'-an','-vf','fps=30','-frames:v','1','-f','image2pipe','-vcodec','bmp',$bmpPipe)
$bmpBytes = [IO.File]::ReadAllBytes($bmpPipe)
if ($bmpBytes.Length -le 54 -or $bmpBytes[0] -ne 0x42 -or $bmpBytes[1] -ne 0x4d -or
    [BitConverter]::ToUInt32($bmpBytes,2) -ne $bmpBytes.Length) { throw 'Live decode BMP/image2pipe contract produced an invalid frame.' }

Write-Host 'FFMPEG_PROBE_DECODE_BMP_MJPEG_OPENH264=PASS'
Write-Host 'FFMPEG_COPYRIGHT_SOURCE_COMPLIANCE=PASS'
Write-Host 'OPENH264_PATENT_SCOPE=NO_CISCO_BINARY_PATENT_GRANT_CLAIMED'
