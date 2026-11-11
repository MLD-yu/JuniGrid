@echo off
rem 双击运行调试版 JuniGrid（Agent 启动的窗口会随命令结束被回收，所以留这个给人工用）
set "EXE=%LOCALAPPDATA%\JuniGridBuild\Debug\net10.0-windows10.0.17763.0\JuniGrid.exe"
if not exist "%EXE%" (
    echo [x] 没找到调试版：%EXE%
    echo     先跑：dotnet build JuniGrid/JuniGrid.csproj -c Debug
    pause
    exit /b 1
)
rem 带参数 cdp 时开调试端口，Agent 才能连进去做真机验证（只监听 127.0.0.1）
if /i "%~1"=="cdp" (
    set "JUNIGRID_CDP_PORT=9333"
    echo [i] CDP 调试端口已开：127.0.0.1:9333
)
start "" "%EXE%"
exit /b 0
