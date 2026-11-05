[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TargetDll,
    [switch]$Offline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$toolProject = Join-Path $PSScriptRoot 'UnoInteropPatch.csproj'
$target = [IO.Path]::GetFullPath($TargetDll)
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
    throw "Uno Win32 runtime assembly is missing: $target"
}
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('naut-uno-interop-' + [Guid]::NewGuid().ToString('N'))
$patchPath = Join-Path $tempRoot 'Uno.UI.Runtime.Skia.Win32.dll'
$expectedSha256 = 'd18c985c54f7e6f211a19d52b88f788cdafd0fef16f2165e371b0f75de730b2d'
try {
    New-Item -ItemType Directory -Path $tempRoot -ErrorAction Stop | Out-Null
    $restoreArgs = @('restore', $toolProject, '--locked-mode', '-m:1', '-p:NuGetAudit=false')
    if ($Offline) {
        $offlineSource = Join-Path $tempRoot 'offline-source'
        New-Item -ItemType Directory -Path $offlineSource | Out-Null
        $restoreArgs += @('--source', $offlineSource)
    }
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Uno ABI patch tool locked restore failed.' }

    & dotnet run --project $toolProject --configuration Release --no-restore -- $target $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'Uno ABI patch generation failed.' }

    $actual = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $expectedSha256) {
        throw "Generated Uno ABI patch identity differs from verified build: $actual"
    }
    Copy-Item -LiteralPath $patchPath -Destination $target -Force
    $installed = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($installed -cne $expectedSha256) {
        throw 'Installed Uno ABI patch failed hash verification.'
    }
    Write-Host 'UNO_UIA_NATIVE_ABI=PASS'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
    foreach ($directory in @('bin', 'obj')) {
        $owned = Join-Path $PSScriptRoot $directory
        if (Test-Path -LiteralPath $owned) {
            Remove-Item -LiteralPath $owned -Recurse -Force
        }
    }
}
