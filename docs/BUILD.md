# 可复现构建

所有命令在 `plotpick/` 内执行，不修改工作区其他任务。

## 所需输入

- Windows x64，PowerShell 7。
- .NET SDK **10.0.401**，解压到 `.tools/dotnet/`；`global.json` 固定版本。
- NuGet 配置在根目录，下载缓存、包与本地源分别在 `.tools/cli-home/`、`.tools/packages/`、`.tools/feed/`。
- `assets/tessdata/` 随源码含 `eng.traineddata`、`chi_sim.traineddata`；应用完全本地使用这些模型。
- `assets/native/` 含随发布包使用的 MSVC 运行库，许可说明在 `assets/licenses/`。
- 安装包额外需要 Inno Setup 编译器及其完整配套文件，放到 `.tools/inno/`，使 `.tools/inno/ISCC.exe` 存在。本次使用 **Inno Setup 7.1.0**；该本地编译器显示非商业使用许可，构建方应使用与自己的用途匹配的编译器授权。源码包不分发编译器。

## 构建

```powershell
# 联网还原固定版本的依赖并编译
./scripts/build.ps1 -Test -Publish

# 已准备完整本地 NuGet 源时，可断网构建
./scripts/build.ps1 -Offline -Test -Publish -Installer

# 当前桌面会话中验证自包含发布版，测试只操作自己启动的子进程
./scripts/test-ui.ps1

# 生成源码归档（只在开发/打包时需要 Python）
python scripts/package-source.py
```

网络受限时可以先运行 `python scripts/fetch-deps.py` 准备 `.tools/feed/`。此脚本只下载构建依赖，应用自身不联网。

核心项目声明稳定的 `win-x64` 还原图，构建命令开启 `RestoreLockedMode`。若主动升级依赖，应显式重新生成并审查各项目 `packages.lock.json`，再执行测试；不要在发布构建时悄悄更新版本。

## 产物

`artifacts/publish-0.3.0/` 是自包含程序目录；便携 ZIP、Setup EXE 和源码 ZIP 放在 `artifacts/`。`.tools/`、`bin/`、`obj/` 不进入源码包。源代码包含测试、模型、样例、第三方许可和生成脚本。

真实数据图的重新生成需要 NumPy、Matplotlib、Pillow；原始数据存档及已生成图像已包含在源码中，因此普通构建和测试不依赖 Python 或网络。执行 `scripts/real-data-fixtures.py` 可按来源元数据重新生成。

便携包没有重新打包开发 SDK；它只包含 .NET 运行时。安装包采用当前用户安装，无自动升级、遥测或账号服务。开发机器上的测试结果不能替代干净目标机的发布验收。
