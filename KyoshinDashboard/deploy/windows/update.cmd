@echo off
setlocal EnableExtensions
REM ==========================================================================
REM  Kyoshin - cap nhat len ban moi (double-click; tu xin quyen Administrator)
REM
REM     update.cmd                 tai ban moi nhat tu GitHub va cai (can Internet)
REM     update.cmd check           chi xem co ban moi khong
REM     update.cmd force           cai lai ban moi nhat ke ca khi dang dung ban do
REM     update.cmd <file.zip>      cai tu file Kyoshin-win-x64.zip, khong can Internet
REM                                (keo-tha file zip vao update.cmd cung duoc)
REM     update.cmd <file.zip> force   cai ca khi goi cu hon (quay ve ban cu)
REM
REM  GIU NGUYEN du lieu (Kyoshin.db), hang doi (agent.db) va cau hinh (appsettings.json).
REM  Truoc khi cai co sao luu vao backup\; ban moi khong chay duoc thi tu tra lai ban cu.
REM  Nhat ky: update.log trong thu muc cai dat.
REM ==========================================================================

REM Thu muc da cai: noi chua script nay neu ben canh co KyoshinServer, neu khong thi C:\Kyoshin
set "KY_ROOT=%~dp0"
set "KY_ROOT=%KY_ROOT:~0,-1%"
if not exist "%KY_ROOT%\KyoshinServer\KyoshinDashboard.exe" set "KY_ROOT=C:\Kyoshin"

set "KY_WORD="
set "KY_ZIP="
if /i "%~1"=="check" set "KY_WORD=check"
if /i "%~1"=="force" set "KY_WORD=force"
if not "%~1"=="" if not defined KY_WORD set "KY_ZIP=%~f1"
if defined KY_ZIP if /i "%~2"=="force" set "KY_WORD=force"
if defined KY_ZIP if /i "%~2"=="check" set "KY_WORD=check"

net session >nul 2>&1 || goto :elevate

set "KY_ARGS="
if /i "%KY_WORD%"=="check" set "KY_ARGS=-CheckOnly"
if /i "%KY_WORD%"=="force" set "KY_ARGS=-Force"
if defined KY_ZIP set "KY_ARGS=%KY_ARGS% -Zip "%KY_ZIP%""

REM The update replaces update.cmd and kyoshin-update.ps1: run a temp copy of the script, and keep
REM everything after it on the same line (cmd reads a .cmd file line by line while it runs)
set "KY_TMP=%TEMP%\kyoshin-update-%RANDOM%%RANDOM%.ps1"
copy /y "%~dp0kyoshin-update.ps1" "%KY_TMP%" >nul 2>&1 || goto :no_script
powershell -NoProfile -ExecutionPolicy Bypass -File "%KY_TMP%" -Root "%KY_ROOT%" %KY_ARGS% & del "%KY_TMP%" >nul 2>&1 & echo. & pause & exit /b

REM Chay lai chinh script nay voi quyen Administrator (hop thoai UAC); tham so qua bien moi truong
:elevate
set "KY_SELF=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$q = [char]34; $a = @(); if ($env:KY_ZIP) { $a += $q + $env:KY_ZIP + $q }; if ($env:KY_WORD) { $a += $env:KY_WORD }; if ($a.Count) { Start-Process -FilePath $env:KY_SELF -ArgumentList $a -Verb RunAs } else { Start-Process -FilePath $env:KY_SELF -Verb RunAs }"
if errorlevel 1 (
    echo [LOI] Can quyen Administrator: chuot phai update.cmd ^> Run as administrator
    echo.
    pause
)
exit /b

:no_script
echo [LOI] Khong thay kyoshin-update.ps1 ben canh update.cmd
echo.
pause
exit /b 1
