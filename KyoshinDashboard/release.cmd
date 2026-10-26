@echo off
setlocal EnableExtensions
REM ==========================================================================
REM  Kyoshin - phat hanh ban moi len GitHub (chay tren MAY DEV)
REM
REM     release.cmd 1.2.0
REM
REM  Day code len GitHub, gan tag v1.2.0 cho commit hien tai va day tag.
REM  GitHub Actions (.github\workflows\release.yml) build goi Kyoshin-win-x64.zip
REM  va tao Release (khoang 3-5 phut). May o xuong cai bang C:\Kyoshin\update.cmd.
REM  Can commit het thay doi truoc (git status sach).
REM ==========================================================================

set "VER=%~1"
if "%VER%"=="" goto :usage
echo %VER%| findstr /r "^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul || goto :usage

cd /d "%~dp0.." || goto :end
for /f "delims=" %%s in ('git status --porcelain') do goto :dirty
git fetch --tags --quiet
git rev-parse -q --verify "refs/tags/v%VER%" >nul && goto :exists

echo Day code len GitHub ...
git push origin HEAD || goto :push_failed
git tag -a "v%VER%" -m "Kyoshin %VER%" || goto :end
git push origin "v%VER%" || goto :push_failed

echo.
echo ==========================================================================
echo  Da gan tag v%VER%. GitHub dang build goi cai dat (3-5 phut):
for /f "delims=" %%u in ('git remote get-url origin') do set "URL=%%u"
set "URL=%URL:.git=%"
echo     %URL%/actions
echo  Xong thi ban moi o: %URL%/releases/latest
echo  O xuong: chuot phai C:\Kyoshin\update.cmd ^> Run as administrator
echo ==========================================================================
goto :end

:usage
echo Cach dung: release.cmd ^<phien ban^>     vi du: release.cmd 1.2.0
echo Cac tag da co:
git -C "%~dp0.." tag --sort=-v:refname 2>nul
goto :end

:dirty
echo [LOI] Con thay doi chua commit (git status). Commit truoc roi chay lai.
git status --short
goto :end

:exists
echo [LOI] Tag v%VER% da co. Chon so phien ban lon hon.
goto :end

:push_failed
echo [LOI] Khong day len GitHub duoc. Kiem tra mang / dang nhap (gh auth status).
goto :end

:end
endlocal
