[CmdletBinding()]
param([string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$files = (& git -C $root ls-files -z --cached --others --exclude-standard) -split '\x00'
if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory files for privacy verification.' }

$accountPath = '(?i)(?:\b[a-z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|runner(?:admin)?\b)|/(?:home|Users)/(?!(?:runner|Shared)/))[\p{L}\p{Nd}_. -]+[\\/]'
$secret = '-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----|\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,})\b'
$email = '(?i)(?<![\w.+-])[\w.!#$%&+~-]+@(?:[\w-]+\.)+[a-z]{2,}'
$contact = '(?i)\b(?:tel:\s*\+?\d|(?:wa\.me|api\.whatsapp\.com)/[^\s)>]*\d{7,}|\+62(?:[ .()-]*\d){8,13}(?!\d)|(?:phone|telephone|mobile|contact|nomor hp|nomor telepon)\s*[:=]\s*(?:\+?\d[ .()-]*){8,15}(?!\d))'
$personalFile = '(?i)(?<![\p{L}\p{Nd}_])(?:multiview_\d+_[0-9a-f]{8}-[0-9a-f-]{27,}|(?:img|dsc|pxl|vid|screenshot)[_-]\d{8}(?:[_-]\d{4,})?(?:[-_][\p{L}\p{Nd}]+)*)\.(?:jpe?g|png|heic|webp|mp4|mov)\b'
$managedFile = '(?i)(?<![\p{L}\p{Nd}_])(?<profile>[\p{L}\p{Nd}_.-]+?)_{1,2}(?:image|video|model)_{1,2}[0-9a-f]{8}\.[a-z0-9]{2,5}\b'
$neutralProfiles = @('SampleProfile', 'ExampleProfile', 'TestProfile', 'FixtureProfile', 'SyntheticProfile', 'ProfileName')
# Existing attribution contacts are allowed only in the exact upstream notice.
$licensedContacts = @{
    'scripts/packaging/licenses/ICU-77.1-LICENSE.txt' = '451167c55c0fa447cc2d5632714f5e3c567fe4f1e1badefab2c1333852198aca'
    'src/Naut.Showcase/public/third-party-assets/licenses/CC-BY-2.0.html' = 'a949dd5d7f931e974f835f62fac19b1c812e55fa2d5aa053dcd4ed53f2d30536'
    'src/Naut.Showcase/public/third-party-assets/licenses/CC-BY-3.0.html' = '967a3670bcbfdc50614e71062b18effed899007a0e364541328ebdb863520132'
    'src/Naut.Showcase/public/third-party-assets/licenses/CC-BY-SA-2.0.html' = 'a90434ea9f88eb5c29eec63113a9902ac116be6f9c2061dc1fa66283610557b2'
}
$issues = [Collections.Generic.List[string]]::new()
$checkedFiles = 0

function Test-PrivateContact([string]$Text) {
    foreach ($match in [regex]::Matches($Text, $email)) {
        if ($match.Value -notmatch '@(?:(?:[\w-]+\.)*example\.(?:com|net|org)|(?:[\w-]+\.)+(?:invalid|test)|users\.noreply\.github\.com)$') { return $true }
    }
    return $Text -match $contact
}

foreach ($file in $files | Where-Object { $_ } | Sort-Object -Unique) {
    # Suppress the relative path as well when the filename contains a private value.
    if ($file -match $accountPath -or $file -match $personalFile -or $file -match $secret -or (Test-PrivateContact $file) -or
        ($file -match $managedFile -and $Matches.profile -notin $neutralProfiles)) {
        $id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($file))).Substring(0, 12)
        [void]$issues.Add("filename rejected (path SHA-256 prefix $id)")
        continue
    }
    # Only the reviewed, source-owned example may be committed as an NTPack.
    # Changed archive bytes require a new content/metadata review; all other packs remain forbidden.
    if ($file -ceq 'docs/examples/presentation-pack-contract4.ntpack') {
        $examplePath = Join-Path $root $file
        if (-not (Test-Path -LiteralPath $examplePath -PathType Leaf)) {
            [void]$issues.Add("$($file): reviewed example is missing")
        } elseif ((Get-FileHash -LiteralPath $examplePath -Algorithm SHA256).Hash -ine '4046b5bcd5b994892e961e8745030940cca7b28b247af5c4a5856467d5bf52bc') {
            [void]$issues.Add("$($file): reviewed example content changed; privacy review required")
        } else {
            $checkedFiles++
        }
        continue
    }
    if ($file -match '(?i)(?:^|/)\.env(?:$|\.(?!(?:example|sample|template)$)[^/]+$)|(?:^|/)(?:id_rsa|id_ed25519)(?:$|/)|\.(?:pem|pfx|p12|key|db|sqlite3?|db-wal|db-shm|sqlite3?-(?:wal|shm)|dmp|log|zip|7z|rar|ntpack)$') {
        [void]$issues.Add("$($file): private or generated file type")
        continue
    }
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $checkedFiles++
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 255 -and $bytes[1] -eq 254) {
        $content = [Text.Encoding]::Unicode.GetString($bytes)
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 254 -and $bytes[1] -eq 255) {
        $content = [Text.Encoding]::BigEndianUnicode.GetString($bytes)
    } elseif ($bytes -contains 0) {
        continue
    } else {
        $content = [Text.Encoding]::UTF8.GetString($bytes)
    }
    $reviewExampleNames = $file -match '\.(?:md|txt|sql)$|^(?:\.github/|scripts/verification/(?:fixtures|diagnostics)/)'
    if ($licensedContacts.ContainsKey($file)) {
        $normalized = [Text.Encoding]::UTF8.GetBytes($content.Replace("`r`n", "`n"))
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($normalized))
        $checkContacts = $hash -ne $licensedContacts[$file]
    } else {
        $checkContacts = $true
    }
    $reason = if ($content -match $secret) { 'credential pattern' }
        elseif ($content -match $accountPath) { 'local account path' }
        elseif ($checkContacts -and (Test-PrivateContact $content)) { 'email address or direct contact' }
        elseif ($reviewExampleNames -and $content -match $personalFile) { 'personal media filename pattern' }
    if ($reason) {
        [void]$issues.Add("$($file): $reason")
        continue
    }
    if ($reviewExampleNames) {
        foreach ($match in [regex]::Matches($content, $managedFile)) {
            if ($match.Groups['profile'].Value -notin $neutralProfiles) {
                [void]$issues.Add("$($file): use a neutral profile name in managed-media examples")
                break
            }
        }
    }
}

if ($issues.Count -gt 0) {
    throw ("Repository privacy verification failed. Matching values suppressed." + [Environment]::NewLine + ($issues -join [Environment]::NewLine))
}
Write-Output "REPOSITORY_PRIVACY=PASS; FILES=$checkedFiles"
