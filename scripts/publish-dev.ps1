[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'

if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "Project-local .NET SDK was not found: $dotnetPath"
}

$desktopOutput = Join-Path $projectRoot 'artifacts\desktop'
$nativeHostOutput = Join-Path $projectRoot 'artifacts\native-host'

& $dotnetPath publish (Join-Path $projectRoot 'src\AutoMagic.Desktop\AutoMagic.Desktop.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $desktopOutput `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

& $dotnetPath publish (Join-Path $projectRoot 'src\AutoMagic.NativeHost\AutoMagic.NativeHost.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $nativeHostOutput `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw 'Native messaging host publish failed.' }

Write-Host "Desktop: $desktopOutput"
Write-Host "Native messaging host: $nativeHostOutput"
