@echo off
rem Regenere publish\Aion2Builder.exe (un seul fichier, a lancer par double-clic).
rem Ferme l'application avant : l'exe en cours d'utilisation ne peut pas etre remplace.
cd /d "%~dp0"
dotnet publish src\Aion2Builder.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o publish
del /q publish\*.pdb 2>nul
echo.
echo Executable : %~dp0publish\Aion2Builder.exe
pause
