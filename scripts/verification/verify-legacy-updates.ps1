[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$LegacyRepository,
      [Parameter(Mandatory=$true)][string]$DistributionRoot,
      [string]$SignaturePath, [switch]$Preflight)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repositoryRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
[xml]$buildProps=Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$targetVersion=$buildProps.SelectSingleNode('/Project/PropertyGroup/ProductVersion').InnerText
$sourceFiles=@(
    'src/Neuterradise.Profiling.Protocol/ProfilingRuntimeEnvironment.cs',
    'src/Neuterradise.Runtime/SystemServices/ProductIdentity.cs',
    'src/Neuterradise.Runtime/SystemServices/Storage/RootPathRules.cs',
    'src/Neuterradise.Runtime/SystemServices/Storage/InstallPaths.cs',
    'src/Neuterradise.Runtime/SystemServices/Storage/AppStatePaths.cs',
    'src/Neuterradise.Runtime/SystemServices/Updates/UpdateManifest.cs',
    'src/Neuterradise.Runtime/SystemServices/Updates/UpdateTrustPolicy.cs',
    'src/Neuterradise.Runtime/SystemServices/Updates/UpdateCheckService.cs',
    'src/Neuterradise.Runtime/SystemServices/Updates/UpdateDownloadService.cs',
    'src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageStager.cs',
    'src/Neuterradise.Runtime/SystemServices/Updates/UpdatePackageValidator.cs',
    'src/Neuterradise.Runtime/SystemServices/Updates/UpdatePublisherSignature.cs',
    'src/Neuterradise.Runtime/SystemServices/Updates/UpdateUriPolicy.cs'
)
foreach ($version in @('0.0.1','0.0.2')) {
    $probeRoot=Join-Path $repositoryRoot "out/legacy-updater-$version"
    New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
    foreach ($file in $sourceFiles) {
        $source=@(& git -C $LegacyRepository show "v${version}:$file")
        if ($LASTEXITCODE -ne 0) { throw "Exact legacy source is unavailable: v$version $file" }
        [IO.File]::WriteAllText((Join-Path $probeRoot ([IO.Path]::GetFileName($file))), ($source -join [Environment]::NewLine))
    }
    $configuration=@(& git -C $LegacyRepository show "v${version}:src/Neuterradise.Runtime/SystemServices/Storage/AppConfigurationStore.cs") -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0 -or -not $configuration.Contains('https://github.com/secondshift-dv/naut/releases/latest/download/update.json')) {
        throw "Legacy feed authority changed: v$version"
    }
    $legacyContract=@(& git -C $LegacyRepository show "v${version}:release-contract.json") -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw 'Legacy release contract unavailable.' }
    [IO.File]::WriteAllText((Join-Path $probeRoot 'release-contract.json'),$legacyContract)
    foreach ($file in @('ReleaseContract.cs','ReleaseLayout.cs','UpdateManifestAuthority.cs')) {
        $source=@(& git -C $LegacyRepository show "v${version}:src/Neuterradise.Release.Contracts/$file")
        if ($LASTEXITCODE -ne 0) { throw 'Legacy shared authority source unavailable.' }
        [IO.File]::WriteAllText((Join-Path $probeRoot $file),($source -join [Environment]::NewLine))
    }
    $project=@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier><ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable><RestoreLockedMode>false</RestoreLockedMode>
    <ProductVersion>$version</ProductVersion><ProductAssemblyVersion>$version.0</ProductAssemblyVersion>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="release-contract.json" LogicalName="Neuterradise.Release.Contracts.release-contract.json" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $probeRoot 'check.csproj'),$project)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'diagnostics/LegacyUpdateProbe.cs') -Destination (Join-Path $probeRoot 'Program.cs')
    & dotnet build (Join-Path $probeRoot 'check.csproj') -c Release -r win-x64 -m:1 -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Exact legacy updater compile failed: $version" }
    $arguments=@('run','--project',(Join-Path $probeRoot 'check.csproj'),'-c','Release','-r','win-x64','--no-build','--no-restore','--',$DistributionRoot,(Join-Path $probeRoot ('fixtures-'+[Guid]::NewGuid().ToString('N'))))
    if ($Preflight) { $arguments += '--preflight' }
    elseif ($SignaturePath) { $arguments += $SignaturePath }
    else { $arguments += (Join-Path $DistributionRoot 'update-signature.json') }
    $arguments += $targetVersion
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Direct legacy compatibility failed: $version" }
}
