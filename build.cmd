@echo off
setlocal
cd /d "%~dp0"
set TARGET=%~1
if /i "%TARGET%"=="clean" (rmdir /s /q bin 2>nul & rmdir /s /q obj 2>nul & echo Cleaned. & exit /b 0)
rem SDK check. A per-user install (%LOCALAPPDATA%\Microsoft\dotnet) is often not on PATH, and a machine-wide dotnet.exe without an SDK may come first.
call :havesdk && goto sdkok
set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
call :havesdk && goto sdkok
echo ERROR: .NET 10 SDK not installed. Install it with: winget install Microsoft.DotNet.SDK.10
echo        or per-user: powershell -c "iwr https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1; .\dotnet-install.ps1 -Channel 10.0"
exit /b 1
:sdkok
rem A per-user runtime is not registered machine-wide, so the apphost (bin\Cadence.exe --selftest) needs DOTNET_ROOT to find it.
if not defined DOTNET_ROOT for /f "delims=" %%D in ('where dotnet') do if not defined DOTNET_ROOT if /i "%%~dpD"=="%LOCALAPPDATA%\Microsoft\dotnet\" set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
if not exist obj mkdir obj
powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-layering.ps1 || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\mkver.ps1 obj\Version.g.props || exit /b 1
if not exist cadence.ico powershell -NoProfile -ExecutionPolicy Bypass -File tools\mkico.ps1 cadence.ico || exit /b 1
dotnet build Cadence.slnx -c Release -warnaserror -nologo -v:minimal || exit /b 1
if /i "%TARGET%"=="integration" goto integration
dotnet run --project tests\Cadence.Tests -c Release --no-build || exit /b 1
dotnet run --project tests\Cadence.App.Tests -c Release --no-build || exit /b 1
dotnet publish src\Cadence.App -c Release -nologo -v:minimal -o bin || exit /b 1
if /i "%TARGET%"=="standalone" (dotnet publish src\Cadence.App -c Release -nologo -v:minimal -p:SelfContained=true -o bin\standalone || exit /b 1)
powershell -NoProfile -Command "$p = Start-Process bin\Cadence.exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow -RedirectStandardOutput obj\selftest.txt; Get-Content obj\selftest.txt; exit $p.ExitCode" || exit /b 1
rem [CP-E] The self-contained exe must run with no .NET in sight: DOTNET_ROOT removed, and a missing runtime would end it at once (no dialog) instead of hanging.
if /i "%TARGET%"=="standalone" (powershell -NoProfile -Command "Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue; $env:DOTNET_DISABLE_GUI_ERRORS = '1'; $p = Start-Process bin\standalone\Cadence.exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow -RedirectStandardOutput obj\selftest-standalone.txt; Write-Host ('standalone ' + (Get-Content obj\selftest-standalone.txt | Select-Object -Last 1)); exit $p.ExitCode" || exit /b 1)
echo Built bin\Cadence.exe
exit /b 0

:integration
dotnet run --project tests\Cadence.IntegrationTests -c Release --no-build
set RC=%ERRORLEVEL%
powershell -NoProfile -ExecutionPolicy Bypass -File tools\clean-test-tasks.ps1
exit /b %RC%

:havesdk
where dotnet >nul 2>&1 || exit /b 1
dotnet --list-sdks 2>nul | findstr /b "10." >nul
exit /b
