[CmdletBinding()]
param([switch]$Offline, [string]$PackageRoot, [string]$BaselineZip, [string]$DistributionRoot, [switch]$RequireSignature)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
foreach ($entryPoint in @('scripts/build.ps1','scripts/release.ps1','scripts/packaging/package-win-x64.ps1')) {
    $syntaxTokens = $null
    $syntaxErrors = $null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot $entryPoint), [ref]$syntaxTokens, [ref]$syntaxErrors) | Out-Null
    if ($syntaxErrors.Count -gt 0) { throw "Canonical PowerShell syntax failed: $entryPoint" }
}
$probeRoot = Join-Path $repositoryRoot 'out/updater-check'
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
$runtime = [Security.SecurityElement]::Escape((Join-Path $repositoryRoot 'src/Neuterradise.Runtime/Neuterradise.Runtime.csproj'))
$updater = [Security.SecurityElement]::Escape((Join-Path $repositoryRoot 'src/Neuterradise.Updater/Neuterradise.Updater.csproj'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier><ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable><RestoreLockedMode>false</RestoreLockedMode>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$runtime" /><ProjectReference Include="$updater" /></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $probeRoot 'check.csproj'), $project)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'diagnostics/UpdaterProbe.cs') -Destination (Join-Path $probeRoot 'Program.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'diagnostics/UpdateResponsivenessProbe.cs') -Destination (Join-Path $probeRoot 'UpdateResponsivenessProbe.cs')
$buildArguments = @('build', (Join-Path $probeRoot 'check.csproj'), '-c', 'Release', '-r', 'win-x64', '-m:1', '-p:NuGetAudit=false')
if ($Offline) {
    $offlineSource = Join-Path $probeRoot 'offline-source'
    New-Item -ItemType Directory -Path $offlineSource -Force | Out-Null
    $buildArguments += @('--source', $offlineSource)
}
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Updater probe compile failed.' }
$arguments = @('run', '--project', (Join-Path $probeRoot 'check.csproj'), '-c', 'Release', '-r', 'win-x64', '--no-build', '--no-restore', '--', (Join-Path $probeRoot ('fixtures-' + [Guid]::NewGuid().ToString('N'))))
if ($BaselineZip -or $DistributionRoot -or $RequireSignature) {
    if (!$BaselineZip -or !$DistributionRoot -or $PackageRoot) {
        throw 'Package replacement verification requires BaselineZip and DistributionRoot.'
    }
    $arguments += @('--replace-package', (Resolve-Path -LiteralPath $BaselineZip).Path, (Resolve-Path -LiteralPath $DistributionRoot).Path)
    if ($RequireSignature) { $arguments += '--require-signature' }
}
elseif ($PackageRoot) { $arguments += $PackageRoot }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Updater verification failed.' }
