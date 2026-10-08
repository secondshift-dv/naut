[CmdletBinding()]
param([switch]$Offline)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$probeRoot = Join-Path $repositoryRoot 'out/release-layout-check'
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
$runtimeProject = [Security.SecurityElement]::Escape((Join-Path $repositoryRoot 'src/Neuterradise.Runtime/Neuterradise.Runtime.csproj'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RestoreLockedMode>false</RestoreLockedMode>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$runtimeProject" /></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $probeRoot 'check.csproj'), $project)
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts/verification/diagnostics/ReleaseLayoutProbe.cs') -Destination (Join-Path $probeRoot 'Program.cs')
Push-Location $repositoryRoot
try {
    if ($Offline) {
        $offlineSource = Join-Path $probeRoot 'offline-source';
        New-Item -ItemType Directory -Path $offlineSource -Force | Out-Null
        & dotnet restore (Join-Path $probeRoot 'check.csproj') --runtime win-x64 --source $offlineSource -m:1 -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw "Offline release layout restore failed with exit code $LASTEXITCODE." }
    }
    $buildArguments = @('build', (Join-Path $probeRoot 'check.csproj'), '-c', 'Release', '-r', 'win-x64', '-m:1')
    if ($Offline) { $buildArguments += @('--no-restore', '-p:NuGetAudit=false') }
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Release layout probe build failed with exit code $LASTEXITCODE." }
    $arguments = @('run', '--project', (Join-Path $probeRoot 'check.csproj'), '-c', 'Release', '-r', 'win-x64', '--no-build', '--no-restore')
    $arguments += @('--', (Join-Path $probeRoot ('fixtures-' + [Guid]::NewGuid().ToString('N'))))
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Release layout verification failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
