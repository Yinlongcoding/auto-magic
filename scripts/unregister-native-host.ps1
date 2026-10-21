# 删除 register-native-host.ps1 写入的注册表项和清单文件。发布出的程序留在 artifacts/ 不动。
$ErrorActionPreference = 'Stop'
$hostName = 'com.am.bridge'

$registryPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
if (Test-Path $registryPath) {
    Remove-Item -Path $registryPath -Force
}

$manifestPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "AM\NativeMessaging\$hostName.json"
if (Test-Path -LiteralPath $manifestPath) {
    Remove-Item -LiteralPath $manifestPath -Force
}

Write-Host "已取消注册 $hostName"
