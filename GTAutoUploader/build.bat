@echo off
REM Usage: build.bat ["D:\path\to\Gorilla Tag"]
set "GTDIR=C:\Program Files (x86)\Steam\steamapps\common\Gorilla Tag"
if not "%~1"=="" set "GTDIR=%~1"
echo Game folder: %GTDIR%
dotnet build AutoUploader\AutoUploader.csproj -c Release /p:GTDir="%GTDIR%" || goto err
dotnet build MyMod\MyMod.csproj -c Release /p:GTDir="%GTDIR%" || goto err
echo.
echo DONE. AutoUploader -> BepInEx\plugins, MyMod -> BepInEx\autouploader
pause
exit /b 0
:err
echo.
echo BUILD FAILED - check GTDir path and that BepInEx + Utilla are installed.
pause
exit /b 1
