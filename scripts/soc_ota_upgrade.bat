@echo off
setlocal EnableExtensions

REM 丰田T2版本测试工具（PowerShell 脚本入口，双击运行或命令行传参）
REM 用法:
REM   soc_ota_upgrade.bat
REM   soc_ota_upgrade.bat "D:\path\to\ota_package.zip"

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%soc_ota_upgrade.ps1"

if not exist "%PS_SCRIPT%" (
    echo [ERROR] 未找到脚本: %PS_SCRIPT%
    exit /b 1
)

where adb >nul 2>&1
if errorlevel 1 (
    echo [ERROR] 未找到 adb，请先配置 Android SDK Platform-Tools 并加入 PATH
    exit /b 1
)

if "%~1"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" -PackagePath "%~1"
)

set "EXIT_CODE=%ERRORLEVEL%"
echo.
if "%EXIT_CODE%"=="0" (
    echo [OK] 升级流程结束
) else (
    echo [FAIL] 升级流程失败，退出码: %EXIT_CODE%
)
pause
exit /b %EXIT_CODE%
