@echo off
REM ==================================================================
REM  Cpp2IL measurement run - ALWAYS use this, never call the exe bare.
REM  Omitting --use-processor silently drops to ~77.7 pct / ~134k markers
REM  (nativemethoddetector off = native calls become "Method not found";
REM   attributeinjector/stablenamer off = 2,411 fewer methods).
REM  Correct baseline: 105,675 methods, 85,107 clean (80.54 pct), 102,632 markers,
REM  MNF 13,354 over 576 targets.
REM
REM  Usage:  run.cmd [outputDir] [extra BodyScan args...]
REM     e.g. run.cmd C:\Users\Helper\Desktop\CPP2IL\cpp2il_out_correct --save bodyscan-runs\v21.json
REM ==================================================================
setlocal
set "GAME=C:\Users\Helper\Desktop\StumbleCorp\StgCorpV1\StgCorp"
set "CPP2IL=C:\Users\Helper\Desktop\CPP2IL\Cpp2IL\Cpp2IL\bin\Release\net10.0\Cpp2IL.exe"
set "BODYSCAN=C:\Users\Helper\Desktop\CPP2IL\Cpp2IL\Cpp2IL.BodyScan\bin\Release\net10.0\Cpp2IL.BodyScan.exe"
set "PROCESSORS=attributeanalyzer,attributeinjector,callanalyzer,nativemethoddetector,stablenamer"

set "OUT=%~1"
if "%OUT%"=="" set "OUT=C:\Users\Helper\Desktop\CPP2IL\cpp2il_out_correct"

echo [run.cmd] Cpp2IL -^> %OUT%
"%CPP2IL%" --game-path "%GAME%" --use-processor %PROCESSORS% --output-as dll_il_recovery --output-to "%OUT%"
if errorlevel 1 ( echo [run.cmd] Cpp2IL FAILED & exit /b 1 )

echo.
echo [run.cmd] BodyScan %OUT%
"%BODYSCAN%" "%OUT%" %2 %3 %4 %5 %6 %7
endlocal
