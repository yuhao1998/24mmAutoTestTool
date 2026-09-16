@echo off
REM T2 ATF 升级入口启动器
REM   无参数 / --listen : 监听 build_pigeon 触发（默认）
REM   --package <path> : 直接升级本地包
setlocal
cd /d "%~dp0"
python t2_upgrade_entry.py %*
echo.
pause
endlocal
