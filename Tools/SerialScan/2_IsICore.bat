@echo off
cd /d "%~dp0"
echo.
echo  Is the device an iCore strobe? Read-only check.
echo    0x200 = Slave Address  (iCore returns its own unit id)
echo    0x300 = Operation      (0=OFF 1=Continuous 2=Pulse)
echo.
set /p PORT=Port (e.g. COM10) :
set /p UNIT=UnitId (e.g. 3)   :
set /p BAUD=Baud (e.g. 115200):
echo.
echo === 0x200 Slave Address ===
SerialScan.exe --ports %PORT% --bauds %BAUD% --units %UNIT% --addr 0x200 --no-pause
echo.
echo === 0x300 Operation ===
SerialScan.exe --ports %PORT% --bauds %BAUD% --units %UNIT% --addr 0x300 --no-pause
echo.
echo  Both return a value  -^> iCore confirmed.
echo  Modbus exception     -^> not iCore (may be the DMD).
echo.
pause
