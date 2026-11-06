param(
    [ValidateSet('inspect','auto','recheck','cooldown-recheck','build')][string]$Mode='inspect',
    [ValidatePattern('^[1-9][0-9]{0,17}$')][string]$Table,
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$')][string]$When
)
$ErrorActionPreference='Stop'
if ([bool]$Table -ne [bool]$When) { throw 'Table 与 When 必须同时指定' }
$inputs=@('NoticeAck.csproj','Program.cs','Fixture.cs','notice.js','dialogs.js','lobby_snapshot.js') | ForEach-Object {
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_)).Hash
}
$sha=[Security.Cryptography.SHA256]::Create()
try { $hash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($inputs -join ''))))).Replace('-','').Substring(0,20) }
finally { $sha.Dispose() }
$stage=Join-Path ([IO.Path]::GetTempPath()) ("SeaSalt-NoticeAck-build-" + $hash)
$bin=Join-Path $stage 'out'
$exe=Join-Path $bin 'NoticeAck.exe'
if (!(Test-Path -LiteralPath $exe)) {
    & dotnet build (Join-Path $PSScriptRoot 'NoticeAck.csproj') -c Release --artifacts-path (Join-Path $stage 'artifacts') -o $bin --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Mode -eq 'build') { Write-Output $exe; exit 0 }
if ($Table) { & $exe $Mode $Table $When }
else { & $exe $Mode }
exit $LASTEXITCODE
