<#
.SYNOPSIS
Publica o Host (self-contained, win-x64) e compila o instalador com o Inno Setup 6.

.EXAMPLE
pwsh clients/windows-recorder/installer/build-installer.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string] $Version = '0.1.0',
    [string] $IsccPath,
    # Pasta de saída do publish; por padrão fica em artifacts/ (ignorada pelo git).
    [string] $PublishDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $root 'artifacts\publish\win-x64' }

function Find-Iscc {
    if ($IsccPath) { return $IsccPath }
    $found = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
    return $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

$iscc = Find-Iscc
if (-not $iscc -or -not (Test-Path $iscc)) {
    throw 'ISCC.exe nao encontrado. Instale com: winget install JRSoftware.InnoSetup --scope user (ou use -IsccPath).'
}

# Sem trimming (WPF nao suporta); .NET embutido para o Windows 11 limpo nao precisar instalar nada.
# So limpa uma pasta vazia ou que ja contem um publish do app: um -PublishDir errado nao apaga outra coisa.
if (Test-Path $PublishDir) {
    if ((Get-ChildItem $PublishDir -Force | Measure-Object).Count -gt 0 -and -not (Test-Path (Join-Path $PublishDir 'WhisperRecorder.exe'))) {
        throw "A pasta $PublishDir nao esta vazia e nao contem um publish do app; escolha outra em -PublishDir."
    }
    Remove-Item $PublishDir -Recurse -Force
}
dotnet publish (Join-Path $root 'src\Host\Host.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishTrimmed=false -p:DebugType=none -p:Version=$Version -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou ($LASTEXITCODE)." }

& $iscc "/DAppVersion=$Version" "/DPublishDir=$PublishDir" (Join-Path $PSScriptRoot 'WhisperRecorder.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou ($LASTEXITCODE)." }

$setup = Join-Path $root "artifacts\installer\WhisperRecorder-Setup-$Version.exe"
Write-Host "Instalador: $setup"
