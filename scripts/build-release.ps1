param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$portableDotnet = Join-Path $projectRoot ".tools\dotnet\dotnet.exe"
$dotnet = if (Test-Path $portableDotnet) { $portableDotnet } else { "dotnet" }
$cliHome = Join-Path $projectRoot ".tools\dotnet-home"
$env:DOTNET_CLI_HOME = $cliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"

& $dotnet restore (Join-Path $projectRoot "PalPeek.sln")
if (-not $SkipTests) {
    & $dotnet test (Join-Path $projectRoot "PalPeek.sln") -c $Configuration --no-restore
}

$publish = Join-Path $projectRoot "artifacts\publish"
& $dotnet publish (Join-Path $projectRoot "src\PalPeek.App\PalPeek.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publish

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
Copy-Item (Join-Path $projectRoot "packaging\sunshine\apps.json") $sunshineDestination -Force

$moonlightSource = Join-Path $projectRoot ".tools\moonlight"
if (-not (Test-Path (Join-Path $moonlightSource "moonlight.exe"))) {
    throw "Moonlight portable v6.1.0 is missing from $moonlightSource"
}
Copy-Item (Join-Path $moonlightSource "*") $moonlightDestination -Recurse -Force

Write-Host "Release staged at $publish"
