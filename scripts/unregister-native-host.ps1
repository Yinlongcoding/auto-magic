[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$registryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.automagic.desktop'
$manifestPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AutoMagic\NativeMessaging\com.automagic.desktop.json'

if (Test-Path -LiteralPath $registryPath) {
    Remove-Item -LiteralPath $registryPath -Recurse -Force
}
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    Remove-Item -LiteralPath $manifestPath -Force
}

Write-Host 'Chrome native messaging host registration removed.'
