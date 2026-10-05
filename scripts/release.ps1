[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$Publish,
    [switch]$ArtifactMode,
    [string]$SourceHead,
    [string]$Repository
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$BuildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$ReleaseContractPath = Join-Path $RepositoryRoot 'release-contract.json'
$DistRoot = Join-Path $RepositoryRoot 'dist'
$BuildScript = Join-Path $PSScriptRoot 'build.ps1'

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Normalize-PemText {
    param([Parameter(Mandatory = $true)][string]$Value)
    return (($Value -replace [string][char]13, '').Trim())
}

function New-UpdatePublisherSignature {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$SignaturePath,
        [Parameter(Mandatory = $true)][string]$ContractPath,
        [Parameter(Mandatory = $true)][string]$PrivateKeyPem
    )

    if ([string]::IsNullOrWhiteSpace($PrivateKeyPem)) {
        throw 'NEUTERRADISE_UPDATE_SIGNING_KEY_PEM is required for a signed release.'
    }

    $contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
    $keyId = [string]$contract.updatePublisherSigningKeyId
    $matchingKeys = @($contract.updatePublisherKeys | Where-Object {
        [string]::Equals([string]$_.keyId, $keyId, [StringComparison]::Ordinal)
    })
    if ($matchingKeys.Count -ne 1) {
        throw "Release contract must resolve exactly one active signing key '$keyId'."
    }

    $key = $matchingKeys[0]
    if ([bool]$key.revoked) {
        throw "Release contract signing key '$keyId' is revoked."
    }
    if (-not [string]::Equals(
        [string]$key.algorithm,
        'ECDSA_P256_SHA256',
        [StringComparison]::Ordinal)) {
        throw "Release contract signing key '$keyId' has an unsupported algorithm."
    }

    $private = [Security.Cryptography.ECDsa]::Create()
    $public = [Security.Cryptography.ECDsa]::Create()
    try {
        $private.ImportFromPem($PrivateKeyPem)
        if ($private.KeySize -ne 256) {
            throw 'Update signing private key must be ECDSA P-256.'
        }

        $expectedPublicPem = Normalize-PemText -Value ([string]$key.publicKeyPem)
        $actualPublicPem = Normalize-PemText -Value $private.ExportSubjectPublicKeyInfoPem()
        if (-not [string]::Equals(
            $expectedPublicPem,
            $actualPublicPem,
            [StringComparison]::Ordinal)) {
            throw "Update signing private key does not match pinned publisher key '$keyId'."
        }

        $manifestBytes = [IO.File]::ReadAllBytes($ManifestPath)
        $manifestSha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($manifestBytes)
        ).ToLowerInvariant()
        $signature = $private.SignData(
            $manifestBytes,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation
        )

        $document = [ordered]@{
            schemaVersion = 1
            keyId = $keyId
            algorithm = 'ECDSA_P256_SHA256'
            signedPayloadSha256 = $manifestSha256
            signatureBase64 = [Convert]::ToBase64String($signature)
        }
        [IO.File]::WriteAllText(
            $SignaturePath,
            ($document | ConvertTo-Json -Depth 4),
            [Text.UTF8Encoding]::new($false)
        )

        $public.ImportFromPem([string]$key.publicKeyPem)
        if ($public.KeySize -ne 256 -or -not $public.VerifyData(
            $manifestBytes,
            $signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
            throw 'Generated update publisher signature failed immediate verification.'
        }
    }
    finally {
        $private.Dispose()
        $public.Dispose()
    }
}

function Resolve-RepositorySlug {
    param([string]$ExplicitRepository)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitRepository)) {
        if ($ExplicitRepository -notmatch '^[^/\s]+/[^/\s]+$') {
            throw "Repository must use owner/name form; received '$ExplicitRepository'."
        }
        return $ExplicitRepository
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
        if ($env:GITHUB_REPOSITORY -notmatch '^[^/\s]+/[^/\s]+$') {
            throw "GITHUB_REPOSITORY is not in owner/name form: '$($env:GITHUB_REPOSITORY)'."
        }
        return $env:GITHUB_REPOSITORY
    }

    $origin = @(& git remote get-url origin 2>$null)
    if ($LASTEXITCODE -eq 0 -and $origin.Count -gt 0) {
        $value = ([string]$origin[0]).Trim()
        if ($value -match '^(?:https://github\.com/|git@github\.com:)(?<slug>[^/\s]+/[^/\s]+?)(?:\.git)?$') {
            return [string]$Matches['slug']
        }
    }

    throw 'Could not resolve GitHub repository identity. Supply -Repository owner/name or configure GITHUB_REPOSITORY/origin.'
}

