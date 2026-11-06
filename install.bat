@echo off
chcp 65001 >nul
cd /d "%~dp0"
node scripts\install.mjs
if %ERRORLEVEL% NEQ 0 (
  echo.
  echo [HATA] Kurulum sirasinda bir hata olustu.
  pause
  exit /b %ERRORLEVEL%
)
echo.
pause
