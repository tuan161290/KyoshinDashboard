@echo off
setlocal EnableExtensions
REM ==========================================================================
REM  Kyoshin - cai dat / cap nhat 2 Windows Service tren may o xuong
REM     KyoshinServer : web dashboard + CSDL (cong 5000, ca mang LAN xem duoc)
REM     KyoshinAgent  : doc PLC Mitsubishi FX, gui ve KyoshinServer tren chinh may nay
REM
REM  Chay thu bang run-console.cmd truoc (chon COM); OK roi moi cai.
REM  Chuot phai > Run as administrator, ngay trong thu muc goi Kyoshin:
REM     install-service.cmd              cai moi, hoac cap nhat len ban moi
REM     install-service.cmd uninstall    go 2 service + luat firewall (giu nguyen file va du lieu)
REM
REM  - Cai vao C:\Kyoshin\KyoshinServer va C:\Kyoshin\KyoshinAgent (doi INSTALL_ROOT neu can)
REM  - Khi cap nhat: GIU NGUYEN Kyoshin.db (du lieu), agent.db (hang doi) va cau hinh dang dung
REM  - Chep them update.cmd vao C:\Kyoshin: cac lan sau chi can chay C:\Kyoshin\update.cmd
REM  - Mo cong web tren Windows Firewall de may khac trong xuong xem dashboard
REM  - Service tu chay khi bat may (khong can dang nhap) va tu khoi dong lai neu loi
REM ==========================================================================

set "INSTALL_ROOT=C:\Kyoshin"
set "SERVER_INST=%INSTALL_ROOT%\KyoshinServer"
set "AGENT_INST=%INSTALL_ROOT%\KyoshinAgent"
set "SRC=%~dp0"
set "SRC=%SRC:~0,-1%"
set "HELPER=%SRC%\kyoshin-config.ps1"
set "FW_RULE=Kyoshin Dashboard"

net session >nul 2>&1 || goto :need_admin
if /i "%~1"=="uninstall" goto :uninstall
if not exist "%SRC%\Server\KyoshinDashboard.exe" goto :no_package
if not exist "%SRC%\Agent\KyoshinAgent.exe" goto :no_package

REM --- 1. Dung service cu (neu co) de giai phong file, cong web va cong COM ---
echo [1/5] Dung service dang chay (neu co)...
net stop KyoshinAgent >nul 2>&1
net stop KyoshinServer >nul 2>&1
ping -n 3 127.0.0.1 >nul
tasklist /fi "imagename eq KyoshinDashboard.exe" 2>nul | findstr /i "KyoshinDashboard.exe" >nul && goto :console_running
tasklist /fi "imagename eq KyoshinAgent.exe" 2>nul | findstr /i "KyoshinAgent.exe" >nul && goto :console_running

REM --- 2. Lay cau hinh nao: goi vua chay thu (run-console.cmd) hay ban dang cai? ---
call :helper Configured "%SRC%\Server" "%SRC%\Agent"
if defined ERROR goto :config_failed
set "PKG_CONFIGURED=%CONFIGURED%"
call :helper Configured "%SERVER_INST%" "%AGENT_INST%"
set "INST_CONFIGURED=%CONFIGURED%"

set "KEEP_SERVER=Kyoshin.db Kyoshin.db-shm Kyoshin.db-wal"
set "KEEP_AGENT=agent.db agent.db-shm agent.db-wal"
set "CFG_FROM=goi vua chay thu (run-console.cmd)"
if /i "%PKG_CONFIGURED%"=="True" goto :copy
if /i not "%INST_CONFIGURED%"=="True" goto :not_configured
set "CFG_FROM=ban dang cai tren may"
set "KEEP_AGENT=%KEEP_AGENT% appsettings.json"
if exist "%SERVER_INST%\appsettings.json" set "KEEP_SERVER=%KEEP_SERVER% appsettings.json"

REM --- 3. Copy file moi (giu du lieu + hang doi) ---
:copy
echo [2/5] Copy file vao %INSTALL_ROOT% ... (cau hinh: %CFG_FROM%)
robocopy "%SRC%\Server" "%SERVER_INST%" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP /XF %KEEP_SERVER% >nul
if errorlevel 8 goto :copy_failed
robocopy "%SRC%\Agent" "%AGENT_INST%" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP /XF %KEEP_AGENT% >nul
if errorlevel 8 goto :copy_failed
REM Cong cu cap nhat (update.cmd) va so phien ban dang cai
for %%f in (update.cmd kyoshin-update.ps1 kyoshin-config.ps1 version.txt) do if exist "%SRC%\%%f" copy /y "%SRC%\%%f" "%INSTALL_ROOT%\" >nul
set "KY_VERSION=khong ro"
if exist "%SRC%\version.txt" set /p KY_VERSION=<"%SRC%\version.txt"

REM --- 4. Lien ket Agent voi Server (khoa API chung, gui ve 127.0.0.1) + mo firewall ---
call :helper Link "%SERVER_INST%" "%AGENT_INST%"
if defined ERROR goto :config_failed
call :helper Show "%SERVER_INST%" "%AGENT_INST%"
if defined ERROR goto :config_failed
if defined PORT_BUSY goto :port_busy
echo [3/5] Mo cong %PORT% tren Windows Firewall cho mang LAN...
netsh advfirewall firewall delete rule name="%FW_RULE%" >nul 2>&1
netsh advfirewall firewall add rule name="%FW_RULE%" dir=in action=allow protocol=TCP localport=%PORT% profile=any >nul
if errorlevel 1 echo     [!] Khong mo duoc firewall - may khac se khong vao duoc dashboard

