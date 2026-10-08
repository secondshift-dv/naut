[CmdletBinding()]
param([switch]$Offline, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '../verification/verify-packs.ps1') -Offline:$Offline -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Pack packaging failed.' }
