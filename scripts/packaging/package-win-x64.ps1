[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = $(
        if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
            Join-Path ([IO.Path]::GetTempPath()) 'neuterradise-package'
        }
        else {
            Join-Path $env:RUNNER_TEMP 'neuterradise-package'
        }
    ),

    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

$ProductId = 'neuterradise'
$BuildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $BuildPropsPath)) { throw "Directory.Build.props is required for product release identity." }
$BuildProps = [xml](Get-Content -LiteralPath $BuildPropsPath -Raw)
$BuildPropertyGroup = $BuildProps.Project.PropertyGroup | Where-Object { $_.ProductVersion -and $_.ProductRuntimeIdentifier } | Select-Object -First 1
if ($null -eq $BuildPropertyGroup -or [string]::IsNullOrWhiteSpace($BuildPropertyGroup.ProductVersion) -or [string]::IsNullOrWhiteSpace($BuildPropertyGroup.ProductRuntimeIdentifier)) { throw "Directory.Build.props must define ProductVersion and ProductRuntimeIdentifier." }
$ProductVersion = [string]$BuildPropertyGroup.ProductVersion
$RuntimeIdentifier = [string]$BuildPropertyGroup.ProductRuntimeIdentifier
$ZipName = "naut-v$ProductVersion-$RuntimeIdentifier.zip"

