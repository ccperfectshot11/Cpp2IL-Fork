#!/usr/bin/env bash
set -u
R=/c/Users/Helper/Desktop/Cpp2IL-Fork
export DOTNET_gcServer=0
echo "=== opresc serverul ==="
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -like '*AssetRipper.GUI.Free*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }" 2>/dev/null
sleep 3
echo "=== recompilez AssetRipper ==="
cd "$R/helpers/AssetRipper" && dotnet build Source/AssetRipper.GUI.Free/AssetRipper.GUI.Free.csproj -c Release -v q --nologo 2>&1 | grep -Ei ": error|Build succeeded" | head -4
echo "=== pornesc serverul ==="
cd "$R/helpers/AssetRipper/Source/0Bins/AssetRipper.GUI.Free/Release" && nohup dotnet AssetRipper.GUI.Free.dll --port 7788 --headless > "$R/assetripper.log" 2>&1 &
for i in $(seq 1 30); do sleep 2; curl -s -m 3 -o /dev/null http://127.0.0.1:7788/ 2>/dev/null && break; done
echo "  server=$(curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1:7788/ 2>/dev/null)"
echo "=== reexport ==="
bash "$R/reexport.sh" 2>&1 | tail -6
echo "=== verificare ==="
bash "$R/unity-check/check.sh" 2>&1 | tail -12
echo "=== LANT COMPLET GATA ==="