function Get-RemoteTagCommit {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Repo
    )

    $remote = "https://github.com/$Repo.git"
    $direct = @(git ls-remote --refs $remote "refs/tags/$Tag")
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect remote tag '$Tag'."
    }
    if ($direct.Count -eq 0) {
        return $null
    }

    $peeled = @(git ls-remote $remote "refs/tags/$Tag^{}")
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve remote tag '$Tag'."
    }

    $line = if ($peeled.Count -gt 0) { $peeled[0] } else { $direct[0] }
    return ($line -split '\s+')[0].Trim()
}

function Invoke-GhChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Push-Location $RepositoryRoot
try {
    foreach ($command in @('git', 'gh')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "$command is required to create or publish a GitHub Release."
        }
    }

    $Repository = Resolve-RepositorySlug -ExplicitRepository $Repository


    if ($ArtifactMode) {
        if (-not $NoBuild) {
            throw 'ArtifactMode requires -NoBuild because verified publication input must not be rebuilt.'
        }
        if ([string]::IsNullOrWhiteSpace($SourceHead) -or
            $SourceHead -notmatch '^[0-9a-fA-F]{40}$') {
            throw 'ArtifactMode requires an exact 40-character -SourceHead.'
        }
        $head = $SourceHead.ToLowerInvariant()
    }
    else {
        $dirty = @(git status --porcelain)
        if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) {
            throw 'GitHub Release creation requires a clean working tree.'
        }

        $head = (git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
            throw 'Could not resolve source HEAD.'
        }

        git fetch origin main --quiet
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not refresh origin/main before release creation.'
        }
        $originMain = (git rev-parse origin/main).Trim()
        if ($LASTEXITCODE -ne 0 -or $head -ne $originMain) {
            throw "Release source must exactly match current origin/main. HEAD=$head origin/main=$originMain"
        }
    }

    foreach ($authorityFile in @($BuildPropsPath, $ReleaseContractPath)) {
        if (-not (Test-Path -LiteralPath $authorityFile -PathType Leaf)) {
            throw "Release authority file is missing: $authorityFile"
        }
    }

    [xml]$buildProps = Get-Content -LiteralPath $BuildPropsPath -Raw
    $identity = $buildProps.Project.PropertyGroup |
        Where-Object { $_.ProductVersion -and $_.ProductRuntimeIdentifier } |
        Select-Object -First 1
    if ($null -eq $identity) {
        throw 'Directory.Build.props must define ProductVersion and ProductRuntimeIdentifier.'
    }

    $productVersion = [string]$identity.ProductVersion
    $runtimeIdentifier = [string]$identity.ProductRuntimeIdentifier
    $tag = "v$productVersion"
    $zipName = "naut-v$productVersion-$runtimeIdentifier.zip"

    if ($env:GITHUB_REF_TYPE -eq 'tag' -and
        -not [string]::Equals($env:GITHUB_REF_NAME, $tag, [StringComparison]::Ordinal)) {
        throw "Git tag '$($env:GITHUB_REF_NAME)' does not match product version tag '$tag'."
    }

    if (-not $NoBuild) {
        & $BuildScript
        if (-not $?) {
            throw 'Canonical build failed before release creation.'
        }
    }

    $zipPath = Join-Path $DistRoot $zipName
    $manifestPath = Join-Path $DistRoot 'update.json'
    $provenancePath = Join-Path $DistRoot 'build-provenance.json'
    $signaturePath = Join-Path $DistRoot 'update-signature.json'
    foreach ($required in @($zipPath, $manifestPath, $provenancePath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Verified release input is missing: $required"
        }
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.productId -ne 'neuterradise' -or
        $manifest.productVersion -ne $productVersion -or
        $manifest.runtimeIdentifier -ne $runtimeIdentifier) {
        throw 'update.json identity does not match Directory.Build.props.'
    }

    $zipInfo = Get-Item -LiteralPath $zipPath
    $zipHash = Get-Sha256Lower -Path $zipPath
    $manifestHash = Get-Sha256Lower -Path $manifestPath
    if ([long]$manifest.payloadByteLength -ne $zipInfo.Length -or
        -not [string]::Equals(
            [string]$manifest.payloadSha256,
            $zipHash,
            [StringComparison]::Ordinal)) {
        throw 'update.json does not match the final release ZIP.'
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if ($provenance.schemaVersion -ne 1 -or
        $provenance.sourceHead -ne $head -or
        $provenance.productVersion -ne $productVersion -or
        $provenance.runtimeIdentifier -ne $runtimeIdentifier -or
        $provenance.zipFileName -ne $zipName -or
        [long]$provenance.zipByteLength -ne $zipInfo.Length -or
        -not [string]::Equals(
            [string]$provenance.zipSha256,
            $zipHash,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$provenance.updateManifestSha256,
            $manifestHash,
            [StringComparison]::Ordinal)) {
        throw 'dist build provenance does not match the current source HEAD and release payload.'
    }

    $sourceCompanionProofs = @($provenance.sourceCompanions)
    $requiredSourceCompanionNames = @('ffmpeg-naut-corresponding-source.zip', 'opencv-ffmpeg-corresponding-source.zip')
    if ($sourceCompanionProofs.Count -ne $requiredSourceCompanionNames.Count) {
        throw 'dist build provenance must declare both required corresponding-source archives.'
    }
    $sourceCompanionPaths = @()
    foreach ($name in $requiredSourceCompanionNames) {
        $matches = @($sourceCompanionProofs | Where-Object { [string]$_.fileName -ceq $name })
        if ($matches.Count -ne 1) { throw "dist build provenance is missing corresponding source '$name'." }
        $path = Join-Path $DistRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Verified release input is missing: $path" }
        $info = Get-Item -LiteralPath $path
        $hash = Get-Sha256Lower -Path $path
        if ([long]$matches[0].byteLength -ne $info.Length -or [string]$matches[0].sha256 -cne $hash) {
            throw "Corresponding-source release asset differs from build provenance: $name"
        }
        $sourceCompanionPaths += $path
    }

    $signingArgs = @{
        ManifestPath = $manifestPath
        SignaturePath = $signaturePath
        ContractPath = $ReleaseContractPath
        PrivateKeyPem = $env:NEUTERRADISE_UPDATE_SIGNING_KEY_PEM
    }
    New-UpdatePublisherSignature @signingArgs

    gh auth status | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'GitHub CLI is not authenticated.'
    }

    $remoteTagCommit = Get-RemoteTagCommit -Tag $tag -Repo $Repository
    if ($null -ne $remoteTagCommit -and
        -not [string]::Equals(
            $remoteTagCommit,
            $head,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing remote tag '$tag' points to $remoteTagCommit, not current HEAD $head."
    }

    $releaseViewOutput = @(
        & gh release view $tag --repo $Repository --json isDraft,targetCommitish,tagName 2>&1
    )
    $releaseExists = $LASTEXITCODE -eq 0
    $releaseAssets = @($zipPath, $manifestPath, $signaturePath) + $sourceCompanionPaths

    if ($releaseExists) {
        $releaseInfo = ($releaseViewOutput -join [Environment]::NewLine) | ConvertFrom-Json
        if (-not $releaseInfo.isDraft) {
            throw "GitHub Release '$tag' is already published and is immutable through this script."
        }

        Invoke-GhChecked -Arguments @(
            'release', 'edit', $tag,
            '--repo', $Repository,
            '--target', $head,
            '--draft'
        )
        Invoke-GhChecked -Arguments (@(
            'release', 'upload', $tag,
            '--repo', $Repository,
            '--clobber'
        ) + $releaseAssets)
    }
    else {
        $releaseError = $releaseViewOutput -join [Environment]::NewLine
        if (-not [string]::IsNullOrWhiteSpace($releaseError) -and
            $releaseError -notmatch '(?i)(release not found|not found|HTTP 404)') {
            throw "Could not determine whether release '$tag' exists: $releaseError"
        }

        Invoke-GhChecked -Arguments (@(
            'release', 'create', $tag,
            '--repo', $Repository,
            '--target', $head,
            '--title', "naut $tag",
            '--generate-notes',
            '--draft'
        ) + $releaseAssets)
    }

    if ($Publish) {
        Invoke-GhChecked -Arguments @(
            'release', 'edit', $tag,
            '--repo', $Repository,
            '--draft=false'
        )

        $publishedTagCommit = Get-RemoteTagCommit -Tag $tag -Repo $Repository
        if ($null -eq $publishedTagCommit -or
            -not [string]::Equals(
                $publishedTagCommit,
                $head,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Published tag '$tag' does not resolve to current HEAD $head."
        }

        Write-Host 'STATUS=RELEASED'
    }
    else {
        Write-Host 'STATUS=DRAFT_READY'
    }

    Write-Host "TAG=$tag"
    Write-Host "SOURCE_HEAD=$head"
    Write-Host "ZIP=$zipPath"
    Write-Host "UPDATE_MANIFEST=$manifestPath"
    Write-Host "UPDATE_SIGNATURE=$signaturePath"
    Write-Host "BUILD_PROVENANCE=$provenancePath"
}
finally {
    Pop-Location
}