$ReleaseContractSourcePath = Join-Path $RepositoryRoot 'release-contract.json'
if (-not (Test-Path -LiteralPath $ReleaseContractSourcePath -PathType Leaf)) { throw "release-contract.json is required." }
$ReleaseContract = Get-Content -LiteralPath $ReleaseContractSourcePath -Raw | ConvertFrom-Json
if ($ReleaseContract.schemaVersion -ne 1 -or
    $ReleaseContract.productId -ne $ProductId -or
    $ReleaseContract.runtimeIdentifier -ne $RuntimeIdentifier) {
    throw "release-contract.json identity is invalid."
}
$ModelsRelativeRoot = ([string]$ReleaseContract.modelsRelativeRoot).Replace('\', '/').Trim('/')
$RequiredReleaseMembers = @($ReleaseContract.requiredMembers | ForEach-Object { ([string]$_).Replace('\', '/') })
$RequiredUniqueFileNames = @($ReleaseContract.requiredUniqueFileNames | ForEach-Object { [string]$_ })
$FfmpegMirrorUrl = [string]$ReleaseContract.ffmpegMirrorUrl

$RuntimeDirectoryName = 'runtime'
$AppExe = 'naut.exe'
$WorkerExe = 'NeuTerradise.Profiling.Worker.exe'
$UpdaterExe = 'NeuTerradise.Updater.exe'
$LauncherRelativePath = $AppExe
$AppRelativePath = "$RuntimeDirectoryName/$AppExe"
$WorkerRelativePath = "$RuntimeDirectoryName/workers/$WorkerExe"
$UpdaterRelativePath = "$RuntimeDirectoryName/$UpdaterExe"

$OpenCvZooCommit = '47534e27c9851bb1128ccc0102f1145e27f23f98'
$YuNetFileName = 'face_detection_yunet_2023mar.onnx'
$YuNetSha256 = '8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4'
$YuNetUrl = "https://media.githubusercontent.com/media/opencv/opencv_zoo/$OpenCvZooCommit/models/face_detection_yunet/$YuNetFileName"
$YuNetLicenseUrl = "https://raw.githubusercontent.com/opencv/opencv_zoo/$OpenCvZooCommit/models/face_detection_yunet/LICENSE"

$SFaceFileName = 'face_recognition_sface_2021dec.onnx'
$SFaceSha256 = '0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79'
$SFaceUrl = "https://media.githubusercontent.com/media/opencv/opencv_zoo/$OpenCvZooCommit/models/face_recognition_sface/$SFaceFileName"
$SFaceLicenseUrl = "https://raw.githubusercontent.com/opencv/opencv_zoo/$OpenCvZooCommit/models/face_recognition_sface/LICENSE"

$FfmpegLockPath = Join-Path $RepositoryRoot 'scripts/packaging/ffmpeg/ffmpeg-runtime.lock.json'
if (-not (Test-Path -LiteralPath $FfmpegLockPath -PathType Leaf)) { throw 'FFmpeg runtime lock is required.' }
$FfmpegLock = Get-Content -LiteralPath $FfmpegLockPath -Raw | ConvertFrom-Json
if ($FfmpegLock.schemaVersion -ne 1 -or
    [string]$FfmpegLock.archiveSha256 -notmatch '^[0-9a-f]{64}$' -or
    [string]$FfmpegLock.sourceCompanionSha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'FFmpeg runtime lock identity is invalid.'
}
$FfmpegArchiveName = [string]$FfmpegLock.archiveName
$FfmpegArchiveSha256 = [string]$FfmpegLock.archiveSha256
$FfmpegArchiveUrl = [string]$FfmpegLock.archiveUrl
$FfmpegSourceCompanionName = [string]$FfmpegLock.sourceCompanionName
$FfmpegSourceCompanionSha256 = [string]$FfmpegLock.sourceCompanionSha256
$FfmpegSourceCompanionUrl = [string]$FfmpegLock.sourceCompanionUrl
if (-not [string]::Equals($FfmpegMirrorUrl, $FfmpegArchiveUrl, [StringComparison]::Ordinal)) {
    throw 'release-contract.json FFmpeg authority must resolve to the independently pinned Naut runtime asset.'
}
$FfmpegCommit = [string]$FfmpegLock.sourceCommit
$FfmpegLicenseUrl = "https://raw.githubusercontent.com/FFmpeg/FFmpeg/$FfmpegCommit/COPYING.LGPLv3"
$BtbnCommit = [string]$FfmpegLock.btbnCommit
$BtbnLicenseUrl = "https://raw.githubusercontent.com/BtbN/FFmpeg-Builds/$BtbnCommit/LICENSE"
. (Join-Path $RepositoryRoot 'scripts/packaging/ffmpeg-policy.ps1')

$RuntimeThirdPartyLockPath = Join-Path $RepositoryRoot 'scripts/packaging/runtime-third-party.lock.json'
if (-not (Test-Path -LiteralPath $RuntimeThirdPartyLockPath -PathType Leaf)) { throw 'Runtime third-party compliance lock is required.' }
$RuntimeThirdPartyLock = Get-Content -LiteralPath $RuntimeThirdPartyLockPath -Raw | ConvertFrom-Json
if ($RuntimeThirdPartyLock.schemaVersion -ne 1) { throw 'Runtime third-party compliance lock schema is invalid.' }
$RuntimeThirdPartyLicenseRoot = Join-Path $RepositoryRoot 'scripts/packaging/licenses'
$OpenCvFfmpegSourceCompanionName = [string]$RuntimeThirdPartyLock.opencvFfmpeg.sourceCompanionName
$OpenCvFfmpegSourceCompanionUrl = [string]$RuntimeThirdPartyLock.opencvFfmpeg.sourceCompanionUrl
$OpenCvFfmpegSourceCompanionSha256 = [string]$RuntimeThirdPartyLock.opencvFfmpeg.sourceCompanionSha256

$OpenCvSharpPackage = [string]$RuntimeThirdPartyLock.opencvFfmpeg.packageId
$OpenCvSharpVersion = [string]$RuntimeThirdPartyLock.opencvFfmpeg.packageVersion
$OpenCvSharpCommit = [string]$RuntimeThirdPartyLock.opencvFfmpeg.packageRepositoryCommit
$OpenCvSharpLicenseUrl = "https://raw.githubusercontent.com/shimat/opencvsharp/$OpenCvSharpCommit/LICENSE"

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actual = Get-Sha256Lower -Path $Path
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::Ordinal)) {
        throw "$Label SHA-256 mismatch. Expected $Expected but got $actual."
    }
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$AllowEqual
    )

    $candidateFull = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Candidate))
    $rootFull = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    if ([string]::Equals($candidateFull, $rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $AllowEqual.IsPresent
    }

    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-DisposableOutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $candidate = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $root = [IO.Path]::GetPathRoot($candidate)
    if ([string]::IsNullOrWhiteSpace($root) -or
        [string]::Equals($candidate, [IO.Path]::TrimEndingDirectorySeparator($root), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Packaging OutputDirectory cannot be a filesystem root.'
    }

    $repoPackagingRoot = Join-Path $RepoRoot 'dist\.packaging'
    $safe = Test-PathWithin -Candidate $candidate -Root $repoPackagingRoot -AllowEqual

    if (-not $safe -and -not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        $safe = Test-PathWithin -Candidate $candidate -Root $env:RUNNER_TEMP
    }

    if (-not $safe) {
        $systemTemp = [IO.Path]::GetTempPath()
        $safe = Test-PathWithin -Candidate $candidate -Root $systemTemp
    }

    if (-not $safe) {
        throw "Packaging OutputDirectory must be a dedicated disposable directory under RUNNER_TEMP, the system temp directory, or '$repoPackagingRoot'. Refusing recursive cleanup of '$candidate'."
    }
}

function Invoke-Download {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$OutFile
    )

    $parent = Split-Path -Parent $OutFile
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    # Persistent cache survives repeated local builds. GitHub Actions may override the root
    # through NEUTERRADISE_BUILD_CACHE so the exact same packaging code can use actions/cache.
    if (-not [string]::IsNullOrWhiteSpace($env:NEUTERRADISE_BUILD_CACHE)) {
        $cacheRoot = [IO.Path]::GetFullPath($env:NEUTERRADISE_BUILD_CACHE)
    }
    else {
        $localAppData = [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)

        if ([string]::IsNullOrWhiteSpace($localAppData)) {
            $localAppData = $env:USERPROFILE
        }

        $cacheRoot = Join-Path $localAppData 'naut\BuildCache\downloads'
    }

    New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null

    $cacheLabel = if ([string]::Equals($Uri, $YuNetUrl, [StringComparison]::Ordinal)) {
        'YuNet-model'
    }
    elseif ([string]::Equals($Uri, $SFaceUrl, [StringComparison]::Ordinal)) {
        'SFace-model'
    }
    elseif ([string]::Equals($Uri, $FfmpegArchiveUrl, [StringComparison]::Ordinal)) {
        'FFmpeg-archive'
    }
    elseif ([string]::Equals($Uri, $FfmpegSourceCompanionUrl, [StringComparison]::Ordinal)) {
        'FFmpeg-corresponding-source'
    }
    elseif ([string]::Equals($Uri, $OpenCvFfmpegSourceCompanionUrl, [StringComparison]::Ordinal)) {
        'OpenCV-FFmpeg-corresponding-source'
    }
    elseif ([string]::Equals($Uri, $YuNetLicenseUrl, [StringComparison]::Ordinal)) {
        'YuNet-license'
    }
    elseif ([string]::Equals($Uri, $SFaceLicenseUrl, [StringComparison]::Ordinal)) {
        'SFace-license'
    }
    elseif ([string]::Equals($Uri, $FfmpegLicenseUrl, [StringComparison]::Ordinal)) {
        'FFmpeg-license'
    }
    elseif ([string]::Equals($Uri, $BtbnLicenseUrl, [StringComparison]::Ordinal)) {
        'BtbN-license'
    }
    elseif ([string]::Equals($Uri, $OpenCvSharpLicenseUrl, [StringComparison]::Ordinal)) {
        'OpenCvSharp-license'
    }
    else {
        'external-file'
    }

    $expectedSha256 = if ([string]::Equals(
        $Uri,
        $YuNetUrl,
        [StringComparison]::Ordinal)) {

        $YuNetSha256
    }
    elseif ([string]::Equals(
        $Uri,
        $SFaceUrl,
        [StringComparison]::Ordinal)) {

        $SFaceSha256
    }
    elseif ([string]::Equals(
        $Uri,
        $FfmpegArchiveUrl,
        [StringComparison]::Ordinal)) {

        $FfmpegArchiveSha256
    }
    elseif ([string]::Equals(
        $Uri,
        $FfmpegSourceCompanionUrl,
        [StringComparison]::Ordinal)) {

        $FfmpegSourceCompanionSha256
    }
    elseif ([string]::Equals(
        $Uri,
        $OpenCvFfmpegSourceCompanionUrl,
        [StringComparison]::Ordinal)) {

        $OpenCvFfmpegSourceCompanionSha256
    }
    else {
        $null
    }

    $uriObject = [Uri]$Uri
    $leafName = [IO.Path]::GetFileName($uriObject.AbsolutePath)

    if ([string]::IsNullOrWhiteSpace($leafName)) {
        $leafName = 'download.bin'
    }

    $uriBytes = [Text.Encoding]::UTF8.GetBytes($Uri)
    $uriHashBytes = [Security.Cryptography.SHA256]::HashData($uriBytes)
    $uriHash = [Convert]::ToHexString($uriHashBytes).ToLowerInvariant()

    $cacheFile = Join-Path $cacheRoot "$uriHash-$leafName"
    $cacheValid = Test-Path -LiteralPath $cacheFile -PathType Leaf

    if ($cacheValid) {
        $cachedItem = Get-Item -LiteralPath $cacheFile

        if ($cachedItem.Length -le 0) {
            Write-Host "CACHE_INVALID=$cacheLabel"
            Remove-Item -LiteralPath $cacheFile -Force
            $cacheValid = $false
        }
    }

    if ($cacheValid -and -not [string]::IsNullOrWhiteSpace($expectedSha256)) {
        $cachedSha256 = Get-Sha256Lower -Path $cacheFile

        if (-not [string]::Equals(
            $cachedSha256,
            $expectedSha256,
            [StringComparison]::Ordinal)) {

            Write-Host "CACHE_INVALID=$cacheLabel"
            Remove-Item -LiteralPath $cacheFile -Force
            $cacheValid = $false
        }
    }

    if ($cacheValid) {
        Write-Host "CACHE_HIT=$cacheLabel"
        Copy-Item -LiteralPath $cacheFile -Destination $OutFile -Force
        return
    }

    Write-Host "CACHE_MISS=$cacheLabel"

    if ($Offline) {
        throw "Offline packaging is missing required cached artifact '$cacheLabel'. URI=$Uri"
    }

    $temporaryCacheFile =
        "$cacheFile.partial.$PID.$([Guid]::NewGuid().ToString('N'))"

    try {
        Invoke-WebRequest `
            -Uri $Uri `
            -OutFile $temporaryCacheFile `
            -MaximumRedirection 10 `
            -Headers @{ 'User-Agent' = "NeuTerradise-build/$ProductVersion" }

        if (-not (Test-Path -LiteralPath $temporaryCacheFile -PathType Leaf)) {
            throw "Download did not produce a file for $cacheLabel."
        }

        $downloadedItem = Get-Item -LiteralPath $temporaryCacheFile
        if ($downloadedItem.Length -le 0) {
            throw "Downloaded file is empty for $cacheLabel."
        }

        if (-not [string]::IsNullOrWhiteSpace($expectedSha256)) {
            Assert-Sha256 `
                -Path $temporaryCacheFile `
                -Expected $expectedSha256 `
                -Label $cacheLabel
        }

        Move-Item `
            -LiteralPath $temporaryCacheFile `
            -Destination $cacheFile `
            -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryCacheFile) {
            Remove-Item -LiteralPath $temporaryCacheFile -Force
        }
    }

    Copy-Item -LiteralPath $cacheFile -Destination $OutFile -Force
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code ${LASTEXITCODE}: dotnet $($Arguments -join ' ')"
    }
}

