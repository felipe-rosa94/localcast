$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot 'publish.ps1')

$iscc = @(
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host 'Inno Setup não encontrado. Instale com: winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
    Write-Host 'Depois rode este script novamente.'
    exit 1
}

& $iscc (Join-Path $root 'installer\LocalCast.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador' }
Write-Host "Instalador pronto: $(Join-Path $root 'dist\LocalCast-Setup.exe')"
