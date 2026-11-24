<#
.SYNOPSIS
  Builds the signed release APK of the TV app and bundles it into tv/ (tv/psrs-tv-overlay.apk + tv/apk-version.txt).
.PARAMETER TvRepo
  Path of the PS-Remote-Sub-TV checkout (must hold keystore.properties for a signed release build).
.PARAMETER ExpectedCertSha256
  SHA-256 digest of the release signing certificate (public); a differently signed APK (e.g. debug) is rejected.
#>
param(
    [string]$TvRepo = (Join-Path $PSScriptRoot '..\..\PS-Remote-Sub-TV'),
    [string]$ExpectedCertSha256 = 'b55d9e19f96224c27337f7c47175da466b3a865e727125200a33a54357090204'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $TvRepo 'gradlew.bat'))) {
    throw "TV repo not found at '$TvRepo'. Pass -TvRepo <path to PS-Remote-Sub-TV>."
}
$TvRepo = (Resolve-Path $TvRepo).Path
$buildTools = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Android\Sdk\build-tools') -Directory |
    Sort-Object { [version]($_.Name -replace '[^\d.].*$', '') } -Descending | Select-Object -First 1
if (-not $buildTools) { throw 'Android SDK build-tools not found' }

$oldJavaHome = $env:JAVA_HOME
$env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr'   # for gradle and apksigner only
Push-Location $TvRepo
try {
    # Remove old outputs so a skipped signing step can never leave a stale APK behind.
    $releaseDir = Join-Path $TvRepo 'app\build\outputs\apk\release'
    Remove-Item (Join-Path $releaseDir 'app-release.apk'), (Join-Path $releaseDir 'app-release-unsigned.apk') -Force -ErrorAction SilentlyContinue

    & .\gradlew.bat assembleRelease
    if ($LASTEXITCODE -ne 0) { throw "gradlew assembleRelease failed ($LASTEXITCODE)" }

    $apk = Join-Path $releaseDir 'app-release.apk'
    if (Test-Path (Join-Path $releaseDir 'app-release-unsigned.apk')) { throw 'Release build was not signed (app-release-unsigned.apk produced); is keystore.properties present?' }
    if (-not (Test-Path $apk)) { throw "Release APK not produced: $apk" }

    $certs = (& (Join-Path $buildTools.FullName 'apksigner.bat') verify --print-certs $apk | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'apksigner verify failed: the APK is not validly signed' }
    if ($certs -notmatch 'certificate SHA-256 digest:\s*([0-9a-fA-F]{64})') { throw 'Could not read the signer certificate digest' }
    if ($Matches[1] -ne $ExpectedCertSha256) { throw "APK is signed with an unexpected certificate ($($Matches[1])); expected the release certificate." }

    $badging = (& (Join-Path $buildTools.FullName 'aapt2.exe') dump badging $apk | Out-String)
    if ($badging -notmatch "versionCode='(\d+)'") { throw 'Could not read versionCode from the built APK' }
    $versionCode = [int]$Matches[1]
} finally {
    Pop-Location
    $env:JAVA_HOME = $oldJavaHome
}

$tvDir = Join-Path $PSScriptRoot '..\tv'
New-Item -ItemType Directory -Force $tvDir | Out-Null
Copy-Item $apk (Join-Path $tvDir 'psrs-tv-overlay.apk') -Force
Set-Content -Path (Join-Path $tvDir 'apk-version.txt') -Value $versionCode -Encoding ascii -NoNewline
Write-Host "Bundled TV app versionCode $versionCode into tv/"
