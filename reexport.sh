#!/usr/bin/env bash
# Reexporta proiectul cu Level3 - codul recuperat, nu cioturi.
set -u
A=http://127.0.0.1:7788
curl -s -m 60 -X POST "$A/Settings/Update" -d "ScriptContentLevel=Level3" -o /dev/null
echo "=== incarc jocul ==="
curl -s -m 2400 -X POST "$A/LoadFolder" --data-urlencode "Path=C:\Users\Helper\Desktop\StumblePeak" -o /dev/null -w "  load=%{http_code}\n"
grep -i "ScriptContentLevel" /c/Users/Helper/Desktop/Cpp2IL-Fork/assetripper.log | tail -1
echo "=== export ==="
rm -rf /c/Users/Helper/Desktop/StumblePeakUnity 2>/dev/null
curl -s -m 5400 -X POST "$A/Export/UnityProject" --data-urlencode "Path=C:\Users\Helper\Desktop\StumblePeakUnity" -o /dev/null -w "  export=%{http_code}\n"
tail -3 /c/Users/Helper/Desktop/Cpp2IL-Fork/assetripper.log
echo "=== REEXPORT GATA ==="