function Write-JsonUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 12
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Normalize-RelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return $Path.Replace('\', '/')
}

function Get-ReleaseRole {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ([string]::Equals($RelativePath, $LauncherRelativePath, [StringComparison]::OrdinalIgnoreCase)) { return 'launcher' }
    if ([string]::Equals($RelativePath, $AppRelativePath, [StringComparison]::OrdinalIgnoreCase)) { return 'app' }
    if ([string]::Equals($RelativePath, $WorkerRelativePath, [StringComparison]::OrdinalIgnoreCase)) { return 'profiling-worker' }
    if ([string]::Equals($RelativePath, $UpdaterRelativePath, [StringComparison]::OrdinalIgnoreCase)) { return 'updater' }
    if ([string]::Equals($RelativePath, "$RuntimeDirectoryName/deployment/artifacts.json", [StringComparison]::OrdinalIgnoreCase)) { return 'deployment-manifest' }
    if ($RelativePath.StartsWith("$RuntimeDirectoryName/tools/", [StringComparison]::OrdinalIgnoreCase)) { return 'tool' }
    if ($RelativePath.StartsWith("$RuntimeDirectoryName/workers/models/", [StringComparison]::OrdinalIgnoreCase)) { return 'model' }
    if ($RelativePath.StartsWith("$RuntimeDirectoryName/workers/", [StringComparison]::OrdinalIgnoreCase)) { return 'profiling-runtime' }
    if ($RelativePath.StartsWith("$RuntimeDirectoryName/LICENSES/", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($RelativePath, "$RuntimeDirectoryName/THIRD-PARTY-NOTICES.txt", [StringComparison]::OrdinalIgnoreCase)) { return 'notice' }
    return 'runtime'
}

function Assert-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Root $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required deployed file is missing: $RelativePath"
    }
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Assert-DisposableOutputDirectory -Path $OutputDirectory -RepoRoot $RepositoryRoot
$PublishRoot = Join-Path $OutputDirectory 'publish'
$InstallRoot = Join-Path $OutputDirectory 'install-root'
$RuntimeInstallRoot = Join-Path $InstallRoot $RuntimeDirectoryName
$DownloadRoot = Join-Path $OutputDirectory 'downloads'
$ExtractRoot = Join-Path $OutputDirectory 'extracted'
$VerificationRoot = Join-Path $OutputDirectory 'verification'
$ZipPath = Join-Path $OutputDirectory $ZipName

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishRoot, $InstallRoot, $RuntimeInstallRoot, $DownloadRoot, $ExtractRoot -Force | Out-Null

Push-Location $RepositoryRoot
try {
    $commonPublish = @(
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '--disable-build-servers',
        '-m:1',
        '-warnaserror',
        '-p:BuildInParallel=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:PublishTrimmed=false',
        '-p:DisableUnoResizetizer=true',
        '-p:TargetsTriggeredByCompilation='
    )

    $launcherOutput = Join-Path $PublishRoot 'launcher'
    New-Item -ItemType Directory -Path $launcherOutput -Force | Out-Null
    $launcherArgs = @(
        'publish',
        'src/Neuterradise.Launcher/Neuterradise.Launcher.csproj'
    ) + $commonPublish + @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '--output', $launcherOutput
    )
    Invoke-DotNet -Arguments $launcherArgs
    $publishedLauncher = Join-Path $launcherOutput $AppExe
    if (-not (Test-Path -LiteralPath $publishedLauncher -PathType Leaf)) {
        throw "Single-file launcher publish did not produce $AppExe."
    }
    Copy-Item -LiteralPath $publishedLauncher -Destination (Join-Path $InstallRoot $AppExe) -Force

    $appOutput = Join-Path $PublishRoot 'app'
    New-Item -ItemType Directory -Path $appOutput -Force | Out-Null
    # The product UI is Uno Platform (Skia Desktop, Win32 host): publish the net10.0-desktop head as a
    # plain self-contained folder. Never MSIX; the updater replaces these files and never owns the Vault.
    $appArgs = @(
        'publish',
        'src/Neuterradise.App/Neuterradise.App.csproj',
        '--framework', 'net10.0-desktop'
    ) + $commonPublish + @(
        '-p:PublishSingleFile=false',
        '-p:WindowsPackageType=None',
        '--output', $appOutput
    )
    Invoke-DotNet -Arguments $appArgs
    Copy-Item -Path (Join-Path $appOutput '*') -Destination $RuntimeInstallRoot -Recurse -Force
    Move-Item -LiteralPath (Join-Path $RuntimeInstallRoot 'NeuTerradise.exe') -Destination (Join-Path $RuntimeInstallRoot $AppExe)

    foreach ($forbidden in @(
        '*.msix',
        '*.appx',
        '*.msixbundle',
        'Uno.WinUI.Runtime.Skia.Wpf*',
        'PresentationFramework.dll',
        'PresentationCore.dll',
        'System.Xaml.dll',
        'System.Printing.dll',
        'ReachFramework.dll',
        'Uno.UI.HotDesign*',
        'Uno.UI.RemoteControl*',
        'Uno.UI.App.Mcp*',
        'Uno.AI.XamlGeneration*')) {
        $hits = @(Get-ChildItem -LiteralPath $RuntimeInstallRoot -File -Recurse -Filter $forbidden)
        if ($hits.Count -gt 0) {
            throw "Uno desktop publish must not contain $forbidden (found $($hits[0].FullName))."
        }
    }

    $xamlGenerationAssets = Join-Path $RuntimeInstallRoot 'uno.ai.xamlgeneration.assets'
    if (Test-Path -LiteralPath $xamlGenerationAssets) {
        throw "Uno desktop publish must not contain design-time XAML generation assets: $xamlGenerationAssets"
    }

    foreach ($manifest in @(Get-ChildItem -LiteralPath $RuntimeInstallRoot -File -Recurse |
        Where-Object { $_.Name.EndsWith('.deps.json', [StringComparison]::OrdinalIgnoreCase) -or
                       $_.Name.EndsWith('.runtimeconfig.json', [StringComparison]::OrdinalIgnoreCase) })) {
        $forbiddenReference = Select-String -LiteralPath $manifest.FullName -SimpleMatch -Quiet -Pattern @(
            'Uno.WinUI.Runtime.Skia.Wpf',
            'PresentationFramework',
            'PresentationCore',
            'Uno.UI.HotDesign',
            'Uno.UI.RemoteControl',
            'Uno.UI.App.Mcp',
            'Uno.AI.XamlGeneration')
        if ($forbiddenReference) {
            throw "Uno desktop dependency manifest contains a forbidden host or development-tool reference: $($manifest.FullName)."
        }
    }

    $workerOutput = Join-Path $PublishRoot 'worker'
    $workerInstallRoot = Join-Path $RuntimeInstallRoot 'workers'
    New-Item -ItemType Directory -Path $workerOutput, $workerInstallRoot -Force | Out-Null
    $workerArgs = @(
        'publish',
        'src/Neuterradise.Profiling.Worker/Neuterradise.Profiling.Worker.csproj'
    ) + $commonPublish + @(
        '-p:PublishSingleFile=false',
        '--output', $workerOutput
    )
    Invoke-DotNet -Arguments $workerArgs
    Copy-Item -Path (Join-Path $workerOutput '*') -Destination $workerInstallRoot -Recurse -Force

    $updaterOutput = Join-Path $PublishRoot 'updater'
    New-Item -ItemType Directory -Path $updaterOutput -Force | Out-Null
    $updaterArgs = @(
        'publish',
        'src/Neuterradise.Updater/Neuterradise.Updater.csproj'
    ) + $commonPublish + @(
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '--output', $updaterOutput
    )
    Invoke-DotNet -Arguments $updaterArgs

    $publishedUpdater = Join-Path $updaterOutput $UpdaterExe
    if (-not (Test-Path -LiteralPath $publishedUpdater -PathType Leaf)) {
        throw "Single-file updater publish did not produce $UpdaterExe."
    }
    Copy-Item -LiteralPath $publishedUpdater -Destination (Join-Path $RuntimeInstallRoot $UpdaterExe) -Force
}
finally {
    Pop-Location
}

