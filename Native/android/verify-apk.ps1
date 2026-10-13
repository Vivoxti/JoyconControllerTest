param(
    [string]$Apk = (Join-Path $PSScriptRoot '..\..\Builds\Android\Kagefumi-Joycon2-dev.apk'),
    [string]$UnityAndroid = 'C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Data\PlaybackEngines\AndroidPlayer'
)
$ErrorActionPreference = 'Stop'
$apkPath = (Resolve-Path -LiteralPath $Apk).Path
$buildTools = Join-Path $UnityAndroid 'SDK\build-tools\36.0.0'
$java = Join-Path $UnityAndroid 'OpenJDK\bin\java.exe'
& $java -jar (Join-Path $buildTools 'lib\apksigner.jar') verify --verbose $apkPath
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed' }
$aapt = Join-Path $buildTools 'aapt2.exe'
$badging = (& $aapt dump badging $apkPath) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect APK metadata' }
if ($badging -notmatch "package: name='com.vivoderin.kagefumi'") { throw 'Unexpected application id' }
if ($badging -notmatch "native-code: 'arm64-v8a'") { throw 'ARM64 APK expected' }
if ($badging -notmatch 'application-debuggable') { throw 'Development APK expected' }
$manifest = (& $aapt dump xmltree $apkPath --file AndroidManifest.xml) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect merged manifest' }
foreach ($permission in @('BLUETOOTH_SCAN','BLUETOOTH_CONNECT','BLUETOOTH_ADMIN','ACCESS_FINE_LOCATION')) {
    if (!$manifest.Contains('android.permission.' + $permission)) { throw "Missing permission $permission" }
}
$archive = [IO.Compression.ZipFile]::OpenRead($apkPath)
try {
    foreach ($library in @('lib/arm64-v8a/libil2cpp.so','lib/arm64-v8a/libunity.so')) {
        if ($null -eq $archive.GetEntry($library)) { throw "Missing native player library $library" }
    }
    $blePresent = $false
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -notmatch '^classes\d*\.dex$') { continue }
        $stream = $entry.Open()
        $memory = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            if ([Text.Encoding]::ASCII.GetString($memory.ToArray()).Contains('Lcom/vivoderin/joycon2/BleTransport;')) {
                $blePresent = $true
            }
        } finally { $stream.Dispose(); $memory.Dispose() }
    }
    if (!$blePresent) { throw 'Java BLE transport is missing from APK DEX' }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $apkPath -Algorithm SHA256).Hash.ToLowerInvariant()
$report = @(
    "Verified APK: $apkPath",
    "Size: $((Get-Item -LiteralPath $apkPath).Length) bytes",
    "SHA256: $hash",
    'PASS: signature, application id, ARM64 IL2CPP, Development, BLE permissions, Java BLE class',
    'Hardware/launch test: pending (Pixel 3, Android 12)',
    '',
    $badging,
    '',
    $manifest
) -join "`n"
$report | Set-Content -LiteralPath (Join-Path (Split-Path $apkPath) 'verification.txt') -Encoding utf8
"$hash  $([IO.Path]::GetFileName($apkPath))" | Set-Content -LiteralPath ($apkPath + '.sha256') -Encoding ascii
Write-Output "APK checks passed: $apkPath ($([Math]::Round((Get-Item -LiteralPath $apkPath).Length / 1MB, 1)) MB)"
