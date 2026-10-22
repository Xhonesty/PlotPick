param([switch]$Test,[switch]$Publish,[switch]$Installer,[switch]$Offline)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$env:DOTNET_ROOT=Join-Path $taskRoot '.tools/dotnet'
$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.tools/cli-home'
$env:NUGET_PACKAGES=Join-Path $taskRoot '.tools/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$dotnet=Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if(-not(Test-Path -LiteralPath $dotnet)){throw '请先将 .NET SDK 10.0.401 解压到本项目 .tools/dotnet。'}
$source=@()
if($Offline){$source=@('--source',(Join-Path $taskRoot '.tools/feed'))}
& $dotnet build src/PlotPick.App/PlotPick.App.csproj -c Release @source -p:RestoreLockedMode=true --nologo
if($LASTEXITCODE -ne 0){throw '编译失败'}
if($Test){
    & $dotnet restore tests/PlotPick.Tests/PlotPick.Tests.csproj @source -p:RestoreLockedMode=true --nologo
    if($LASTEXITCODE -ne 0){throw '测试依赖还原失败'}
    & $dotnet test tests/PlotPick.Tests/PlotPick.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=core.trx' --results-directory artifacts/test-results --nologo
    if($LASTEXITCODE -ne 0){throw '测试失败'}
    & $dotnet src/PlotPick.App/bin/Release/net10.0-windows/win-x64/PlotPick.dll --verify-editing (Join-Path $taskRoot 'artifacts/test-results/v03')
    if($LASTEXITCODE -ne 0){throw 'WPF 编辑与视图验证失败，检查 artifacts/test-results/v03。'}
}
if($Publish -or $Installer){
    $publishPath=Join-Path $taskRoot 'artifacts/publish-0.3.0'
    & $dotnet publish src/PlotPick.App/PlotPick.App.csproj -c Release -r win-x64 --self-contained true @source -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false -o $publishPath --nologo
    if($LASTEXITCODE -ne 0){throw '发布失败'}
    # Only delete generated debug-symbol files in this exact publish directory.
    if([IO.Path]::GetFullPath($publishPath) -ne [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts/publish-0.3.0'))){throw '发布路径检查失败'}
    Get-ChildItem -LiteralPath $publishPath -Filter '*.pdb' -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE'),(Join-Path $taskRoot 'README.md'),(Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $publishPath -Force
    New-Item -ItemType Directory (Join-Path $publishPath 'samples') -Force | Out-Null
    Copy-Item -Path (Join-Path $taskRoot 'samples/*.plotpick') -Destination (Join-Path $publishPath 'samples') -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $publishPath -Recurse -Force
    New-Item -ItemType Directory (Join-Path $publishPath 'licenses') -Force | Out-Null
    if(Test-Path (Join-Path $taskRoot 'assets/licenses')){Copy-Item -Path (Join-Path $taskRoot 'assets/licenses/*') -Destination (Join-Path $publishPath 'licenses') -Recurse -Force}
    # App-local MSVC runtime for native OCR/OpenCV. Prefer the official redistributable payload under assets/native.
    foreach($name in @('vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll')){
        $runtime=Join-Path $taskRoot ('assets/native/'+$name)
        if(Test-Path -LiteralPath $runtime){Copy-Item -LiteralPath $runtime -Destination $publishPath -Force}
    }
    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath (Join-Path $taskRoot 'artifacts/PlotPick-0.3.0-win-x64.zip') -Force
    if($Installer){
        $compiler=Join-Path $taskRoot '.tools/inno/ISCC.exe'
        if(-not(Test-Path -LiteralPath $compiler)){throw '缺少 Inno Setup 编译器，请放入 .tools/inno。'}
        & $compiler installer/PlotPick.iss
        if($LASTEXITCODE -ne 0){throw '安装包编译失败'}
    }
    Get-ChildItem (Join-Path $taskRoot 'artifacts') -File | Where-Object { $_.Extension -in '.zip','.exe' } | Get-FileHash -Algorithm SHA256 | Format-Table -AutoSize
}
