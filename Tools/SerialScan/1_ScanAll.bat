@echo off
cd /d "%~dp0"
echo.
echo  Wide scan: COM8/9/10 x 6 bauds x UnitId 1-16   (about 90 sec)
echo.
echo  Close IJPSystem app first - it holds the port.
echo.
pause
SerialScan.exe --ports COM8,COM9,COM10 --bauds 4800,9600,19200,38400,57600,115200 --units 1-16