# Symbol files from framework/NuGet packages are development artifacts, not runtime payload.
Get-ChildItem -LiteralPath $InstallRoot -File -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue |
    Remove-Item -Force

$WorkerInstallRoot = Join-Path $RuntimeInstallRoot 'workers'
$YuNetRelativePath = "$ModelsRelativeRoot/yunet/$YuNetFileName"
$SFaceRelativePath = "$ModelsRelativeRoot/sface/$SFaceFileName"
$YuNetPath = Join-Path $RuntimeInstallRoot $YuNetRelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
$SFacePath = Join-Path $RuntimeInstallRoot $SFaceRelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)

Invoke-Download -Uri $YuNetUrl -OutFile $YuNetPath
Assert-Sha256 -Path $YuNetPath -Expected $YuNetSha256 -Label 'YuNet model'
Invoke-Download -Uri $SFaceUrl -OutFile $SFacePath
Assert-Sha256 -Path $SFacePath -Expected $SFaceSha256 -Label 'SFace model'

$LicensesRoot = Join-Path $RuntimeInstallRoot 'LICENSES'
New-Item -ItemType Directory -Path $LicensesRoot -Force | Out-Null
Invoke-Download -Uri $YuNetLicenseUrl -OutFile (Join-Path $LicensesRoot 'YuNet-MIT.txt')
Invoke-Download -Uri $SFaceLicenseUrl -OutFile (Join-Path $LicensesRoot 'SFace-Apache-2.0.txt')
foreach ($notice in @(
    @{ File = [string]$RuntimeThirdPartyLock.opencvFfmpeg.licenseFile; Hash = [string]$RuntimeThirdPartyLock.opencvFfmpeg.licenseSha256; Label = 'OpenCV FFmpeg license' },
    @{ File = [string]$RuntimeThirdPartyLock.icu.licenseFile; Hash = [string]$RuntimeThirdPartyLock.icu.licenseSha256; Label = 'ICU license' },
    @{ File = [string]$RuntimeThirdPartyLock.unoFonts.licenseFile; Hash = [string]$RuntimeThirdPartyLock.unoFonts.licenseSha256; Label = 'Uno font license' }
)) {
    $sourceNotice = Join-Path $RuntimeThirdPartyLicenseRoot $notice.File
    Assert-Sha256 -Path $sourceNotice -Expected $notice.Hash -Label "$($notice.Label) authority"
    Copy-Item -LiteralPath $sourceNotice -Destination (Join-Path $LicensesRoot $notice.File) -Force
}
$OpenCvFfmpegSourceCompanionPath = Join-Path $DownloadRoot $OpenCvFfmpegSourceCompanionName
Invoke-Download -Uri $OpenCvFfmpegSourceCompanionUrl -OutFile $OpenCvFfmpegSourceCompanionPath
Assert-Sha256 -Path $OpenCvFfmpegSourceCompanionPath -Expected $OpenCvFfmpegSourceCompanionSha256 -Label 'OpenCV FFmpeg corresponding-source archive'
Copy-Item -LiteralPath $OpenCvFfmpegSourceCompanionPath -Destination (Join-Path $OutputDirectory $OpenCvFfmpegSourceCompanionName) -Force
$FfmpegArchivePath = Join-Path $DownloadRoot $FfmpegArchiveName
Invoke-Download -Uri $FfmpegArchiveUrl -OutFile $FfmpegArchivePath
Assert-Sha256 -Path $FfmpegArchivePath -Expected $FfmpegArchiveSha256 -Label 'Naut FFmpeg runtime archive'
$FfmpegSourceCompanionPath = Join-Path $DownloadRoot $FfmpegSourceCompanionName
Invoke-Download -Uri $FfmpegSourceCompanionUrl -OutFile $FfmpegSourceCompanionPath
Assert-Sha256 -Path $FfmpegSourceCompanionPath -Expected $FfmpegSourceCompanionSha256 -Label 'FFmpeg corresponding-source archive'
Copy-Item -LiteralPath $FfmpegArchivePath -Destination (Join-Path $OutputDirectory $FfmpegArchiveName) -Force
Copy-Item -LiteralPath $FfmpegSourceCompanionPath -Destination (Join-Path $OutputDirectory $FfmpegSourceCompanionName) -Force
Write-Host 'FFMPEG_SOURCE=PINNED_NAUT_REPRODUCIBLE_RUNTIME'

$FfmpegExtractRoot = Join-Path $ExtractRoot 'ffmpeg'
Expand-Archive -LiteralPath $FfmpegArchivePath -DestinationPath $FfmpegExtractRoot -Force
$FfmpegCandidates = @(Get-ChildItem -LiteralPath $FfmpegExtractRoot -File -Recurse -Filter 'ffmpeg.exe')
$FfprobeCandidates = @(Get-ChildItem -LiteralPath $FfmpegExtractRoot -File -Recurse -Filter 'ffprobe.exe')
if ($FfmpegCandidates.Count -ne 1 -or $FfprobeCandidates.Count -ne 1) {
    throw "Expected exactly one ffmpeg.exe and one ffprobe.exe in pinned archive; got $($FfmpegCandidates.Count) and $($FfprobeCandidates.Count)."
}
$FfmpegCandidateLicenses = Join-Path $FfmpegExtractRoot 'LICENSES'
if (-not (Test-Path -LiteralPath $FfmpegCandidateLicenses -PathType Container)) { throw 'Pinned FFmpeg license inventory is missing.' }
Copy-Item -Path (Join-Path $FfmpegCandidateLicenses '*') -Destination $LicensesRoot -Force
Copy-Item -LiteralPath (Join-Path $FfmpegExtractRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $LicensesRoot 'FFmpeg-THIRD-PARTY-NOTICES.txt') -Force
Copy-Item -LiteralPath (Join-Path $FfmpegExtractRoot 'LICENSE.txt') -Destination (Join-Path $LicensesRoot 'FFmpeg-LICENSE.txt') -Force

$ToolsRoot = Join-Path $RuntimeInstallRoot 'tools'
New-Item -ItemType Directory -Path $ToolsRoot -Force | Out-Null
$FfmpegPath = Join-Path $ToolsRoot 'ffmpeg.exe'
$FfprobePath = Join-Path $ToolsRoot 'ffprobe.exe'
Copy-Item -LiteralPath $FfmpegCandidates[0].FullName -Destination $FfmpegPath -Force
Copy-Item -LiteralPath $FfprobeCandidates[0].FullName -Destination $FfprobePath -Force
$DeploymentDirectory = Join-Path $RuntimeInstallRoot 'deployment'
New-Item -ItemType Directory -Path $DeploymentDirectory -Force | Out-Null
$FfmpegProvenancePath = Join-Path $DeploymentDirectory 'ffmpeg-provenance.json'
Copy-Item -LiteralPath (Join-Path $FfmpegExtractRoot 'provenance.json') -Destination $FfmpegProvenancePath -Force
$FfmpegProof = Assert-FfmpegPolicy -FfmpegPath $FfmpegPath -FfprobePath $FfprobePath -ProvenancePath $FfmpegProvenancePath -LicensePath (Join-Path $LicensesRoot 'FFmpeg-LGPL-3.0.txt') -ArchiveSha256 $FfmpegArchiveSha256 -LockPath $FfmpegLockPath

