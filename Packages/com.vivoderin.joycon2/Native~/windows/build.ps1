# Builds Joycon2Ble.dll (x64, Release) into the package (Runtime/Plugins/Windows/x86_64) and the WebGL rate helper
# Joycon2Boost.exe into WebHelpers~ (the WebGL build copies it next to index.html).
# Requires Visual Studio 2022 (Build Tools are enough) with the C++ workload and a Windows 10/11 SDK.
# Note: the Unity Editor locks the DLL once loaded — close the Editor before re-running this.
param([string]$Config = "Release")
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with C++ x64 tools not found" }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
$build = Join-Path $root "build"

$env:PATH += ";$(Split-Path $vswhere)"
cmd /c "`"$vcvars`" >nul && cmake -S `"$root`" -B `"$build`" -G Ninja -DCMAKE_BUILD_TYPE=$Config && cmake --build `"$build`""
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dest = Join-Path $root "..\..\Runtime\Plugins\Windows\x86_64"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $build "Joycon2Ble.dll") $dest -Force
Write-Host "Copied Joycon2Ble.dll -> $((Resolve-Path $dest).Path)"

$helpers = Join-Path $root "..\..\WebHelpers~"
New-Item -ItemType Directory -Force $helpers | Out-Null
Copy-Item (Join-Path $build "Joycon2Boost.exe") $helpers -Force
Write-Host "Copied Joycon2Boost.exe -> $((Resolve-Path $helpers).Path)"
