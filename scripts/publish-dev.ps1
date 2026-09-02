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
$catalogSource = Join-Path $projectRoot 'src\AutoMagic.Desktop\Data\ozon-category-tree.test.json'
$catalogDestination = Join-Path $desktopOutput 'Data\ozon-category-tree.test.json'

& $dotnetPath publish (Join-Path $projectRoot 'src\AutoMagic.Desktop\AutoMagic.Desktop.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $desktopOutput `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

if (-not (Test-Path -LiteralPath $catalogSource -PathType Leaf)) {
    throw "Ozon test category catalog was not found: $catalogSource"
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $catalogDestination) | Out-Null
Copy-Item -LiteralPath $catalogSource -Destination $catalogDestination -Force
if (-not (Test-Path -LiteralPath $catalogDestination -PathType Leaf)) {
    throw "Ozon test category catalog was not copied to publish output: $catalogDestination"
}
Write-Host "Ozon test category catalog: $catalogDestination"

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