$ffmpegVersionLine = (& $FfmpegPath -hide_banner -version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or $ffmpegVersionLine -notmatch '330caae0c1') {
    throw "Pinned ffmpeg executable did not report expected source revision 330caae0c1. Reported: $ffmpegVersionLine"
}
$ffprobeVersionLine = (& $FfprobePath -hide_banner -version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or $ffprobeVersionLine -notmatch '330caae0c1') {
    throw "Pinned ffprobe executable did not report expected source revision 330caae0c1. Reported: $ffprobeVersionLine"
}

# Hover preparation requires libopenh264. The reviewed Naut LGPL runtime excludes GPL/nonfree
# features, so package verification proves the encoder contract instead of merely proving launch.
$ffmpegEncoders = (& $FfmpegPath -hide_banner -encoders 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $ffmpegEncoders -notmatch '(?m)^\s*V[^\r\n]*\blibopenh264\b') {
    throw 'Pinned ffmpeg executable does not provide the required libopenh264 video-preview encoder.'
}

$FfmpegSourceEntry = Normalize-RelativePath -Path ((Get-RelativePathCompat -BasePath $FfmpegExtractRoot -Path $FfmpegCandidates[0].FullName))
$FfprobeSourceEntry = Normalize-RelativePath -Path ((Get-RelativePathCompat -BasePath $FfmpegExtractRoot -Path $FfprobeCandidates[0].FullName))

$NuGetPackagesRoot = if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    [IO.Path]::GetFullPath($env:NUGET_PACKAGES)
}
else {
    Join-Path $env:USERPROFILE '.nuget\packages'
}
$OpenCvPackageRoot = Join-Path $NuGetPackagesRoot "$($OpenCvSharpPackage.ToLowerInvariant())/$OpenCvSharpVersion"
if (-not (Test-Path -LiteralPath $OpenCvPackageRoot -PathType Container)) {
    throw "Restored NuGet package was not found: $OpenCvPackageRoot"
}
$OpenCvPackageNatives = @(Get-ChildItem -LiteralPath $OpenCvPackageRoot -File -Recurse -Filter 'OpenCvSharpExtern.dll')
if ($OpenCvPackageNatives.Count -ne 1) {
    throw "Expected exactly one OpenCvSharpExtern.dll in $OpenCvSharpPackage $OpenCvSharpVersion; got $($OpenCvPackageNatives.Count)."
}
$PublishedOpenCvNatives = @(Get-ChildItem -LiteralPath $WorkerInstallRoot -File -Recurse -Filter 'OpenCvSharpExtern.dll')
if ($PublishedOpenCvNatives.Count -ne 1) {
    throw "Expected exactly one deployed OpenCvSharpExtern.dll under workers/; got $($PublishedOpenCvNatives.Count)."
}
$OpenCvPackageNativeSha = Get-Sha256Lower -Path $OpenCvPackageNatives[0].FullName
$OpenCvPublishedNativeSha = Get-Sha256Lower -Path $PublishedOpenCvNatives[0].FullName
if (-not [string]::Equals($OpenCvPackageNativeSha, $OpenCvPublishedNativeSha, [StringComparison]::Ordinal)) {
    throw 'Published OpenCvSharpExtern.dll does not match the restored pinned NuGet package byte-for-byte.'
}
$OpenCvSourceEntry = Normalize-RelativePath -Path ((Get-RelativePathCompat -BasePath $OpenCvPackageRoot -Path $OpenCvPackageNatives[0].FullName))
$OpenCvDeployedRelativePath = Normalize-RelativePath -Path ((Get-RelativePathCompat -BasePath $RuntimeInstallRoot -Path $PublishedOpenCvNatives[0].FullName))

$OpenCvFfmpegName = [string]$RuntimeThirdPartyLock.opencvFfmpeg.deployedName
$OpenCvFfmpegPackageNatives = @(Get-ChildItem -LiteralPath $OpenCvPackageRoot -File -Recurse -Filter $OpenCvFfmpegName)
$PublishedOpenCvFfmpegNatives = @(Get-ChildItem -LiteralPath $WorkerInstallRoot -File -Recurse -Filter $OpenCvFfmpegName)
if ($OpenCvFfmpegPackageNatives.Count -ne 1 -or $PublishedOpenCvFfmpegNatives.Count -ne 1) {
    throw "Expected exactly one $OpenCvFfmpegName in the package and deployed worker runtime."
}
$OpenCvFfmpegExpectedSha = [string]$RuntimeThirdPartyLock.opencvFfmpeg.sha256
Assert-Sha256 -Path $OpenCvFfmpegPackageNatives[0].FullName -Expected $OpenCvFfmpegExpectedSha -Label 'OpenCV FFmpeg package binary'
Assert-Sha256 -Path $PublishedOpenCvFfmpegNatives[0].FullName -Expected $OpenCvFfmpegExpectedSha -Label 'OpenCV FFmpeg deployed binary'
$OpenCvFfmpegSourceEntry = Normalize-RelativePath -Path ((Get-RelativePathCompat -BasePath $OpenCvPackageRoot -Path $OpenCvFfmpegPackageNatives[0].FullName))
$OpenCvFfmpegDeployedRelativePath = Normalize-RelativePath -Path ((Get-RelativePathCompat -BasePath $RuntimeInstallRoot -Path $PublishedOpenCvFfmpegNatives[0].FullName))

foreach ($component in @(
    @{ Name='SkiaSharp'; Id=[string]$RuntimeThirdPartyLock.skiaSharp.packageId; Version=[string]$RuntimeThirdPartyLock.skiaSharp.packageVersion; LicenseSource='LICENSE.txt'; NoticesSource='THIRD-PARTY-NOTICES.txt'; LicenseHash=[string]$RuntimeThirdPartyLock.skiaSharp.licenseSha256; NoticesHash=[string]$RuntimeThirdPartyLock.skiaSharp.noticesSha256 },
    @{ Name='HarfBuzzSharp'; Id=[string]$RuntimeThirdPartyLock.harfBuzzSharp.packageId; Version=[string]$RuntimeThirdPartyLock.harfBuzzSharp.packageVersion; LicenseSource='LICENSE.txt'; NoticesSource='THIRD-PARTY-NOTICES.txt'; LicenseHash=[string]$RuntimeThirdPartyLock.harfBuzzSharp.licenseSha256; NoticesHash=[string]$RuntimeThirdPartyLock.harfBuzzSharp.noticesSha256 },
    @{ Name='DotNet-Runtime'; Id=[string]$RuntimeThirdPartyLock.dotnetRuntime.packageId; Version=[string]$RuntimeThirdPartyLock.dotnetRuntime.packageVersion; LicenseSource='LICENSE.TXT'; NoticesSource='THIRD-PARTY-NOTICES.TXT'; LicenseHash=[string]$RuntimeThirdPartyLock.dotnetRuntime.licenseSha256; NoticesHash=[string]$RuntimeThirdPartyLock.dotnetRuntime.noticesSha256 }
)) {
    $packageRoot = Join-Path $NuGetPackagesRoot "$($component.Id.ToLowerInvariant())/$($component.Version)"
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { throw "Restored compliance package is missing: $($component.Id) $($component.Version)" }
    $licenseSource = Join-Path $packageRoot $component.LicenseSource
    $noticesSource = Join-Path $packageRoot $component.NoticesSource
    Assert-Sha256 -Path $licenseSource -Expected $component.LicenseHash -Label "$($component.Name) license"
    Assert-Sha256 -Path $noticesSource -Expected $component.NoticesHash -Label "$($component.Name) notices"
    Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $LicensesRoot "$($component.Name)-LICENSE.txt") -Force
    Copy-Item -LiteralPath $noticesSource -Destination (Join-Path $LicensesRoot "$($component.Name)-THIRD-PARTY-NOTICES.txt") -Force
}

