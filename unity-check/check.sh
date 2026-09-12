#!/usr/bin/env bash
# Compileaza scripturile generate de AssetRipper cu Roslyn, FARA limita de erori.
# Unity se opreste dupa cateva sute si numai pe assembly-urile pe care le incarca in Safe Mode;
# aici iese lista completa, si se poate reface dupa fiecare reparatie fara sa deschizi editorul.
set -u
U="C:/Users/Helper/Desktop/StumblePeakUnity/ExportedProject"
E="C:/Program Files/Unity/Hub/Editor/2021.3.25f1/Editor/Data/Managed/UnityEngine"
OUT="$(dirname "$0")"

cat > "$OUT/check.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NoWarn>$(NoWarn);CS0108;CS0114;CS0465;CS0169;CS0649;CS0162</NoWarn>
    <ErrorReportLimit>0</ErrorReportLimit>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="UNITYPROJ/Assets/Scripts/**/*.cs" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="UNITYREFS/*.dll" />
    <Reference Include="UNITYPROJ/Assets/Plugins/*.dll" />
    <Reference Include="C:/Users/Helper/Desktop/StumblePeakUnity/ExportedProject/Library/PackageCache/com.unity.nuget.newtonsoft-json@3.2.0/Runtime/Newtonsoft.Json.dll" />
  </ItemGroup>
</Project>
CSPROJ
sed -i "s|UNITYPROJ|$U|g; s|UNITYREFS|$E|g" "$OUT/check.csproj"

export DOTNET_gcServer=0
dotnet build "$OUT/check.csproj" -c Release -v n --nologo 2>&1 | tee "$OUT/roslyn.log" > /dev/null || true
# Fiecare diagnostic apare de doua ori in log (o data cu prefix "1>", o data indentat),
# deci se numara pe (fisier, linie, cod) unic, nu pe linii.
grep -oE "[^ >]+\.cs\([0-9]+,[0-9]+\): error CS[0-9]+" "$OUT/roslyn.log" | sort -u | wc -l
echo "=== familii de erori ==="
grep -oE "[^ >]+\.cs\([0-9]+,[0-9]+\): error CS[0-9]+" "$OUT/roslyn.log" | sort -u | grep -oE "CS[0-9]+$" | sort | uniq -c | sort -rn | head -20
echo "=== GATA ==="
