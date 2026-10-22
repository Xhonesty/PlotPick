param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$env:DOTNET_ROOT=Join-Path $taskRoot '.tools/dotnet'
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools/cli-home'
$env:NUGET_PACKAGES=Join-Path $taskRoot '.tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$dotnet=Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if(-not(Test-Path 'artifacts/publish-0.3.0/PlotPick.exe')){throw '请先运行 scripts/build.ps1 -Publish。'}
& $dotnet build tests/PlotPick.UiSmoke/PlotPick.UiSmoke.csproj --source .tools/feed -p:RestoreLockedMode=true --nologo
if($LASTEXITCODE -ne 0){throw 'UI 测试编译失败'}
& $dotnet tests/PlotPick.UiSmoke/bin/Debug/net10.0-windows/PlotPick.UiSmoke.dll $taskRoot
if($LASTEXITCODE -ne 0){throw 'UI 测试未通过，检查 artifacts/test-results/v030-ui-smoke.json。'}
