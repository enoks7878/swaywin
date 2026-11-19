@echo off
dotnet run --project "%~dp0tests\SwayWin.Tests\SwayWin.Tests.csproj" --configuration Release
if errorlevel 1 exit /b 1
dotnet publish "%~dp0src\SwayWin.App\SwayWin.App.csproj" --configuration Release --runtime win-x64 --self-contained false --output "%~dp0dist"
exit /b %errorlevel%
