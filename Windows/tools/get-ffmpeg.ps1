$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot 'ffmpeg'
$zip  = Join-Path $env:TEMP 'ffmpeg-localcast.zip'
$url  = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Write-Host "Baixando ffmpeg..."
Invoke-WebRequest -Uri $url -OutFile $zip
$tmp = Join-Path $env:TEMP 'ffmpeg-localcast'
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
Expand-Archive -Path $zip -DestinationPath $tmp
$exe = Get-ChildItem -Path $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1
Copy-Item $exe.FullName (Join-Path $dest 'ffmpeg.exe') -Force
Remove-Item $zip -Force
Remove-Item -Recurse -Force $tmp
Write-Host "ffmpeg instalado em $dest"
& (Join-Path $dest 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
