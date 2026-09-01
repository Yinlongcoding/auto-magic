[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectLocalDotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'

if (Test-Path -LiteralPath $projectLocalDotnetPath -PathType Leaf) {
    $dotnetPath = $projectLocalDotnetPath
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw 'A .NET 10 SDK is required. Install it system-wide or place it under .tools\dotnet.'
    }

    $dotnetPath = $dotnetCommand.Source
}

$sdkVersion = (& $dotnetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or -not $sdkVersion.StartsWith('10.')) {
    throw "A .NET 10 SDK is required. Current SDK: $sdkVersion"
}

Write-Host "Using .NET SDK $sdkVersion from $dotnetPath"

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
