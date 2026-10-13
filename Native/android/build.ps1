param([string]$UnityAndroid = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Data\PlaybackEngines\AndroidPlayer')
$ErrorActionPreference = 'Stop'
$nativeRoot = $PSScriptRoot
$buildRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot 'build'))
$classesRoot = Join-Path $buildRoot 'classes'
$aarRoot = Join-Path $buildRoot 'aar'
$javac = Join-Path $UnityAndroid 'OpenJDK\bin\javac.exe'
$jar = Join-Path $UnityAndroid 'OpenJDK\bin\jar.exe'
$androidJar = Join-Path $UnityAndroid 'SDK\platforms\android-36\android.jar'
foreach ($required in @($javac, $jar, $androidJar)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing Android build tool: $required" }
}
# Remove only generated output, after checking the absolute target stays inside this native module.
if (!$buildRoot.StartsWith([IO.Path]::GetFullPath($nativeRoot) + [IO.Path]::DirectorySeparatorChar)) {
    throw 'Build path escaped Native/android'
}
if (Test-Path -LiteralPath $buildRoot) { Remove-Item -LiteralPath $buildRoot -Recurse -Force }
New-Item -ItemType Directory -Path $classesRoot, $aarRoot -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $nativeRoot 'src') -Filter '*.java' -Recurse | Select-Object -ExpandProperty FullName)
& $javac -source 8 -target 8 -Xlint:deprecation -Xlint:-options -classpath $androidJar -d $classesRoot @sources
if ($LASTEXITCODE -ne 0) { throw 'Android BLE Java compilation failed' }
$classesJar = Join-Path $aarRoot 'classes.jar'
& $jar cf $classesJar -C $classesRoot .
if ($LASTEXITCODE -ne 0) { throw 'classes.jar creation failed' }
Copy-Item -LiteralPath (Join-Path $nativeRoot 'AndroidManifest.xml') -Destination $aarRoot
Copy-Item -LiteralPath (Join-Path $nativeRoot 'consumer-rules.pro') -Destination (Join-Path $aarRoot 'proguard.txt')
$output = [IO.Path]::GetFullPath((Join-Path $nativeRoot '..\..\Assets\Joycon2\Plugins\Android\joycon2ble.aar'))
New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
# jar produces a ZIP-compatible AAR with a plain XML manifest; no Gradle/NDK dependency needed.
& $jar cf $output -C $aarRoot .
if ($LASTEXITCODE -ne 0) { throw 'AAR creation failed' }
Write-Output "Android BLE AAR: $output ($((Get-Item -LiteralPath $output).Length) bytes)"
