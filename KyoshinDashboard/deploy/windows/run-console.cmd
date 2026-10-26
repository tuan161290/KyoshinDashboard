@echo off
setlocal EnableExtensions
REM ==========================================================================
REM  Kyoshin - CHAY THU bang cmd (truoc khi cai thanh Service)
REM
REM  Double-click file nay tren may Windows o xuong (khong can quyen Administrator):
REM   - Chon cong COM cua PLC (hoac mock = gia lap)
REM   - Tu tao khoa API va lien ket Agent voi Server tren chinh may nay
REM   - Mo Server (web + DB) trong mot cua so rieng, mo trinh duyet
REM   - Chay Agent (doc PLC) ngay trong cua so nay; xem log de biet PLC da doc duoc chua
REM  Dung: Ctrl+C o cua so nay, roi dong cua so "Kyoshin Server".
REM  Chay OK -> chuot phai install-service.cmd > Run as administrator
REM ==========================================================================

cd /d "%~dp0"
set "SERVER_DIR=%~dp0Server"
set "AGENT_DIR=%~dp0Agent"
set "HELPER=%~dp0kyoshin-config.ps1"
if not exist "%SERVER_DIR%\KyoshinDashboard.exe" goto :no_package
if not exist "%AGENT_DIR%\KyoshinAgent.exe" goto :no_package

echo Dang kiem tra, vui long doi vai giay...

REM --- 1. Ban Service hoac ban cmd khac dang chay? (trung cong web / cong COM) ---
sc query KyoshinServer 2>nul | findstr "RUNNING" >nul && goto :service_running
sc query KyoshinAgent 2>nul | findstr "RUNNING" >nul && goto :service_running
tasklist /fi "imagename eq KyoshinDashboard.exe" 2>nul | findstr /i "KyoshinDashboard.exe" >nul && goto :already_running
tasklist /fi "imagename eq KyoshinAgent.exe" 2>nul | findstr /i "KyoshinAgent.exe" >nul && goto :already_running

REM --- 2. Doc het thong tin TRUOC khi hoi (powershell dang chay co the "nuot" phim go truoc) ---
call :read_config
if defined ERROR goto :config_failed
if defined PORT_BUSY goto :port_busy

echo.
echo ==========================================================================
echo  KYOSHIN - CHAY THU
echo ==========================================================================
echo  Che do hien tai   : %MODE%
echo  Cong COM tren may : %COMPORTS%
echo  Web dashboard     : cong %PORT%
echo.

REM --- 3. Hoi (Enter = giu nguyen) ---
set "CHOICE="
set /p "CHOICE= Cong COM cua PLC (vd COM5), mock = gia lap, Enter = giu nguyen: "

REM --- 4. Luu cong COM + lien ket Agent voi Server (khoa API chung) ---
set "ERROR="
set "KY_COM=%CHOICE%"
for /f "usebackq tokens=1,* delims==" %%a in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HELPER%" -Action Prepare -Server "%SERVER_DIR%" -Agent "%AGENT_DIR%"`) do set "%%a=%%b"
if defined ERROR goto :config_failed
call :read_config
if defined ERROR goto :config_failed

REM --- 5. Server trong cua so rieng ---
echo.
echo  Dang mo Server (cua so "Kyoshin Server")...
start "Kyoshin Server - dong cua so nay de dung web" /d "%SERVER_DIR%" "%SERVER_DIR%\KyoshinDashboard.exe"
set "READY="
for /f "usebackq tokens=1,* delims==" %%a in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HELPER%" -Action WaitServer -Server "%SERVER_DIR%" -Agent "%AGENT_DIR%"`) do set "%%a=%%b"
if /i not "%READY%"=="True" goto :server_failed
start "" "http://localhost:%PORT%"

REM --- 6. Agent trong cua so nay ---
echo.
echo ==========================================================================
echo  Web dashboard tren may nay : http://localhost:%PORT%
echo  May khac trong xuong       : %LAN_URLS%
echo                               (chi mo duoc sau khi chay install-service.cmd - mo firewall)
echo  Agent                      : %MODE%
echo.
echo  Xem log Agent ben duoi:
echo     "Connected to FX PLC line on COMx"   = da mo cong COM cua PLC
echo     "station 00 is responding"           = doc duoc PLC station 00
echo     "station 00 read failed - ..."       = PLC tra loi loi: xem ma loi trong log
echo        ma 02h = doi "SumCheck" ^(true/false^); ma 03h/06h = thu "Command": "QR"
echo     "Failed to open FX PLC line"         = sai cong COM / chua cam bo USB-RS485
echo     "Connected to server"                = Agent da gui duoc du lieu cho Server
echo  Nhan Ctrl+C de dung Agent.
echo ==========================================================================
"%AGENT_DIR%\KyoshinAgent.exe"
echo.
echo  Agent da dung. Nho dong ca cua so "Kyoshin Server".
echo  Neu chay OK: chuot phai install-service.cmd ^> Run as administrator de cai thanh Service
echo  (cong COM va khoa API vua dung duoc giu nguyen khi cai).
goto :end

REM Doc cau hinh hien tai vao PORT, MODE, COMPORTS, PORT_BUSY, LAN_URLS (ERROR neu loi)
:read_config
set "ERROR="
set "PORT_BUSY="
set "LAN_URLS="
for /f "usebackq tokens=1,* delims==" %%a in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HELPER%" -Action Show -Server "%SERVER_DIR%" -Agent "%AGENT_DIR%"`) do set "%%a=%%b"
if not defined LAN_URLS set "LAN_URLS=(khong tim thay dia chi mang)"
exit /b

:no_package
echo [LOI] Khong thay Server\KyoshinDashboard.exe va Agent\KyoshinAgent.exe ben canh script.
echo       Hay chay script nay tu ben trong thu muc goi Kyoshin (tao bang publish.cmd).
goto :end

:service_running
echo [!] Service KyoshinServer / KyoshinAgent da duoc cai va dang chay - khong chay them ban cmd
echo     (trung cong web va cong COM). Muon chay thu thi dung service truoc (cmd quyen Administrator):
echo        net stop KyoshinAgent
echo        net stop KyoshinServer
goto :end

:already_running
echo [!] KyoshinDashboard.exe hoac KyoshinAgent.exe dang chay o cua so khac.
echo     Dong cac cua so do (Ctrl+C) roi chay lai.
goto :end

:port_busy
echo [LOI] Cong %PORT% dang bi chuong trinh "%PORT_BUSY%" chiem.
echo       Tat chuong trinh do, hoac doi cong trong Server\appsettings.json (Kestrel - Url).
goto :end

:config_failed
echo [LOI] Khong doc/luu duoc cau hinh: %ERROR%
goto :end

:server_failed
echo [LOI] Server khong khoi dong duoc sau 30 giay - xem loi trong cua so "Kyoshin Server".
goto :end

:end
echo.
pause
endlocal
