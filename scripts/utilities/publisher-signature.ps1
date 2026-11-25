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
