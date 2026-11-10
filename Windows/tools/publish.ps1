$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$ffmpeg = Join-Path $PSScriptRoot 'ffmpeg\ffmpeg.exe'
if (-not (Test-Path $ffmpeg)) { & (Join-Path $PSScriptRoot 'get-ffmpeg.ps1') }

$out = Join-Path $root 'dist\LocalCast'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

dotnet publish (Join-Path $root 'src\LocalCast.App') -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }

if (-not (Test-Path (Join-Path $out 'tools\ffmpeg\ffmpeg.exe'))) {
    New-Item -ItemType Directory -Force -Path (Join-Path $out 'tools\ffmpeg') | Out-Null
    Copy-Item $ffmpeg (Join-Path $out 'tools\ffmpeg\ffmpeg.exe')
}

$zip = Join-Path $root 'dist\LocalCast-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Pacote pronto: $zip"
