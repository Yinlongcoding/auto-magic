param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,
    [Parameter(Mandatory = $true)]
    [string] $ChineseSourcePath,
    [Parameter(Mandatory = $true)]
    [string] $DestinationPath
)

$source = Get-Content -LiteralPath $SourcePath -Raw | ConvertFrom-Json
$chineseSource = Get-Content -LiteralPath $ChineseSourcePath -Raw | ConvertFrom-Json
if ($source.has_next -ne $false) { throw '原产国字典仍有下一页。' }
if ($chineseSource.has_next -ne $false) { throw '原产国中文字典仍有下一页。' }

$chineseById = @{}
foreach ($item in $chineseSource.result) {
    $id = [long]$item.id
    if ($chineseById.ContainsKey($id)) { throw "原产国中文字典 valueId 重复：$id" }
    $chineseById[$id] = [string]$item.value
}

$values = @($source.result | ForEach-Object {
    [ordered]@{
        valueId = [long]$_.id
        displayZh = $chineseById[[long]$_.id]
        targetValue = [string]$_.value
    }
})

if ($values.Count -eq 0) { throw '原产国字典不能为空。' }
if (($values.valueId | Sort-Object -Unique).Count -ne $values.Count) { throw '原产国 valueId 重复。' }
if (($values.targetValue | Sort-Object -Unique).Count -ne $values.Count) { throw '原产国名称重复。' }
if ($chineseById.Count -ne $values.Count -or $values.Where({ [string]::IsNullOrWhiteSpace($_.displayZh) }).Count -gt 0) {
    throw '中俄原产国字典的 valueId 未完全对齐。'
}

$catalog = [ordered]@{
    schemaVersion = 1
    moduleId = 'origin-country'
    attributeId = 4389
    status = 'reference'
    source = 'Ozon description-category attribute values API (RU and ZH_HANS)'
    complete = $true
    values = $values
}

$directory = Split-Path -Parent $DestinationPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$catalog | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $DestinationPath -Encoding utf8
