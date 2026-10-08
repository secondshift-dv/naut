[CmdletBinding()]
param([switch]$Offline, [string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$probeRoot = Join-Path $repositoryRoot 'out/curated-packs-check'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'out/pack-downloads' }
New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
$runtimeProject = [Security.SecurityElement]::Escape((Join-Path $repositoryRoot 'src/Neuterradise.Runtime/Neuterradise.Runtime.csproj'))
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><RuntimeIdentifier>win-x64</RuntimeIdentifier><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><RestoreLockedMode>false</RestoreLockedMode></PropertyGroup>
  <ItemGroup><ProjectReference Include="$runtimeProject" /></ItemGroup>
</Project>
"@
$projectPath = Join-Path $probeRoot 'check.csproj'
[IO.File]::WriteAllText($projectPath, $project)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'diagnostics/CuratedPacksProbe.cs') -Destination (Join-Path $probeRoot 'Program.cs')
Push-Location $repositoryRoot
try {
    $restore = @('restore', $projectPath, '-r', 'win-x64', '-m:1', '-p:NuGetAudit=false')
    if ($Offline) {
        $offlineSource = Join-Path $probeRoot 'offline-source'
        New-Item -ItemType Directory -Path $offlineSource -Force | Out-Null
        $restore += @('--source', $offlineSource)
    }
    & dotnet @restore
    if ($LASTEXITCODE -ne 0) { throw 'Pack probe restore failed.' }
    & dotnet run --project $projectPath -c Release -r win-x64 --no-restore -- $repositoryRoot ([IO.Path]::GetFullPath($OutputDirectory))
    if ($LASTEXITCODE -ne 0) { throw 'Curated pack verification failed.' }
}
finally { Pop-Location }
