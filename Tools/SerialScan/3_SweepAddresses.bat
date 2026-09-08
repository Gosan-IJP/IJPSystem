@echo off
cd /d "%~dp0"
echo.
echo  Find which register addresses are readable on one device.
echo  Read-only. Nothing is written.
echo.
echo  Close IJPSystem app first - it holds the port.
echo.
set "PORT=COM10"
set "BAUD=115200"
set "UNIT=3"
set "RANGE=0-1000"
set /p PORT=Port   [%PORT%]  :
set /p BAUD=Baud   [%BAUD%] :
set /p UNIT=UnitId [%UNIT%]      :
set /p RANGE=Range  [%RANGE%] :
echo.
echo  -^> %PORT% @ %BAUD% UnitId %UNIT%  addresses %RANGE%
echo.
SerialScan.exe --ports %PORT% --bauds %BAUD% --units %UNIT% --sweep %RANGE%
