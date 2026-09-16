#Requires -Version 5.1
<#
  编译 SocOtaUpgrade 工具包：
    SocOtaUpgrade/
      SocOtaUpgrade.exe
      soc_ota_config.json
      tools/adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CsDir = Join-Path $Root 'cs'
$Bundled = Join-Path $Root 'bundled'
$ScriptsDir = Split-Path $Root -Parent
$DistDir = Join-Path $ScriptsDir 'SocOtaUpgrade'
$ToolsDir = Join-Path $DistDir 'tools'
$OutExe = Join-Path $DistDir 'SocOtaUpgrade.exe'
$HalModules = Join-Path $ScriptsDir 'hal_modules.json'
$Csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $Csc)) {
    $Csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}

function Ensure-PlatformTools {
    New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    $adb = Join-Path $ToolsDir 'adb.exe'
    if ((Test-Path $adb) -and ((Get-Item $adb).Length -gt 100000)) {
        Write-Host "Reuse existing adb in tools"
        return
    }
    $Temp = Join-Path $env:TEMP ("soc_pt_" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $Temp -Force | Out-Null
    try {
        $ptZip = Join-Path $Temp 'platform-tools.zip'
        Write-Host 'Downloading platform-tools ...'
        Invoke-WebRequest -Uri 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $ptZip -UseBasicParsing
        Expand-Archive -Path $ptZip -DestinationPath $Temp -Force
        foreach ($f in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
            Copy-Item (Join-Path $Temp "platform-tools\$f") (Join-Path $ToolsDir $f) -Force
        }
        Write-Host "Deployed tools to $ToolsDir"
    }
    finally {
        Remove-Item $Temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $Csc)) {
    throw "csc.exe not found. Install .NET Framework 4.x"
}

function Convert-PngToIco {
    param(
        [string]$PngPath,
        [string]$IcoPath
    )
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile($PngPath)
    try {
        $bmp = New-Object System.Drawing.Bitmap($src, 256, 256)
        try {
            $hIcon = $bmp.GetHicon()
            $icon = [System.Drawing.Icon]::FromHandle($hIcon)
            try {
                $stream = [System.IO.File]::Open($IcoPath, [System.IO.FileMode]::Create)
                try {
                    $icon.Save($stream)
                }
                finally {
                    $stream.Close()
                }
            }
            finally {
                $icon.Dispose()
            }
        }
        finally {
            $bmp.Dispose()
        }
    }
    finally {
        $src.Dispose()
    }
}

function Ensure-AppIcon {
    param([string]$DistDirectory)
    $assetsDir = Join-Path $Root 'assets'
    $png = Join-Path $assetsDir 'app.png'
    if (-not (Test-Path $png)) {
        Write-Host 'Skip app icon: assets/app.png not found'
        return $null
    }
    $ico = Join-Path $DistDirectory 'app.ico'
    Convert-PngToIco -PngPath $png -IcoPath $ico
    Copy-Item $png (Join-Path $DistDirectory 'app.png') -Force
    Write-Host "App icon: $ico"
    return $ico
}

Ensure-PlatformTools
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
$AppIco = Ensure-AppIcon -DistDirectory $DistDir

$sources = @(
    (Join-Path $CsDir 'Program.cs'),
    (Join-Path $CsDir 'ProgressReporter.cs'),
    (Join-Path $CsDir 'AppBranding.cs'),
    (Join-Path $CsDir 'AppIconHelper.cs'),
    (Join-Path $CsDir 'ILogSink.cs'),
    (Join-Path $CsDir 'Config.cs'),
    (Join-Path $CsDir 'Log.cs'),
    (Join-Path $CsDir 'SessionFileLogSink.cs'),
    (Join-Path $CsDir 'DeviceLogCollector.cs'),
    (Join-Path $CsDir 'FileLogHelper.cs'),
    (Join-Path $CsDir 'AdbProcessTracker.cs'),
    (Join-Path $CsDir 'AdbHelper.cs'),
    (Join-Path $CsDir 'AdbRootHelper.cs'),
    (Join-Path $CsDir 'SerialHelper.cs'),
    (Join-Path $CsDir 'OtaUpdateScript.cs'),
    (Join-Path $CsDir 'OtaPushHelper.cs'),
    (Join-Path $CsDir 'UpgradeSession.cs'),
    (Join-Path $CsDir 'UpgradeService.cs'),
    (Join-Path $CsDir 'BootWaitHelper.cs'),
    (Join-Path $CsDir 'RecoveryBootHelper.cs'),
    (Join-Path $CsDir 'OtaVersionVerifier.cs'),
    (Join-Path $CsDir 'AbSlotHelper.cs'),
    (Join-Path $CsDir 'DeviceMaintenanceHelper.cs'),
    (Join-Path $CsDir 'HalModulesManifestHelper.cs'),
    (Join-Path $CsDir 'HalStatusChecker.cs'),
    (Join-Path $CsDir 'HalScriptPoolRunner.cs'),
    (Join-Path $CsDir 'SessionDetectReportHelper.cs'),
    (Join-Path $CsDir 'VerificationResultsUiHelper.cs'),
    (Join-Path $CsDir 'EmailListenConfigHelper.cs'),
    (Join-Path $CsDir 'EmailListenRunner.cs'),
    (Join-Path $CsDir 'SevenZipPackageHelper.cs'),
    (Join-Path $CsDir 'UpgradeApp.cs'),
    (Join-Path $CsDir 'MainForm.cs')
)

$refs = @(
    '/reference:System.Web.Extensions.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll'
)

Write-Host "Building $OutExe ..."
$iconArg = @()
if ($AppIco -and (Test-Path $AppIco)) {
    $iconArg = @("/win32icon:$AppIco")
}
& $Csc /nologo /target:winexe /platform:anycpu /optimize+ `
    "/out:$OutExe" `
    @iconArg `
    @refs `
    @sources

if ($LASTEXITCODE -ne 0) {
    throw "csc build failed, exit=$LASTEXITCODE"
}

if (-not (Test-Path (Join-Path $DistDir 'soc_ota_config.json'))) {
    $ConfigExample = Join-Path $ScriptsDir 'soc_ota_config.json.example'
    if (Test-Path $ConfigExample) {
        Copy-Item $ConfigExample (Join-Path $DistDir 'soc_ota_config.json')
    }
}

if (Test-Path $HalModules) {
    Copy-Item $HalModules (Join-Path $DistDir 'hal_modules.json') -Force
}

Write-Host ''
Write-Host "[OK] Output: $DistDir"
Write-Host "  SocOtaUpgrade.exe"
Write-Host "  app.ico / app.png"
Write-Host "  soc_ota_config.json"
Write-Host "  hal_modules.json"
Write-Host "  tools\adb.exe (+ DLL)"
Write-Host ''
Write-Host 'Copy the entire SocOtaUpgrade folder to the target machine.'
