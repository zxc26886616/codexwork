param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$Batch,
    [string]$OfflineFixture
)
$ErrorActionPreference = 'Stop'
# 发现、绑定处置、另进程复核都由同一入口完成；只有干净且就绪的最终发现可发布预检。
$notice = Join-Path $PSScriptRoot '批次监管/账号提醒'
$exe = & (Join-Path $notice '处理提醒.ps1') -Mode build
if ($LASTEXITCODE -ne 0 -or !$exe) { throw '后台账号发现工具未能构建' }
$invoke = @('-B',(Join-Path $notice '入口发现.py'),'--batch',$Batch,'--exe',$exe)
if ($PSBoundParameters.ContainsKey('OfflineFixture')) {
    if ([string]::IsNullOrWhiteSpace($OfflineFixture)) { throw '隔离根无效' }
    $invoke += @('--fixture',$OfflineFixture)
}
& python @invoke
exit $LASTEXITCODE