$ThirdPartyNoticePath = Join-Path $RuntimeInstallRoot 'THIRD-PARTY-NOTICES.txt'
$notice = @"
naut v$ProductVersion - Third-Party Runtime Notices

This runtime package contains third-party runtime artifacts pinned by exact source/version and verified before packaging.

1. YuNet face detector model
   Source: OpenCV Zoo commit $OpenCvZooCommit
   Source file: models/face_detection_yunet/$YuNetFileName
   Deployed file: $YuNetRelativePath
   SHA-256: $YuNetSha256
   License: MIT
   License copy: LICENSES/YuNet-MIT.txt

2. SFace face recognition model
   Source: OpenCV Zoo commit $OpenCvZooCommit
   Source file: models/face_recognition_sface/$SFaceFileName
   Deployed file: $SFaceRelativePath
   SHA-256: $SFaceSha256
   License: Apache-2.0
   License copy: LICENSES/SFace-Apache-2.0.txt

3. FFmpeg and ffprobe
   Reproducible Naut runtime archive: $FfmpegArchiveName
   Runtime archive SHA-256: $FfmpegArchiveSha256
   Corresponding source: $FfmpegSourceCompanionName
   Corresponding-source SHA-256: $FfmpegSourceCompanionSha256
   FFmpeg source revision: $FfmpegCommit
   BtbN recipe reference revision: $BtbnCommit
   Naut recipe authority revision: $($FfmpegLock.recipeCommit)
   Build configuration is pinned in scripts/packaging/ffmpeg/ffmpeg-runtime.lock.json
   License: $($FfmpegLock.license)
   License copy: LICENSES/FFmpeg-LGPL-3.0.txt
   Component/build notices: LICENSES/FFmpeg-THIRD-PARTY-NOTICES.txt
   $($FfmpegProof.inputs.openH264PatentStatement)

4. OpenCvSharp native runtime
   NuGet package: $OpenCvSharpPackage $OpenCvSharpVersion
   Package repository commit: $OpenCvSharpCommit
   Deployed file: $OpenCvDeployedRelativePath
   License: Apache-2.0
   License copy: LICENSES/OpenCvSharp-Apache-2.0.txt

5. OpenCV videoio FFmpeg plugin
   Deployed file: $OpenCvFfmpegDeployedRelativePath
   SHA-256: $OpenCvFfmpegExpectedSha
   Upstream binary commit: $($RuntimeThirdPartyLock.opencvFfmpeg.opencvThirdPartyCommit)
   License: $($RuntimeThirdPartyLock.opencvFfmpeg.license)
   License copy: LICENSES/$($RuntimeThirdPartyLock.opencvFfmpeg.licenseFile)
   Corresponding source: $OpenCvFfmpegSourceCompanionName
   Corresponding-source SHA-256: $OpenCvFfmpegSourceCompanionSha256

6. ICU native runtime
   NuGet package: $($RuntimeThirdPartyLock.icu.packageId) $($RuntimeThirdPartyLock.icu.packageVersion)
   Source release: $($RuntimeThirdPartyLock.icu.sourceRelease)
   License: Unicode License V3
   License copy: LICENSES/$($RuntimeThirdPartyLock.icu.licenseFile)

7. SkiaSharp, HarfBuzzSharp, and self-contained .NET runtime
   Exact package versions and binary/license hashes are pinned by scripts/packaging/runtime-third-party.lock.json.
   License/notices copies: LICENSES/SkiaSharp-*, LICENSES/HarfBuzzSharp-*, LICENSES/DotNet-Runtime-*.

8. Fonts
   Built-in presentation fonts retain file-level provenance and OFL texts under Assets/Presentation/BuiltIn/Fonts/.
   Uno OpenSans and Fluent font assets are verified byte-for-byte against their locked NuGet packages.
   Uno package license copy: LICENSES/$($RuntimeThirdPartyLock.unoFonts.licenseFile)

