[CmdletBinding()]
param(
    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$SolutionPath = Join-Path $RepositoryRoot 'NeuTerradise.sln'
$BuildPropsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$OfflineSource = $null
$PreviousOfflineEnvironment = @{}

function Invoke-DotNetChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
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
    if (-not (Test-Path -LiteralPath $SolutionPath -PathType Leaf)) {
        throw "Solution was not found: $SolutionPath"
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'A local .NET 10 SDK is required.'
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or
        -not [string]::Equals($sdkVersion, '10.0.401', [StringComparison]::Ordinal)) {
        throw "Compile verification requires exact .NET SDK 10.0.401; resolved '$sdkVersion'."
    }

    [xml]$buildProps = Get-Content -LiteralPath $BuildPropsPath -Raw
    $identity = $buildProps.Project.PropertyGroup |
        Where-Object { $_.ProductRuntimeIdentifier } |
        Select-Object -First 1
    if ($null -eq $identity -or
        [string]::IsNullOrWhiteSpace([string]$identity.ProductRuntimeIdentifier)) {
        throw 'Directory.Build.props must define ProductRuntimeIdentifier.'
    }
    $runtimeIdentifier = [string]$identity.ProductRuntimeIdentifier

    & (Join-Path $PSScriptRoot 'verify-localization.ps1')
    & (Join-Path $PSScriptRoot 'verify-theme-contract.ps1')

    if ($Offline) {
        foreach ($name in @(
            'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE',
            'DOTNET_CLI_TELEMETRY_OPTOUT',
            'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
            'DOTNET_SDK_VULNERABILITY_CHECK_DISABLE',
            'NUGET_CERT_REVOCATION_MODE')) {
            $PreviousOfflineEnvironment[$name] =
                [Environment]::GetEnvironmentVariable($name, 'Process')
        }

        $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $env:DOTNET_SDK_VULNERABILITY_CHECK_DISABLE = '1'
        $env:NUGET_CERT_REVOCATION_MODE = 'offline'
    }

    Write-Host 'VERIFY_SCOPE=SOURCE'
    Write-Host "SDK=$sdkVersion"
    Write-Host "PRODUCT_RID=$runtimeIdentifier"
    Write-Host "NETWORK_MODE=$(if ($Offline) { 'OFFLINE' } else { 'CACHE_FIRST' })"

    $restoreArgs = @(
        'restore',
        $SolutionPath,
        '--runtime', $runtimeIdentifier,
        '--locked-mode',
        '-m:1',
        '-p:TreatWarningsAsErrors=true'
    )

    if ($Offline) {
        $OfflineSource = Join-Path ([IO.Path]::GetTempPath()) (
            'neuterradise-compile-offline-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $OfflineSource -Force | Out-Null
        $restoreArgs += @(
            '--source', $OfflineSource,
            '-p:NuGetAudit=false'
        )
    }

    Invoke-DotNetChecked -Arguments $restoreArgs

    $projectPaths = @(
        Get-Content -LiteralPath $SolutionPath |
            ForEach-Object {
                if ($_ -match 'Project\("[^"]+"\) = "[^"]+", "([^"]+\.csproj)",') {
                    Join-Path $RepositoryRoot $Matches[1]
                }
            }
    )

    if ($projectPaths.Count -eq 0) {
        throw 'No source projects were discovered from NeuTerradise.sln.'
    }

    foreach ($projectPath in $projectPaths) {
        $relativeProjectPath = (Get-RelativePathCompat -BasePath $RepositoryRoot -Path $projectPath).Replace('\', '/')
        if (-not $relativeProjectPath.StartsWith('src/', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Compile verification may build source projects only: $relativeProjectPath"
        }
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Source project was not found: $projectPath"
        }

        Write-Host "COMPILE_PROJECT=$relativeProjectPath"
        $buildArgs = @(
            'build',
            $projectPath,
            '--configuration', 'Release',
            '--runtime', $runtimeIdentifier,
            '--no-restore',
            '-m:1',
            '--nologo',
            '-p:ContinuousIntegrationBuild=true',
            '-warnaserror',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            '-p:WindowsPackageType=None',
            '-p:DisableUnoResizetizer=true',
            '-p:TargetsTriggeredByCompilation='
        )
        Invoke-DotNetChecked -Arguments $buildArgs
    }

    Write-Host "SOURCE_PROJECTS=$($projectPaths.Count)"
    Write-Host 'WARNINGS_AS_ERRORS=ALL'
    Write-Host 'COMPILE_VERIFY=PASS'
    Write-Host 'PACKAGE_CREATED=NO'
    Write-Host 'VAULT_TOUCHED=NO'
}
finally {
    Pop-Location

    foreach ($entry in $PreviousOfflineEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }

    if ($null -ne $OfflineSource -and (Test-Path -LiteralPath $OfflineSource)) {
        Remove-Item -LiteralPath $OfflineSource -Recurse -Force
    }
}
