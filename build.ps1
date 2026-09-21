param([string]$Dsh, [string]$Origin, [switch]$Aot, [switch]$Package)
$ErrorActionPreference = 'Stop'
$arguments = @((Join-Path $PSScriptRoot 'scripts/verify.py'))
if ($Dsh) { $arguments += @('--dsh', $Dsh) }
if ($Origin) { $arguments += @('--origin', $Origin) }
if ($Aot) { $arguments += '--aot' }
if ($Package) { $arguments += '--package' }
& python @arguments
if ($LASTEXITCODE -ne 0) { throw "Verification failed with exit code $LASTEXITCODE" }