The deployment/artifacts.json manifest remains the operational authority for model/tool/native capabilities explicitly resolved by Naut. The runtime-third-party lock and packaged license inventory are the compliance authority for the remaining bundled runtime libraries. A missing file or SHA-256 mismatch fails the canonical build.
"@
[IO.File]::WriteAllText($ThirdPartyNoticePath, $notice.TrimStart() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

$DeploymentDirectory = Join-Path $RuntimeInstallRoot 'deployment'
New-Item -ItemType Directory -Path $DeploymentDirectory -Force | Out-Null
$ReleaseContractInstallPath = Join-Path $DeploymentDirectory 'release-contract.json'
Copy-Item -LiteralPath $ReleaseContractSourcePath -Destination $ReleaseContractInstallPath -Force
$DeploymentManifestPath = Join-Path $DeploymentDirectory 'artifacts.json'

$deploymentManifest = [ordered]@{
    schemaVersion = 2
    policy = 'InstallRoot-only pinned runtime artifacts; SHA-256 verification is mandatory and mismatches fail closed. The profiling worker is isolated under workers/ and resolves its models from workers/models. No PATH, source-tree, or network fallback is permitted at runtime.'
    noticesRelativePath = 'THIRD-PARTY-NOTICES.txt'
    sources = @(
        [ordered]@{
            sourceId = 'opencv-zoo-yunet-2023mar'
            kind = 'file'
            url = $YuNetUrl
            sha256 = $YuNetSha256
            license = 'MIT'
            licenseUrl = "https://github.com/opencv/opencv_zoo/blob/$OpenCvZooCommit/models/face_detection_yunet/LICENSE"
            provenance = "OpenCV Zoo commit $OpenCvZooCommit, YuNet 2023mar model file."
        },
        [ordered]@{
            sourceId = 'opencv-zoo-sface-2021dec'
            kind = 'file'
            url = $SFaceUrl
            sha256 = $SFaceSha256
            license = 'Apache-2.0'
            licenseUrl = "https://github.com/opencv/opencv_zoo/blob/$OpenCvZooCommit/models/face_recognition_sface/LICENSE"
            provenance = "OpenCV Zoo commit $OpenCvZooCommit, SFace 2021dec model file."
        },
        [ordered]@{
            sourceId = 'naut-ffmpeg-8.1.3-win64-lgpl'
            kind = 'archive'
            url = $FfmpegArchiveUrl
            sha256 = $FfmpegArchiveSha256
            version = [string]$FfmpegLock.version
            license = [string]$FfmpegLock.license
            licenseUrl = "https://github.com/FFmpeg/FFmpeg/blob/$FfmpegCommit/COPYING.LGPLv3"
            provenance = "Naut reproducible FFmpeg runtime; recipe commit $($FfmpegLock.recipeCommit), BtbN recipe reference $BtbnCommit, FFmpeg source $FfmpegCommit; exact corresponding source is hash-pinned."
        },
        [ordered]@{
            sourceId = 'opencvsharp5-runtime-win'
            kind = 'nuget'
            package = $OpenCvSharpPackage
            version = $OpenCvSharpVersion
            license = 'Apache-2.0'
            licenseUrl = "https://github.com/shimat/opencvsharp/blob/$OpenCvSharpCommit/LICENSE"
            provenance = "NuGet package $OpenCvSharpPackage $OpenCvSharpVersion; corresponding OpenCvSharp tag commit $OpenCvSharpCommit."
        }
    )
    artifacts = @(
        [ordered]@{
            logicalName = 'YuNet face detector'
            kind = 'model'
            modelId = 'yunet'
            version = '2023mar'
            sourceId = 'opencv-zoo-yunet-2023mar'
            sourceEntryPath = "models/face_detection_yunet/$YuNetFileName"
            deployedRelativePath = $YuNetRelativePath
            sha256 = Get-Sha256Lower -Path $YuNetPath
            license = 'MIT'
            provenance = "Exact bytes from OpenCV Zoo commit $OpenCvZooCommit."
            consumers = @('profiling-worker')
            distribution = 'provisioned'
            featureImpactWhenMissing = 'Face detection and face profiling are unavailable.'
            mismatchBehavior = 'Fail closed and report the YuNet model capability unavailable.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        },
        [ordered]@{
            logicalName = 'SFace face recognizer'
            kind = 'model'
            modelId = 'sface'
            version = '2021dec'
            sourceId = 'opencv-zoo-sface-2021dec'
            sourceEntryPath = "models/face_recognition_sface/$SFaceFileName"
            deployedRelativePath = $SFaceRelativePath
            sha256 = Get-Sha256Lower -Path $SFacePath
            license = 'Apache-2.0'
            provenance = "Exact bytes from OpenCV Zoo commit $OpenCvZooCommit."
            consumers = @('profiling-worker')
            distribution = 'provisioned'
            featureImpactWhenMissing = 'Face recognition embeddings and related-profile ranking are unavailable.'
            mismatchBehavior = 'Fail closed and report the SFace model capability unavailable.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        },
        [ordered]@{
            logicalName = 'FFmpeg executable'
            kind = 'tool'
            toolId = 'ffmpeg'
            version = [string]$FfmpegLock.version
            sourceId = 'naut-ffmpeg-8.1.3-win64-lgpl'
            sourceEntryPath = $FfmpegSourceEntry
            deployedRelativePath = 'tools/ffmpeg.exe'
            sha256 = Get-Sha256Lower -Path $FfmpegPath
            license = [string]$FfmpegLock.license
            provenance = "Extracted from independently hash-pinned Naut FFmpeg runtime; FFmpeg source $FfmpegCommit; binary hash and linked-library inventory verified against provenance."
            consumers = @('app')
            distribution = 'provisioned'
            featureImpactWhenMissing = 'Video preview derivation is unavailable.'
            mismatchBehavior = 'Fail closed and do not execute an unverified ffmpeg binary.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        },
        [ordered]@{
            logicalName = 'FFprobe executable'
            kind = 'tool'
            toolId = 'ffprobe'
            version = [string]$FfmpegLock.version
            sourceId = 'naut-ffmpeg-8.1.3-win64-lgpl'
            sourceEntryPath = $FfprobeSourceEntry
            deployedRelativePath = 'tools/ffprobe.exe'
            sha256 = Get-Sha256Lower -Path $FfprobePath
            license = [string]$FfmpegLock.license
            provenance = "Extracted from independently hash-pinned Naut FFmpeg runtime; FFmpeg source $FfmpegCommit; binary hash and linked-library inventory verified against provenance."
            consumers = @('app')
            distribution = 'provisioned'
            featureImpactWhenMissing = 'Video technical metadata extraction is unavailable.'
            mismatchBehavior = 'Fail closed and do not execute an unverified ffprobe binary.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        },
        [ordered]@{
            logicalName = 'OpenCvSharp native runtime'
            kind = 'native'
            version = $OpenCvSharpVersion
            sourceId = 'opencvsharp5-runtime-win'
            sourceEntryPath = $OpenCvSourceEntry
            deployedRelativePath = $OpenCvDeployedRelativePath
            sha256 = $OpenCvPublishedNativeSha
            license = 'Apache-2.0'
            provenance = "Published byte matches the restored $OpenCvSharpPackage $OpenCvSharpVersion package exactly."
            consumers = @('profiling-worker')
            distribution = 'build'
            featureImpactWhenMissing = 'YuNet/SFace inference cannot load.'
            mismatchBehavior = 'Fail closed; profiling worker native inference capability is unavailable.'
            startupBlocking = $false
            releaseRequired = $true
            contentState = 'PROVISIONED'
        }
    )
    declinedArtifacts = @()
}
Write-JsonUtf8NoBom -Value $deploymentManifest -Path $DeploymentManifestPath -Depth 12

$parsedDeployment = Get-Content -LiteralPath $DeploymentManifestPath -Raw | ConvertFrom-Json
if ($parsedDeployment.schemaVersion -ne 2 -or $parsedDeployment.artifacts.Count -ne 5) {
    throw 'Generated deployment/artifacts.json failed structural validation.'
}
foreach ($artifact in $parsedDeployment.artifacts) {
    $artifactPath = Join-Path $RuntimeInstallRoot ([string]$artifact.deployedRelativePath).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Deployment manifest points to a missing file: $($artifact.deployedRelativePath)"
    }
    Assert-Sha256 -Path $artifactPath -Expected ([string]$artifact.sha256) -Label ([string]$artifact.logicalName)
}

foreach ($requiredRelativePath in $RequiredReleaseMembers) {
    Assert-RequiredFile -Root $InstallRoot -RelativePath $requiredRelativePath
}
foreach ($requiredFileName in $RequiredUniqueFileNames) {
    $matches = @(Get-ChildItem -LiteralPath $InstallRoot -File -Recurse -Filter $requiredFileName)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one required runtime member named '$requiredFileName'; got $($matches.Count)."
    }
}

$IncrementalMarkerPath = Join-Path $InstallRoot 'runtime/deployment/incremental-update.json'
Write-JsonUtf8NoBom -Value ([ordered]@{ schemaVersion = 1 }) -Path $IncrementalMarkerPath -Depth 2
$ReleaseManifestPath = Join-Path $InstallRoot 'release-manifest.json'
$releaseFiles = @(
    Get-ChildItem -LiteralPath $InstallRoot -File -Recurse |
        Where-Object { -not [string]::Equals($_.FullName, $ReleaseManifestPath, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            $relative = Normalize-RelativePath -Path ((Get-RelativePathCompat -BasePath $InstallRoot -Path $_.FullName))
            if ($relative.StartsWith('src/', [StringComparison]::OrdinalIgnoreCase) -or
                $relative.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase) -or
                $relative.StartsWith('Vault/', [StringComparison]::OrdinalIgnoreCase) -or
                $relative.EndsWith('.db', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Forbidden payload member detected: $relative"
            }
            [ordered]@{
                relativePath = $relative
                byteLength = $_.Length
                sha256 = Get-Sha256Lower -Path $_.FullName
                role = Get-ReleaseRole -RelativePath $relative
            }
        } |
        Sort-Object { $_.relativePath }
)

$releaseManifest = [ordered]@{
    schemaVersion = 1
    productId = $ProductId
    productVersion = $ProductVersion
    runtimeIdentifier = $RuntimeIdentifier
    files = $releaseFiles
}
Write-JsonUtf8NoBom -Value $releaseManifest -Path $ReleaseManifestPath -Depth 8

$rootFiles = @(Get-ChildItem -LiteralPath $InstallRoot -File | Select-Object -ExpandProperty Name)
$rootDirectories = @(Get-ChildItem -LiteralPath $InstallRoot -Directory | Select-Object -ExpandProperty Name)
if ($rootFiles.Count -ne 2 -or
    -not ($rootFiles -contains $AppExe) -or
    -not ($rootFiles -contains 'release-manifest.json') -or
    $rootDirectories.Count -ne 1 -or
    -not [string]::Equals($rootDirectories[0], $RuntimeDirectoryName, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Canonical ZIP root must contain only $AppExe, release-manifest.json, and $RuntimeDirectoryName/."
}

$parsedRelease = Get-Content -LiteralPath $ReleaseManifestPath -Raw | ConvertFrom-Json
if ($parsedRelease.schemaVersion -ne 1 -or
    $parsedRelease.productId -ne $ProductId -or
    $parsedRelease.productVersion -ne $ProductVersion -or
    $parsedRelease.runtimeIdentifier -ne $RuntimeIdentifier) {
    throw 'Generated release-manifest.json identity is invalid.'
}

$manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $parsedRelease.files) {
    $relative = [string]$entry.relativePath
    if (-not $manifestPaths.Add($relative)) {
        throw "Duplicate release manifest path: $relative"
    }

    $path = Join-Path $InstallRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release manifest points to a missing file: $relative"
    }

    $info = Get-Item -LiteralPath $path
    if ($info.Length -ne [long]$entry.byteLength) {
        throw "Release manifest byte length mismatch: $relative"
    }
    Assert-Sha256 -Path $path -Expected ([string]$entry.sha256) -Label "release member $relative"
}

foreach ($requiredRelativePath in $RequiredReleaseMembers) {
    if (-not $manifestPaths.Contains($requiredRelativePath)) {
        throw "Release manifest is missing required membership: $requiredRelativePath"
    }
}
foreach ($requiredFileName in $RequiredUniqueFileNames) {
    $count = @($parsedRelease.files | Where-Object {
        [string]::Equals([IO.Path]::GetFileName([string]$_.relativePath), $requiredFileName, [StringComparison]::OrdinalIgnoreCase)
    }).Count
    if ($count -ne 1) {
        throw "Release manifest must contain exactly one required runtime member named '$requiredFileName'."
    }
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
Compress-Archive -Path (Join-Path $InstallRoot '*') -DestinationPath $ZipPath -CompressionLevel Optimal
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf) -or (Get-Item -LiteralPath $ZipPath).Length -le 0) {
    throw 'ZIP packaging did not produce a non-empty archive.'
}

if (Test-Path -LiteralPath $VerificationRoot) {
    Remove-Item -LiteralPath $VerificationRoot -Recurse -Force
}
Expand-Archive -LiteralPath $ZipPath -DestinationPath $VerificationRoot -Force

$expandedReleasePath = Join-Path $VerificationRoot 'release-manifest.json'
if (-not (Test-Path -LiteralPath $expandedReleasePath -PathType Leaf)) {
    throw 'ZIP verification failed: release-manifest.json is missing after extraction.'
}
$expandedManifest = Get-Content -LiteralPath $expandedReleasePath -Raw | ConvertFrom-Json

$expectedExpanded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$expectedExpanded.Add('release-manifest.json')
foreach ($entry in $expandedManifest.files) {
    [void]$expectedExpanded.Add([string]$entry.relativePath)
    $expandedPath = Join-Path $VerificationRoot ([string]$entry.relativePath).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $expandedPath -PathType Leaf)) {
        throw "ZIP verification failed: missing $($entry.relativePath)"
    }

    $expandedInfo = Get-Item -LiteralPath $expandedPath
    if ($expandedInfo.Length -ne [long]$entry.byteLength) {
        throw "ZIP verification failed: byte length mismatch for $($entry.relativePath)"
    }
    Assert-Sha256 -Path $expandedPath -Expected ([string]$entry.sha256) -Label "ZIP member $($entry.relativePath)"
}

