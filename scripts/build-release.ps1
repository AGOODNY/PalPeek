param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$SkipRestore
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$portableDotnet = Join-Path $projectRoot ".tools\dotnet\dotnet.exe"
$dotnet = if (Test-Path $portableDotnet) { $portableDotnet } else { "dotnet" }
$cliHome = Join-Path $projectRoot ".tools\dotnet-home"
$env:DOTNET_CLI_HOME = $cliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"

function Assert-LastExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

if (-not $SkipRestore) {
    & $dotnet restore (Join-Path $projectRoot "PalPeek.sln")
    Assert-LastExitCode "dotnet restore"
}
if (-not $SkipTests) {
    $testArgs = @("test", (Join-Path $projectRoot "PalPeek.sln"), "-c", $Configuration)
    $testArgs += "--no-restore"
    & $dotnet @testArgs
    Assert-LastExitCode "dotnet test"
}

$publish = Join-Path $projectRoot "artifacts\publish"
if (Test-Path $publish) {
    $resolvedPublish = (Resolve-Path $publish).Path
    $expectedPublish = [IO.Path]::GetFullPath($publish)
    if ($resolvedPublish -ne $expectedPublish -or -not $resolvedPublish.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected publish path: $resolvedPublish"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}
$publishArgs = @(
    "publish",
    (Join-Path $projectRoot "src\PalPeek.App\PalPeek.App.csproj"),
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-o", $publish
)
if ($SkipRestore) {
    $publishArgs += "--no-restore"
}
& $dotnet @publishArgs
Assert-LastExitCode "dotnet publish"

$runtime = Join-Path $publish "runtime"
$sunshineDestination = Join-Path $runtime "sunshine"
$moonlightDestination = Join-Path $runtime "moonlight"
New-Item -ItemType Directory -Force $sunshineDestination, $moonlightDestination | Out-Null

$sunshineBuild = Join-Path $projectRoot "third_party\Sunshine\build\sunshine.exe"
if (-not (Test-Path $sunshineBuild)) {
    throw "PalPeek Host has not been built: $sunshineBuild"
}
Copy-Item $sunshineBuild $sunshineDestination -Force
Copy-Item (Join-Path $projectRoot "packaging\sunshine\palpeek.conf") $sunshineDestination -Force
$sunshineAssets = Join-Path $projectRoot "third_party\Sunshine\build\assets"
if (-not (Test-Path (Join-Path $sunshineAssets "apps.json"))) {
    throw "Sunshine runtime assets are missing from $sunshineAssets"
}
Copy-Item $sunshineAssets $sunshineDestination -Recurse -Force
Copy-Item (Join-Path $projectRoot "packaging\sunshine\apps.json") `
    (Join-Path $sunshineDestination "assets\apps.json") -Force

$moonlightSource = Join-Path $projectRoot ".tools\moonlight"
if (-not (Test-Path (Join-Path $moonlightSource "moonlight.exe"))) {
    throw "Moonlight portable v6.1.0 is missing from $moonlightSource"
}
Copy-Item (Join-Path $moonlightSource "*") $moonlightDestination -Recurse -Force

$iscc = Join-Path $projectRoot ".tools\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    throw "Inno Setup compiler is missing from $iscc"
}

$installerDirectory = Join-Path $projectRoot "artifacts\installer"
New-Item -ItemType Directory -Force $installerDirectory | Out-Null
$oldInstallers = Get-ChildItem -LiteralPath $installerDirectory -Filter "PalPeek-Setup-*-x64.exe" -File
foreach ($oldInstaller in $oldInstallers) {
    Remove-Item -LiteralPath $oldInstaller.FullName -Force
}

& $iscc (Join-Path $projectRoot "installer\PalPeek.iss")
Assert-LastExitCode "Inno Setup compilation"

$installer = Get-ChildItem -LiteralPath $installerDirectory -Filter "PalPeek-Setup-*-x64.exe" -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $installer -or $installer.Length -eq 0) {
    throw "The installer was not created."
}

$hash = Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256
$checksumPath = "$($installer.FullName).sha256"
"$($hash.Hash.ToLowerInvariant())  $($installer.Name)" |
    Set-Content -LiteralPath $checksumPath -Encoding ASCII

Write-Host "Release staged at $publish"
Write-Host "Installer: $($installer.FullName)"
Write-Host "SHA-256: $($hash.Hash)"
