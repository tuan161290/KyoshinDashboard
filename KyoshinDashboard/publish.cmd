@echo off
setlocal EnableExtensions
REM ==========================================================================
REM  Kyoshin - tao goi cai dat cho may Windows o xuong (chay tren MAY DEV)
REM
REM  Cach dung:  publish.cmd [thu_muc_goc] [phien_ban]   (mac dinh D:\Deploy, 0.0.0-local)
REM  Ban phat hanh chinh thuc do GitHub Actions build khi gan tag: xem release.cmd va README.md
REM
REM   <goc>\Kyoshin\Server     web dashboard + CSDL      (service KyoshinServer)
REM   <goc>\Kyoshin\Agent      doc PLC, gui ve Server    (service KyoshinAgent)
REM   <goc>\Kyoshin\run-console.cmd, install-service.cmd, update.cmd, *.ps1, version.txt
REM  Ca hai self-contained: may xuong KHONG can cai .NET.
REM ==========================================================================

set "ROOT=%~1"
if "%ROOT%"=="" set "ROOT=D:\Deploy"
set "OUT=%ROOT%\Kyoshin"
set "VERSION=%~2"
if "%VERSION%"=="" set "VERSION=0.0.0-local"

where dotnet >nul 2>&1 || goto :no_dotnet

REM Xoa goi cu - chi khi thu muc do dung la goi cu (tranh xoa nham)
call :clean "%OUT%" install-service.cmd || exit /b 1

echo [1/2] Publish server (Windows x64, self-contained)...
dotnet publish "%~dp0KyoshinDashboard\KyoshinDashboard.csproj" -c Release -r win-x64 --self-contained true -p:Version=%VERSION% -o "%OUT%\Server" -nologo -v q
if errorlevel 1 goto :publish_failed
del /q "%OUT%\Server\appsettings.Development.json" 2>nul

echo [2/2] Publish agent (Windows x64, self-contained)...
dotnet publish "%~dp0KyoshinAgent\KyoshinAgent.csproj" -c Release -r win-x64 --self-contained true -p:Version=%VERSION% -o "%OUT%\Agent" -nologo -v q
if errorlevel 1 goto :publish_failed
del /q "%OUT%\Agent\appsettings.Development.json" 2>nul

copy /y "%~dp0deploy\windows\run-console.cmd" "%OUT%\" >nul
copy /y "%~dp0deploy\windows\install-service.cmd" "%OUT%\" >nul
copy /y "%~dp0deploy\windows\kyoshin-config.ps1" "%OUT%\" >nul
copy /y "%~dp0deploy\windows\update.cmd" "%OUT%\" >nul
copy /y "%~dp0deploy\windows\kyoshin-update.ps1" "%OUT%\" >nul
>"%OUT%\version.txt" echo %VERSION%

echo.
echo ==========================================================================
echo  XONG!  Goi cai dat: %OUT%  (phien ban %VERSION%)
echo.
echo   1. Copy ca thu muc Kyoshin sang may Windows o xuong
echo   2. Double-click run-console.cmd: chon cong COM, xem dashboard + log PLC
echo   3. Chay OK: chuot phai install-service.cmd ^> Run as administrator
echo      (cai 2 service KyoshinServer + KyoshinAgent, mo cong web cho mang LAN)
echo ==========================================================================
exit /b 0

REM Xoa thu muc %1 neu do la goi cu (co file %2); bao loi neu co file khac
:clean
if exist "%~1\%~2" (
    echo Xoa goi cu: %~1
    rmdir /s /q "%~1"
    if exist "%~1\%~2" (
        echo [LOI] Khong xoa duoc %~1 - co chuong trinh dang chay tu thu muc do? Tat di roi chay lai.
        exit /b 1
    )
    exit /b 0
)
dir /b /a "%~1" 2>nul | findstr "^" >nul || exit /b 0
echo [LOI] %~1 da co file nhung khong phai goi Kyoshin. Chon thu muc goc khac: publish.cmd E:\Deploy
exit /b 1

:no_dotnet
echo [LOI] Khong tim thay lenh dotnet. Can cai .NET 10 SDK tren may dev.
exit /b 1

:publish_failed
echo [LOI] Publish that bai - xem loi phia tren.
exit /b 1