$actualExpanded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $VerificationRoot -File -Recurse) {
    [void]$actualExpanded.Add((Normalize-RelativePath -Path ((Get-RelativePathCompat -BasePath $VerificationRoot -Path $file.FullName))))
}
if (-not $actualExpanded.SetEquals($expectedExpanded)) {
    $unexpected = @($actualExpanded | Where-Object { -not $expectedExpanded.Contains($_) })
    $missing = @($expectedExpanded | Where-Object { -not $actualExpanded.Contains($_) })
    throw "ZIP membership mismatch. Unexpected=[$($unexpected -join ', ')] Missing=[$($missing -join ', ')]"
}

$expandedRootFiles = @(Get-ChildItem -LiteralPath $VerificationRoot -File | Select-Object -ExpandProperty Name)
$expandedRootDirectories = @(Get-ChildItem -LiteralPath $VerificationRoot -Directory | Select-Object -ExpandProperty Name)
if ($expandedRootFiles.Count -ne 2 -or
    -not ($expandedRootFiles -contains $AppExe) -or
    -not ($expandedRootFiles -contains 'release-manifest.json') -or
    $expandedRootDirectories.Count -ne 1 -or
    -not [string]::Equals($expandedRootDirectories[0], $RuntimeDirectoryName, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ZIP verification failed the canonical two-file plus runtime directory root contract.'
}

$zipSha256 = Get-Sha256Lower -Path $ZipPath
$zipBytes = (Get-Item -LiteralPath $ZipPath).Length

# External feed authority for the exact ZIP produced above. This is intentionally generated only
# after final archive verification, so payloadByteLength and payloadSha256 cannot drift from the ZIP.
$UpdateManifestPath = Join-Path $OutputDirectory 'update.json'
$updateManifest = [ordered]@{
    schemaVersion = 1
    productId = $ProductId
    productVersion = $ProductVersion
    runtimeIdentifier = $RuntimeIdentifier
    payloadByteLength = [long]$zipBytes
    payloadSha256 = $zipSha256
    minimumCompatibleVersion = $null
    files = $releaseFiles
}
Write-JsonUtf8NoBom -Value $updateManifest -Path $UpdateManifestPath -Depth 8

$parsedUpdate = Get-Content -LiteralPath $UpdateManifestPath -Raw | ConvertFrom-Json
if ($parsedUpdate.schemaVersion -ne 1 -or
    $parsedUpdate.productId -ne $ProductId -or
    $parsedUpdate.productVersion -ne $ProductVersion -or
    $parsedUpdate.runtimeIdentifier -ne $RuntimeIdentifier -or
    [long]$parsedUpdate.payloadByteLength -ne [long]$zipBytes -or
    -not [string]::Equals([string]$parsedUpdate.payloadSha256, $zipSha256, [StringComparison]::Ordinal) -or
    $parsedUpdate.files.Count -ne $releaseFiles.Count) {
    throw 'Generated update.json failed structural or payload-identity validation.'
}

Write-Host "PACKAGE_READY=$ZipPath"
Write-Host "PACKAGE_SHA256=$zipSha256"
Write-Host "PACKAGE_BYTES=$zipBytes"
Write-Host "UPDATE_MANIFEST=$UpdateManifestPath"
Write-Host "INSTALL_FILE_COUNT=$($releaseFiles.Count + 1)"
Write-Host "LAUNCHER_PATH=$LauncherRelativePath"
Write-Host "APP_PATH=$AppRelativePath"
Write-Host "WORKER_PATH=$WorkerRelativePath"
Write-Host "UPDATER_PATH=$UpdaterRelativePath"
Write-Host "FFMPEG_VERSION=$ffmpegVersionLine"
Write-Host "FFPROBE_VERSION=$ffprobeVersionLine"
