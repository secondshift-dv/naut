Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-FfmpegConfiguration {
    param([Parameter(Mandatory)][string]$Configuration)
    foreach ($forbidden in @('--enable-chromaprint', '--enable-gpl', '--enable-nonfree')) {
        if ($Configuration -match ('(?<!\S)' + [regex]::Escape($forbidden) + '(?:=|\s|$)')) { throw "FFmpeg forbidden configuration: $forbidden" }
    }
    foreach ($required in @('--disable-chromaprint', '--enable-libopenh264', '--enable-version3', '--disable-autodetect')) {
        if ($Configuration -notmatch ('(?<!\S)' + [regex]::Escape($required) + '(?:\s|$)')) { throw "FFmpeg required configuration absent: $required" }
    }
}

function Assert-FfmpegPolicy {
    param(
        [Parameter(Mandatory)][string]$FfmpegPath,
        [Parameter(Mandatory)][string]$FfprobePath,
        [Parameter(Mandatory)][string]$ProvenancePath,
        [Parameter(Mandatory)][string]$LicensePath,
        [Parameter(Mandatory)][string]$ArchiveSha256,
        [Parameter(Mandatory)][string]$LockPath
    )
    if (-not (Test-Path -LiteralPath $ProvenancePath -PathType Leaf)) { throw 'FFmpeg exact build provenance is missing.' }
    $lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
    if ($lock.schemaVersion -ne 1 -or $ArchiveSha256 -cne $lock.archiveSha256 -or
        (Get-FileHash -LiteralPath $ProvenancePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lock.provenanceSha256) {
        throw 'FFmpeg archive/provenance differs from independent source lock.'
    }
    $proof = Get-Content -LiteralPath $ProvenancePath -Raw | ConvertFrom-Json
    if ($proof.schemaVersion -ne 1 -or $proof.license -ne 'LGPL-3.0-or-later' -or
        $proof.recipeCommit -notmatch '^[0-9a-f]{40}$' -or $ArchiveSha256 -notmatch '^[0-9a-f]{64}$' -or
        $proof.sourceCompanionSha256 -notmatch '^[0-9a-f]{64}$' -or
        $proof.inputs.container -notmatch '@sha256:[0-9a-f]{64}$') { throw 'FFmpeg provenance identity/hash/license is invalid.' }
    foreach ($source in $proof.inputs.sources) {
        if ($source.commit -notmatch '^[0-9a-f]{40}$' -or $source.sha256 -notmatch '^[0-9a-f]{64}$') {
            throw 'FFmpeg source revision/archive record is missing or invalid.'
        }
    }
    foreach ($name in @('ffmpeg', 'btbn', 'openh264', 'zlib', 'dav1d')) {
        if (@($proof.inputs.sources | Where-Object name -eq $name).Count -ne 1) { throw "FFmpeg exact source record missing: $name" }
    }
    $source = @($proof.inputs.sources | Where-Object name -eq 'ffmpeg')[0]
    if ($source.commit -cne $lock.sourceCommit -or $proof.recipeCommit -cne $lock.recipeCommit -or
        @($proof.inputs.sources | Where-Object name -eq 'btbn')[0].commit -cne $lock.btbnCommit -or
        $proof.license -cne $lock.license) { throw 'FFmpeg exact source/recipe/license authority differs from the lock.' }
    foreach ($recipe in @('build-ffmpeg.sh', 'ffmpeg-build.py', 'ffmpeg-build-inputs.json')) {
        if ($proof.recipeHashes.$recipe -notmatch '^[0-9a-f]{64}$' -or $proof.recipeHashes.$recipe -cne $lock.recipeHashes.$recipe) { throw "FFmpeg recipe provenance missing or changed: $recipe" }
    }
    $licensesRoot = Split-Path -Parent $LicensePath
    foreach ($requiredNotice in @('FFmpeg-LGPL-3.0.txt', 'FFmpeg-GPL-3.0.txt', 'OpenH264-BSD-2-Clause.txt', 'zlib-LICENSE.txt', 'dav1d-BSD-2-Clause.txt', 'dav1d-PATENTS.txt', 'BtbN-FFmpeg-Builds-LICENSE.txt', 'Source-Copyright-Notices.txt', 'Debian-gcc-12-base-copyright.txt', 'Debian-mingw-w64-common-copyright.txt')) {
        if ($proof.licenseHashes.$requiredNotice -notmatch '^[0-9a-f]{64}$') { throw "Required FFmpeg notice provenance missing: $requiredNotice" }
    }
    foreach ($notice in $proof.licenseHashes.PSObject.Properties) {
        if ($notice.Value -notmatch '^[0-9a-f]{64}$' -or
            (Get-FileHash -LiteralPath (Join-Path $licensesRoot $notice.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $notice.Value) { throw "Required FFmpeg notice missing or changed: $($notice.Name)" }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $licensesRoot 'FFmpeg-THIRD-PARTY-NOTICES.txt') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lock.noticesSha256) { throw 'FFmpeg third-party notices missing or changed.' }
    foreach ($tool in @($FfmpegPath, $FfprobePath)) {
        $name = [IO.Path]::GetFileName($tool)
        $hash = (Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $proof.binaryHashes.$name) { throw "FFmpeg binary provenance mismatch: $name" }
    }
    $config = (& $FfmpegPath -hide_banner -buildconf 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg build configuration query failed.' }
    Assert-FfmpegConfiguration -Configuration $config
    foreach ($tool in @($FfmpegPath, $FfprobePath)) {
        $name = [IO.Path]::GetFileName($tool)
        $version = (& $tool -hide_banner -version 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $version -notmatch [regex]::Escape($source.commit.Substring(0, 10))) {
            throw "FFmpeg source revision not reported by $name"
        }
        $configuration = [regex]::Match($version, '(?m)^configuration:\s*(.+)$').Groups[1].Value.Trim()
        if ($configuration -cne $lock.configuration) { throw "FFmpeg build configuration drift: $name" }
        $reported = (& $tool -hide_banner -L 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $reported -notmatch '(?s)GNU Lesser General Public\s+License.*?either version 3 of the License.*?any later version' -or
            $reported -match '(?<!Lesser )GNU General Public License') { throw "FFmpeg binary-reported license differs from metadata: $name" }
    }
    $licenseHash = (Get-FileHash -LiteralPath $LicensePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($licenseHash -cne $proof.licenseHashes.'FFmpeg-LGPL-3.0.txt') { throw 'FFmpeg packaged license text differs from pinned source license.' }
    $encoders = (& $FfmpegPath -hide_banner -encoders 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $encoders -notmatch '(?m)^\s*V[^\r\n]*\blibopenh264\b' -or
        $encoders -notmatch '(?m)^\s*V[^\r\n]*\bmjpeg\b' -or
        $encoders -notmatch '(?m)^\s*V[^\r\n]*\brawvideo\b' -or
        $encoders -notmatch '(?m)^\s*V[^\r\n]*\bbmp\b') { throw 'FFmpeg required hover/MJPEG/BMP/rawvideo encoder absent.' }
    $decoders = (& $FfmpegPath -hide_banner -decoders 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg decoder inventory query failed.' }
    foreach ($decoder in @('h264', 'hevc', 'mpeg4', 'mjpeg', 'vp8', 'vp9', 'libdav1d')) {
        if ($decoders -notmatch ('(?m)^\s*V[^\r\n]*\b' + [regex]::Escape($decoder) + '\b')) { throw "Required FFmpeg decoder absent: $decoder" }
    }
    $muxers = (& $FfmpegPath -hide_banner -muxers 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $muxers -match '(?m)^\s*E\s+chromaprint\b') { throw 'FFmpeg Chromaprint muxer is forbidden.' }
    foreach ($entry in $proof.linkedArchives.PSObject.Properties) {
        if ($entry.Name -match 'chromaprint|fftw|x264|x265|xvid|vidstab|rubberband' -or
            $entry.Value.source -notin @('ffmpeg', 'openh264', 'zlib', 'dav1d', 'debian-toolchain-runtime') -or
            $entry.Value.sha256 -notmatch '^[0-9a-f]{64}$') { throw "FFmpeg unapproved linked archive: $($entry.Name)" }
    }
    if (@($proof.linkedArchives.PSObject.Properties).Count -eq 0) { throw 'FFmpeg linked library inventory is missing.' }
    if ($proof.sourceCompanionSha256 -cne $lock.sourceCompanionSha256) { throw 'FFmpeg corresponding-source provenance drift.' }
    return $proof
}
