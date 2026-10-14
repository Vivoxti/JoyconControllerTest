param([string]$UnityAndroid = 'C:/Program Files/Unity/Hub/Editor/6000.3.18f1/Editor/Data/PlaybackEngines/AndroidPlayer')
$ErrorActionPreference = 'Stop'
$module = $PSScriptRoot
$output = Join-Path $module 'build'
$jdk = Join-Path $UnityAndroid 'OpenJDK/bin'
$sdk = Join-Path $UnityAndroid 'SDK'
$tools = Join-Path $sdk 'build-tools/36.0.0'
$platform = Join-Path $sdk 'platforms/android-36/android.jar'
New-Item -ItemType Directory -Path $output,(Join-Path $output 'classes'),(Join-Path $output 'dex') -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $module 'src') -Recurse -Filter '*.java' | Select-Object -ExpandProperty FullName)
& "$jdk/javac.exe" -source 8 -target 8 -Xlint:-options -classpath $platform -d "$output/classes" @sources
if ($LASTEXITCODE) { throw 'Booster Java compilation failed' }
& "$jdk/jar.exe" cf "$output/classes.jar" -C "$output/classes" .
if ($LASTEXITCODE) { throw 'Booster JAR failed' }
& "$jdk/java.exe" -cp "$tools/lib/d8.jar" com.android.tools.r8.D8 --lib $platform --min-api 26 --output "$output/dex" "$output/classes.jar"
if ($LASTEXITCODE) { throw 'Booster DEX failed' }
& "$tools/aapt.exe" package -f -M "$module/AndroidManifest.xml" -I $platform -F "$output/unsigned.apk"
if ($LASTEXITCODE) { throw 'Booster manifest packaging failed' }
Push-Location "$output/dex"
try { & "$tools/aapt.exe" add "$output/unsigned.apk" classes.dex } finally { Pop-Location }
if ($LASTEXITCODE) { throw 'Booster DEX packaging failed' }
& "$tools/zipalign.exe" -f 4 "$output/unsigned.apk" "$output/aligned.apk"
if ($LASTEXITCODE) { throw 'Booster zipalign failed' }
$key = Join-Path $env:USERPROFILE '.android/debug.keystore'
if (!(Test-Path -LiteralPath $key)) { throw 'Unity Android debug.keystore not found' }
# Served next to index.html by WebGL builds (Joycon2RateBooster.AndroidHelperFile).
$apk = [IO.Path]::GetFullPath((Join-Path $module '../../../WebHelpers~/Joycon2Boost-android.apk'))
New-Item -ItemType Directory -Path (Split-Path $apk) -Force | Out-Null
& "$jdk/java.exe" -jar "$tools/lib/apksigner.jar" sign --ks $key --ks-key-alias androiddebugkey --ks-pass pass:android --key-pass pass:android --out $apk "$output/aligned.apk"
if ($LASTEXITCODE) { throw 'Booster APK signing failed' }
& "$jdk/java.exe" -jar "$tools/lib/apksigner.jar" verify --verbose $apk
if ($LASTEXITCODE) { throw 'Booster APK verification failed' }
& "$tools/aapt.exe" dump badging $apk
if ($LASTEXITCODE) { throw 'Booster APK manifest verification failed' }
Write-Output "Booster APK: $apk"
