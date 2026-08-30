[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-p]{32}$')]
    [string]$ExtensionId,

    [string]$NativeHostPath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($NativeHostPath)) {
    $NativeHostPath = Join-Path $projectRoot 'artifacts\native-host\AutoMagic.NativeHost.exe'
}

$resolvedHost = (Resolve-Path -LiteralPath $NativeHostPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedHost -PathType Leaf)) {
    throw "Native messaging host was not found: $resolvedHost"
}

$manifestDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AutoMagic\NativeMessaging'
$manifestPath = Join-Path $manifestDirectory 'com.automagic.desktop.json'
New-Item -ItemType Directory -Force -Path $manifestDirectory | Out-Null

$manifest = [ordered]@{
    name = 'com.automagic.desktop'
    description = 'Auto Magic Chrome-to-desktop bridge'
    path = $resolvedHost
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json -Depth 4

[System.IO.File]::WriteAllText(
    $manifestPath,
    $manifest,
    [System.Text.UTF8Encoding]::new($false))

$registryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.automagic.desktop'
New-Item -Path $registryPath -Force | Out-Null
Set-Item -Path $registryPath -Value $manifestPath

Write-Host 'Chrome native messaging host registered successfully.'
Write-Host "Extension ID: $ExtensionId"
Write-Host "Manifest: $manifestPath"
Write-Host "Executable: $resolvedHost"
