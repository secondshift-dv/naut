[CmdletBinding()]
param(
    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$SolutionPath = Join-Path $RepositoryRoot 'NeuTerradise.sln'
$BuildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$ReleaseContractPath = Join-Path $RepositoryRoot 'release-contract.json'
$PackageScript = Join-Path $PSScriptRoot 'packaging/package-win-x64.ps1'
$DistRoot = Join-Path $RepositoryRoot 'dist'
$StageRoot = Join-Path ([IO.Path]::GetTempPath()) ('neuterradise-package-' + [Guid]::NewGuid().ToString('N'))
$OfflineSource = $null
$PreviousOfflineEnvironment = @{}

function Invoke-DotNetChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Get-RelativePathCompat {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $baseFull = [IO.Path]::GetFullPath($BasePath)
    if ($baseFull[$baseFull.Length - 1] -ne [IO.Path]::DirectorySeparatorChar) {
        $baseFull += [IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [Uri]$baseFull
    $pathUri = [Uri]([IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).
        Replace('/', [IO.Path]::DirectorySeparatorChar)
}

Push-Location $RepositoryRoot
try {
    if (-not (Test-Path -LiteralPath $SolutionPath -PathType Leaf)) { throw "Solution was not found: $SolutionPath" }
    if (-not (Test-Path -LiteralPath $PackageScript -PathType Leaf)) { throw "Packaging authority was not found: $PackageScript" }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is required to establish source identity.' }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'A local .NET 10 SDK is required. Automatic SDK installation is not part of the build script.' }

    $dirty = @(git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
    if ($dirty.Count -gt 0) { throw 'Working tree is not clean. Commit, stash, or revert local changes before producing canonical output.' }

    $head = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) { throw 'Could not resolve source HEAD.' }

    if ($Offline) {
        foreach ($name in @(
            'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE',
            'DOTNET_CLI_TELEMETRY_OPTOUT',
            'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
            'DOTNET_SDK_VULNERABILITY_CHECK_DISABLE',
            'NUGET_CERT_REVOCATION_MODE')) {
            $PreviousOfflineEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }

        $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $env:DOTNET_SDK_VULNERABILITY_CHECK_DISABLE = '1'
        $env:NUGET_CERT_REVOCATION_MODE = 'offline'
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or -not [string]::Equals($sdkVersion, '10.0.401', [StringComparison]::Ordinal)) {
        throw "Canonical build requires exact .NET SDK 10.0.401; resolved '$sdkVersion'."
    }

    [xml]$buildProps = Get-Content -LiteralPath $BuildPropsPath -Raw
    $identity = $buildProps.Project.PropertyGroup | Where-Object { $_.ProductVersion -and $_.ProductRuntimeIdentifier } | Select-Object -First 1
    if ($null -eq $identity) { throw 'Directory.Build.props must define ProductVersion and ProductRuntimeIdentifier.' }

    $productVersion = [string]$identity.ProductVersion
    $runtimeIdentifier = [string]$identity.ProductRuntimeIdentifier
    $zipName = "naut-v$productVersion-$runtimeIdentifier.zip"

    & (Join-Path $PSScriptRoot 'verification/verify-localization.ps1')
    & (Join-Path $PSScriptRoot 'verification/verify-theme-contract.ps1')

    if (-not (Test-Path -LiteralPath $ReleaseContractPath -PathType Leaf)) { throw 'release-contract.json is required.' }
    $releaseContract = Get-Content -LiteralPath $ReleaseContractPath -Raw | ConvertFrom-Json
    if ($releaseContract.schemaVersion -ne 1 -or
        $releaseContract.productId -ne 'neuterradise' -or
        $releaseContract.runtimeIdentifier -ne $runtimeIdentifier) {
        throw 'release-contract.json identity is invalid.'
    }

    Write-Host "SOURCE_HEAD=$head"
    Write-Host "PRODUCT_VERSION=$productVersion"
    Write-Host "RID=$runtimeIdentifier"
    Write-Host "NETWORK_MODE=$(if ($Offline) { 'OFFLINE' } else { 'CACHE_FIRST_MISSING_ONLY' })"

    $restoreArgs = @(
        'restore',
        $SolutionPath,
        '--runtime', $runtimeIdentifier,
        '--locked-mode',
        '-m:1',
        '-p:TreatWarningsAsErrors=true'
    )
    if ($Offline) {
        $OfflineSource = Join-Path ([IO.Path]::GetTempPath()) ('neuterradise-offline-nuget-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $OfflineSource -Force | Out-Null

        # A deliberately empty source prevents NuGet from contacting configured feeds. The global
        # packages folder remains available, so cached exact packages are reused and cache misses fail.
        $restoreArgs += @(
            '--source', $OfflineSource,
            '-p:NuGetAudit=false'
        )
    }
    Invoke-DotNetChecked -Arguments $restoreArgs

    & (Join-Path $PSScriptRoot 'verification/verify-updater.ps1') -Offline:$Offline
    & $PackageScript -RepositoryRoot $RepositoryRoot -OutputDirectory $StageRoot -Offline:$Offline
    if (-not $?) { throw 'Packaging authority failed.' }

    $verifiedBuild = Join-Path $StageRoot 'install-root'
    $verifiedZip = Join-Path $StageRoot $zipName
    $verifiedUpdateManifest = Join-Path $StageRoot 'update.json'
    $ffmpegLockPath = Join-Path $RepositoryRoot 'scripts/packaging/ffmpeg/ffmpeg-runtime.lock.json'
    $ffmpegLock = Get-Content -LiteralPath $ffmpegLockPath -Raw | ConvertFrom-Json
    $verifiedFfmpegSource = Join-Path $StageRoot ([string]$ffmpegLock.sourceCompanionName)
    $runtimeThirdPartyLockPath = Join-Path $RepositoryRoot 'scripts/packaging/runtime-third-party.lock.json'
    $runtimeThirdPartyLock = Get-Content -LiteralPath $runtimeThirdPartyLockPath -Raw | ConvertFrom-Json
    $verifiedOpenCvFfmpegSource = Join-Path $StageRoot ([string]$runtimeThirdPartyLock.opencvFfmpeg.sourceCompanionName)
    foreach ($required in @($verifiedBuild, $verifiedZip, $verifiedUpdateManifest, $verifiedFfmpegSource, $verifiedOpenCvFfmpegSource, (Join-Path $verifiedBuild 'release-manifest.json'))) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Verified build output is incomplete: $required" }
    }
    & (Join-Path $PSScriptRoot 'verification/verify-ffmpeg.ps1') -InstallRoot $verifiedBuild -SourceCompanionPath $verifiedFfmpegSource
    if (-not $?) { throw 'FFmpeg runtime/corresponding-source verification failed.' }
    & (Join-Path $PSScriptRoot 'verification/verify-runtime-third-party.ps1') -InstallRoot $verifiedBuild -OpenCvFfmpegSourceCompanionPath $verifiedOpenCvFfmpegSource
    if (-not $?) { throw 'Runtime third-party licensing verification failed.' }
    $verifiedRootFiles = @(Get-ChildItem -LiteralPath $verifiedBuild -File | Select-Object -ExpandProperty Name)
    $verifiedRootDirectories = @(Get-ChildItem -LiteralPath $verifiedBuild -Directory | Select-Object -ExpandProperty Name)
    if ($verifiedRootFiles.Count -ne 2 -or
        -not ($verifiedRootFiles -contains 'naut.exe') -or
        -not ($verifiedRootFiles -contains 'release-manifest.json') -or
        $verifiedRootDirectories.Count -ne 1 -or
        -not [string]::Equals($verifiedRootDirectories[0], 'runtime', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Canonical build root must contain only naut.exe, release-manifest.json, and runtime/.'
    }

    foreach ($relative in @($releaseContract.requiredMembers)) {
        $required = Join-Path $verifiedBuild ([string]$relative).Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Verified build output is missing canonical required member: $relative"
        }
    }
    foreach ($requiredFileName in @($releaseContract.requiredUniqueFileNames)) {
        $matches = @(Get-ChildItem -LiteralPath $verifiedBuild -File -Recurse -Filter ([string]$requiredFileName))
        if ($matches.Count -ne 1) {
            throw "Verified build output must contain exactly one required runtime member named '$requiredFileName'."
        }
    }

    if (Test-Path -LiteralPath $DistRoot) { Remove-Item -LiteralPath $DistRoot -Recurse -Force }
    $runtimeOutput = Join-Path $DistRoot 'naut'
    New-Item -ItemType Directory -Path $runtimeOutput -Force | Out-Null
    Copy-Item -Path (Join-Path $verifiedBuild '*') -Destination $runtimeOutput -Recurse -Force
    Copy-Item -LiteralPath $verifiedZip -Destination (Join-Path $DistRoot $zipName) -Force
    Copy-Item -LiteralPath $verifiedUpdateManifest -Destination (Join-Path $DistRoot 'update.json') -Force
    Copy-Item -LiteralPath $verifiedFfmpegSource -Destination (Join-Path $DistRoot ([IO.Path]::GetFileName($verifiedFfmpegSource))) -Force
    Copy-Item -LiteralPath $verifiedOpenCvFfmpegSource -Destination (Join-Path $DistRoot ([IO.Path]::GetFileName($verifiedOpenCvFfmpegSource))) -Force

    & (Join-Path $PSScriptRoot 'verification/verify-updater.ps1') -Offline:$Offline -PackageRoot $runtimeOutput
    $incrementalRoot = Join-Path $DistRoot 'update-files'
    New-Item -ItemType Directory -Path $incrementalRoot -Force | Out-Null
    $incrementalManifest = Get-Content -LiteralPath (Join-Path $DistRoot 'update.json') -Raw | ConvertFrom-Json
    foreach ($file in $incrementalManifest.files) {
        $source = Join-Path $runtimeOutput ([string]$file.relativePath)
        $target = Join-Path $incrementalRoot ("update-file-$($file.sha256).bin")
        if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $source -Destination $target }
        if ([long](Get-Item -LiteralPath $target).Length -ne [long]$file.byteLength -or
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$file.sha256) {
            throw 'Incremental build asset integrity differs from signed target membership.'
        }
    }
    Copy-Item -LiteralPath (Join-Path $runtimeOutput 'runtime/deployment/incremental-update.json') -Destination (Join-Path $DistRoot 'update-incremental.json')
    $distZip = Join-Path $DistRoot $zipName
    $distManifest = Join-Path $DistRoot 'update.json'
    $provenancePath = Join-Path $DistRoot 'build-provenance.json'
    $projectFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src') -File -Recurse -Filter '*.csproj' | ForEach-Object { $_.FullName })
    $lockFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'src') -File -Recurse -Filter 'packages.lock.json' | ForEach-Object { $_.FullName })
    if ($lockFiles.Count -ne $projectFiles.Count) {
        throw "Canonical dependency authority requires one packages.lock.json per project. Projects=$($projectFiles.Count), lockfiles=$($lockFiles.Count)."
    }

    $dependencyAuthorityFiles = @(
        (Join-Path $RepositoryRoot 'global.json'),
        (Join-Path $RepositoryRoot 'Directory.Build.props'),
        $ReleaseContractPath,
        $PackageScript,
        (Join-Path $RepositoryRoot 'scripts/packaging/ffmpeg-policy.ps1'),
        (Join-Path $RepositoryRoot 'scripts/packaging/ffmpeg/ffmpeg-runtime.lock.json'),
        (Join-Path $RepositoryRoot 'scripts/packaging/ffmpeg/build-ffmpeg.sh'),
        (Join-Path $RepositoryRoot 'scripts/packaging/ffmpeg/ffmpeg-build.py'),
        (Join-Path $RepositoryRoot 'scripts/packaging/ffmpeg/ffmpeg-build-inputs.json'),
        (Join-Path $RepositoryRoot 'scripts/packaging/runtime-third-party.lock.json'),
        (Join-Path $RepositoryRoot 'scripts/packaging/licenses/ICU-77.1-LICENSE.txt'),
        (Join-Path $RepositoryRoot 'scripts/packaging/licenses/OpenCV-FFmpeg-LGPL-2.1.txt'),
        (Join-Path $RepositoryRoot 'scripts/packaging/licenses/Uno-Fonts-Apache-2.0.txt'),
        (Join-Path $RepositoryRoot 'scripts/verification/verify-ffmpeg.ps1'),
        (Join-Path $RepositoryRoot 'scripts/verification/verify-runtime-third-party.ps1')
    ) + $projectFiles + $lockFiles
    $dependencyAuthorityLines = @($dependencyAuthorityFiles | Sort-Object | ForEach-Object {
        $relative = (Get-RelativePathCompat -BasePath $RepositoryRoot -Path $_).Replace('\', '/')
        $sha = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative=$sha"
    })
    $dependencyAuthorityBytes = [Text.Encoding]::UTF8.GetBytes(($dependencyAuthorityLines -join "`n"))
    $dependencyAuthoritySha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($dependencyAuthorityBytes)).ToLowerInvariant()

    $dependencyLockLines = @($lockFiles | Sort-Object | ForEach-Object {
        $relative = (Get-RelativePathCompat -BasePath $RepositoryRoot -Path $_).Replace('\', '/')
        $sha = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative=$sha"
    })
    $dependencyLockBytes = [Text.Encoding]::UTF8.GetBytes(($dependencyLockLines -join "`n"))
    $dependencyLockSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($dependencyLockBytes)).ToLowerInvariant()

    $provenance = [ordered]@{
        schemaVersion = 1
        sourceHead = $head
        dotnetSdkVersion = $sdkVersion
        dependencyDeclarationSha256 = $dependencyAuthoritySha256
        dependencyLockSha256 = $dependencyLockSha256
        productVersion = $productVersion
        runtimeIdentifier = $runtimeIdentifier
        zipFileName = $zipName
        zipByteLength = [long](Get-Item -LiteralPath $distZip).Length
        zipSha256 = (Get-FileHash -LiteralPath $distZip -Algorithm SHA256).Hash.ToLowerInvariant()
        updateManifestSha256 = (Get-FileHash -LiteralPath $distManifest -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceCompanions = @(
            $verifiedFfmpegSource,
            $verifiedOpenCvFfmpegSource
        ) | ForEach-Object {
            $name = [IO.Path]::GetFileName($_)
            $distPath = Join-Path $DistRoot $name
            [ordered]@{
                fileName = $name
                byteLength = [long](Get-Item -LiteralPath $distPath).Length
                sha256 = (Get-FileHash -LiteralPath $distPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
    $provenanceJson = $provenance | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
        $provenancePath,
        $provenanceJson + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    Write-Host 'STATUS=DONE'
    Write-Host "APP=$(Join-Path $runtimeOutput 'naut.exe')"
    Write-Host "ZIP=$(Join-Path $DistRoot $zipName)"
    Write-Host "UPDATE_MANIFEST=$(Join-Path $DistRoot 'update.json')"
    Write-Host "BUILD_PROVENANCE=$(Join-Path $DistRoot 'build-provenance.json')"
    Write-Host 'VAULT_TOUCHED=NO'
}
finally {
    Pop-Location
    foreach ($entry in $PreviousOfflineEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    if ($null -ne $OfflineSource -and (Test-Path -LiteralPath $OfflineSource)) { Remove-Item -LiteralPath $OfflineSource -Recurse -Force }
    if (Test-Path -LiteralPath $StageRoot) { Remove-Item -LiteralPath $StageRoot -Recurse -Force }
}
