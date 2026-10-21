# 发布 AM.NativeHost 并向当前用户的 Chrome 注册 native messaging host（com.am.bridge）。
# ExtensionId：在 chrome://extensions 加载 plugin/ 目录后显示的 32 位 ID。
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-p]{32}$')]
    [string]$ExtensionId
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hostName = 'com.am.bridge'
$publishDir = Join-Path $root 'artifacts\native-host'

dotnet publish (Join-Path $root 'src\AM.NativeHost\AM.NativeHost.csproj') -c Release -o $publishDir --nologo -v q
if ($LASTEXITCODE -ne 0) {
    throw 'AM.NativeHost 发布失败。'
}

$exe = Join-Path $publishDir 'AM.NativeHost.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "找不到 $exe"
}

$manifestDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AM\NativeMessaging'
$manifestPath = Join-Path $manifestDir "$hostName.json"
New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null

$manifest = [ordered]@{
    name = $hostName
    description = 'AM browser-to-desktop bridge'
    path = $exe
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

$registryPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
New-Item -Path $registryPath -Force | Out-Null
Set-Item -Path $registryPath -Value $manifestPath

Write-Host "已注册 $hostName"
Write-Host "插件 ID：$ExtensionId"
Write-Host "清单：$manifestPath"
Write-Host "程序：$exe"
