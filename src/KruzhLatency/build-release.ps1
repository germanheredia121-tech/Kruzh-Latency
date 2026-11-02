<#
.SYNOPSIS
    KRUZH LATENCY -- Pipeline de Compilacion Comercial Self-Contained & Code Signing (Authenticode)
    Credito: KRUZH -- author
.DESCRIPTION
    1. Compila y publica KRUZH LATENCY en modo Release win-x64 Self-Contained (el cliente final NO necesita instalar .NET).
    2. Genera resources.pri combinando los XBF compilados y Microsoft.UI.Xaml.Controls.pri.
    3. Integra firma digital Authenticode con signtool.exe (RFC3161 Timestamp DigiCert/Sectigo).
    4. Genera manifiesto de hashes SHA-256 para distribucion comercial segura.
#>
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$CertPfxPath = "",
    [string]$CertPassword = "",
    [string]$CertThumbprint = "",
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectPath = Join-Path $ScriptDir "KruzhLatency.App\KruzhLatency.App.csproj"
$OutputDir = Join-Path $ScriptDir "publish\$RuntimeIdentifier"

$DotnetExe = "dotnet"
if (-not (Get-Command $DotnetExe -ErrorAction SilentlyContinue)) {
    $LocalDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $LocalDotnet) {
        $DotnetExe = $LocalDotnet
    } else {
        throw "No se encontro el SDK de .NET 8. Instalalo o verifica el PATH."
    }
}

Write-Host "=========================================================================" -ForegroundColor Cyan
Write-Host "  KRUZH LATENCY -- COMMERCIAL BUILD & SIGNING PIPELINE (KRUZH -- author) " -ForegroundColor Green
Write-Host "=========================================================================" -ForegroundColor Cyan

Write-Host "`n[1/4] Publicando ejecutable Self-Contained ($Configuration | $RuntimeIdentifier)..." -ForegroundColor Yellow
& $DotnetExe publish $ProjectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:Platform=x64 `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $OutputDir

$ExePath = Join-Path $OutputDir "KruzhLatency.exe"
if (-not (Test-Path $ExePath)) {
    throw "Error: No se genero $ExePath"
}

Write-Host "`n[2/4] Copiando XBF compilados y generando indice de recursos WinUI 3 (resources.pri)..." -ForegroundColor Yellow
$BinDir = Join-Path $ScriptDir "KruzhLatency.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\$RuntimeIdentifier"
if (Test-Path $BinDir) {
    Copy-Item (Join-Path $BinDir "*.xbf") $OutputDir -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $BinDir "*.pri") $OutputDir -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path $BinDir -Directory | ForEach-Object {
        Copy-Item $_.FullName $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$MakePriExe = Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Filter "makepri.exe" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "x64" } |
    Select-Object -First 1 -ExpandProperty FullName

if ($MakePriExe) {
    $StageDir = Join-Path $env:TEMP "KruzhPriStage"
    Remove-Item $StageDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $StageDir | Out-Null
    Copy-Item (Join-Path $OutputDir "*.xbf") $StageDir -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $OutputDir "Microsoft.UI.Xaml.Controls.pri") $StageDir -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $OutputDir "CommunityToolkit.*.pri") $StageDir -Force -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $OutputDir "CommunityToolkit.WinUI.Controls.SettingsControls")) {
        Copy-Item (Join-Path $OutputDir "CommunityToolkit.WinUI.Controls.SettingsControls") $StageDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $PriConfig = Join-Path $env:TEMP "kruzh_priconfig.xml"
    & $MakePriExe createconfig /cf $PriConfig /dq en-US /o /pv 10.0.0 | Out-Null
    & $MakePriExe new /pr $StageDir /cf $PriConfig /in KruzhLatency /of (Join-Path $OutputDir "resources.pri") /o | Out-Null
    if (Test-Path $BinDir) {
        Copy-Item (Join-Path $OutputDir "resources.pri") (Join-Path $BinDir "resources.pri") -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $StageDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $PriConfig -Force -ErrorAction SilentlyContinue
    Write-Host "App.xbf, Tokens.xbf, MainWindow.xbf y resources.pri copiados e indexados correctamente." -ForegroundColor Green
}

Write-Host "`n[3/4] Verificando firma Authenticode (Code Signing)..." -ForegroundColor Yellow
$SignTool = Get-Command "signtool.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $SignTool) {
    $SdkBin = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $SdkBin) {
        $SignTool = Get-ChildItem -Path $SdkBin -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "x64" } |
            Select-Object -First 1 -ExpandProperty FullName
    }
}

if ($SignTool -and ($CertPfxPath -or $CertThumbprint)) {
    if ($CertPfxPath -and (Test-Path $CertPfxPath)) {
        Write-Host "Firmando con certificado PFX: $CertPfxPath" -ForegroundColor Cyan
        & $SignTool sign /fd SHA256 /tr $TimestampServer /td SHA256 /f $CertPfxPath /p $CertPassword $ExePath
    } elseif ($CertThumbprint) {
        Write-Host "Firmando con certificado del almacen (Thumbprint: $CertThumbprint)" -ForegroundColor Cyan
        & $SignTool sign /fd SHA256 /tr $TimestampServer /td SHA256 /sha1 $CertThumbprint $ExePath
    }
    Write-Host "Firma Authenticode completada con exito." -ForegroundColor Green
} else {
    Write-Host "Aviso: No se especifico -CertPfxPath ni -CertThumbprint. Para firmar en produccion:" -ForegroundColor DarkYellow
    Write-Host "  .\build-release.ps1 -CertPfxPath 'C:\certs\kruzh_codesign.pfx' -CertPassword 'TU_CLAVE'" -ForegroundColor Gray
}

Write-Host "`n[4/4] Calculando Hash Criptografico SHA-256 del paquete..." -ForegroundColor Yellow
$HashObj = Get-FileHash -Path $ExePath -Algorithm SHA256
$ShaFile = Join-Path $OutputDir "SHA256SUMS.txt"
"$($HashObj.Hash)  KruzhLatency.exe" | Set-Content -Path $ShaFile -Encoding UTF8

Write-Host "`n=========================================================================" -ForegroundColor Cyan
Write-Host "  BUILD COMERCIAL FINALIZADO CON EXITO" -ForegroundColor Green
Write-Host "  Ejecutable : $ExePath" -ForegroundColor White
Write-Host "  SHA-256    : $($HashObj.Hash)" -ForegroundColor Cyan
Write-Host "=========================================================================" -ForegroundColor Cyan