REM --- 5. Tao / cap nhat 2 service, khoi dong Server truoc roi Agent ---
echo [4/5] Cau hinh service...
call :ensure_service KyoshinServer "Kyoshin Server" "%SERVER_INST%\KyoshinDashboard.exe" "Kyoshin: web dashboard va CSDL 48 may, cong %PORT%"
if errorlevel 1 goto :sc_failed
call :ensure_service KyoshinAgent "Kyoshin Agent" "%AGENT_INST%\KyoshinAgent.exe" "Kyoshin: doc PLC Mitsubishi FX, gui ve KyoshinServer tren may nay"
if errorlevel 1 goto :sc_failed

echo [5/5] Khoi dong service...
net start KyoshinServer >nul 2>&1
call :helper WaitServer "%SERVER_INST%" "%AGENT_INST%"
if /i not "%READY%"=="True" goto :server_failed
net start KyoshinAgent >nul 2>&1
ping -n 4 127.0.0.1 >nul
sc query KyoshinAgent | findstr "RUNNING" >nul || goto :agent_failed

echo.
echo ==========================================================================
echo  XONG! 2 service dang chay. Phien ban %KY_VERSION%
echo.
echo  Dashboard tren may nay : http://localhost:%PORT%
echo  May khac trong xuong   : %LAN_URLS%
echo  Agent                  : %MODE%
echo  Ghi loi                : %ERRORS%
echo.
echo  Du lieu  : %SERVER_INST%\Kyoshin.db
echo  Hang doi : %AGENT_INST%\agent.db  (du lieu chua gui khi Server tam dung)
echo  Cau hinh : %AGENT_INST%\appsettings.json  (sua xong chay lai script nay)
echo  Log      : Event Viewer ^> Windows Logs ^> Application, nguon "KyoshinDashboard" / "KyoshinAgent"
echo  Cap nhat : %INSTALL_ROOT%\update.cmd  (tai ban moi tu GitHub, hoac keo-tha file zip vao)
echo ==========================================================================
goto :end

REM Goi kyoshin-config.ps1: %1 = Action, %2 = thu muc Server, %3 = thu muc Agent; ket qua vao bien KEY=VALUE
:helper
set "ERROR="
set "PORT_BUSY="
set "READY="
set "CONFIGURED="
for /f "usebackq tokens=1,* delims==" %%a in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%HELPER%" -Action %~1 -Server "%~2" -Agent "%~3"`) do set "%%a=%%b"
exit /b

REM Tao service %1 (ten hien thi %2, file chay %3, mo ta %4), hoac cap nhat neu da co
:ensure_service
sc query "%~1" >nul 2>&1
if errorlevel 1 goto :ensure_create
sc config "%~1" binPath= "\"%~3\"" start= delayed-auto DisplayName= "%~2" >nul
goto :ensure_options
:ensure_create
sc create "%~1" binPath= "\"%~3\"" start= delayed-auto DisplayName= "%~2" >nul
:ensure_options
if errorlevel 1 exit /b 1
sc description "%~1" "%~4" >nul
sc failure "%~1" reset= 86400 actions= restart/5000/restart/5000/restart/5000 >nul
exit /b 0

:uninstall
echo Go service KyoshinAgent va KyoshinServer ...
net stop KyoshinAgent >nul 2>&1
net stop KyoshinServer >nul 2>&1
sc delete KyoshinAgent >nul 2>&1
sc delete KyoshinServer >nul 2>&1
netsh advfirewall firewall delete rule name="%FW_RULE%" >nul 2>&1
echo Da go 2 service va luat firewall. File va du lieu van con trong %INSTALL_ROOT%
goto :end

:need_admin
echo [LOI] Can quyen Administrator: chuot phai install-service.cmd ^> Run as administrator
goto :end

:no_package
echo [LOI] Khong thay Server\KyoshinDashboard.exe va Agent\KyoshinAgent.exe ben canh script.
echo       Hay chay script nay tu ben trong thu muc goi Kyoshin (tao bang publish.cmd).
goto :end

:console_running
echo [LOI] KyoshinDashboard.exe / KyoshinAgent.exe dang chay (cua so run-console.cmd?).
echo       Dong cac cua so do (Ctrl+C) roi chay lai.
goto :end

:not_configured
echo [LOI] Chua chon cong COM cho PLC.
echo       Chay run-console.cmd truoc de chon cong COM va chay thu, roi chay lai script nay.
goto :end

:config_failed
echo [LOI] Khong doc/luu duoc cau hinh: %ERROR%
goto :end

:copy_failed
echo [LOI] Copy file that bai. Kiem tra quyen ghi vao %INSTALL_ROOT%
goto :end

:port_busy
echo [LOI] Cong %PORT% dang bi chuong trinh "%PORT_BUSY%" chiem.
echo       Tat chuong trinh do, hoac doi cong trong %SERVER_INST%\appsettings.json (Kestrel - Url), roi chay lai.
goto :end

:sc_failed
echo [LOI] Khong tao duoc service.
goto :end

:server_failed
echo [LOI] Service KyoshinServer khong khoi dong duoc. Xem Event Viewer ^> Windows Logs ^> Application
sc query KyoshinServer
goto :end

:agent_failed
echo [LOI] Service KyoshinAgent khong khoi dong duoc. Xem Event Viewer ^> Windows Logs ^> Application
sc query KyoshinAgent
goto :end

:end
echo.
pause
endlocal
