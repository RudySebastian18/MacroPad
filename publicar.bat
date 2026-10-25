@echo off
setlocal
cd /d "%~dp0"

rem Uso:
rem   publicar.bat           -> MacroPad.exe autocontenido (no necesita .NET instalado, pesa mas)
rem   publicar.bat ligero    -> MacroPad.exe ligero (necesita el .NET Desktop Runtime instalado)

set PROJECT=MacroPad\MacroPad.csproj

if /I "%~1"=="ligero" goto ligero

echo.
echo === Publicando MacroPad autocontenido ===
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "publish\win-x64"
if errorlevel 1 goto error
echo.
echo Listo: publish\win-x64\MacroPad.exe
goto fin

:ligero
echo.
echo === Publicando MacroPad ligero ===
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "publish\ligero"
if errorlevel 1 goto error
echo.
echo Listo: publish\ligero\MacroPad.exe
goto fin

:error
echo.
echo ERROR: la publicacion fallo. Revisa los mensajes de arriba.
exit /b 1

:fin
endlocal
