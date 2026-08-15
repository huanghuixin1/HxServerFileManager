@echo off
rem ===========================================================================
rem  HxServerFileManager 构建启动器（Windows 双击用）
rem  调用 build.sh 并弹出目标选择菜单（A/B/C/D/E/F），完成后停留窗口便于查看。
rem ===========================================================================
setlocal
chcp 65001 >nul

rem 探测 bash（PATH 或 Git Bash 常见安装位置）
set "BASH=%~dp0bash"
where bash >nul 2>nul && set "BASH=bash"
if not exist "%BASH%" set "BASH=C:\Program Files\Git\bin\bash.exe"
if not exist "%BASH%" set "BASH=C:\Program Files\Git\usr\bin\bash.exe"
if not exist "%BASH%" (
  echo [错误] 未找到 bash，请先安装 Git Bash。
  pause
  exit /b 1
)

cd /d "%~dp0"
"%BASH%" ./build.sh
set "RC=%ERRORLEVEL%"

echo.
echo 构建脚本执行完毕（退出码 %RC%），按任意键关闭窗口。
pause >nul
exit /b %RC%
