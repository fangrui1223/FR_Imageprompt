# FR_Imageprompt 性能基线使用说明

本基线只使用系统临时目录中的合成数据库，不读取或修改真实图库。每个规模完成后会清理临时数据，结果写入指定 JSON 文件。

## 数据库基线

在仓库根目录执行：

```powershell
$env:APPDATA=(Resolve-Path '.').Path+'\.appdata'
$env:LOCALAPPDATA=(Resolve-Path '.').Path+'\.localappdata'
$env:DOTNET_CLI_HOME=(Resolve-Path '.').Path+'\.dotnethome'
$env:NUGET_PACKAGES=(Resolve-Path '.').Path+'\.packages'

& '.\.dotnet\dotnet.exe' run `
  --project tools\PromptVault.Benchmark\PromptVault.Benchmark.csproj `
  -c Release -- `
  --counts 600,5000,30000 `
  --iterations 12 `
  --output artifacts\performance\m0-baseline.json
```

报告包含：

- 合成数据写入时间。
- 数据库初始化时间。
- 实际搜索后端（`fts5-trigram` 或 `like-fallback`）。
- 图库首批 1000 条记录和深层游标页的耗时。
- 中文片段、英文跨词片段、分类筛选和标签筛选的平均值、P50、P95 与最大值。
- 操作系统、逻辑处理器数量、可用内存和 .NET 版本。

不同提交之间对比时，应使用相同机器、构建配置、条目数和迭代次数。报告中的“初始化”是仓库初始化时间，不等同于完整 WPF 冷启动时间。

需要保留一份合成图库做隔离 UI 验证时，可额外传入：

```powershell
--counts 30000 --retain-root .benchmark-ui
```

工具会在指定目录下新建带随机后缀的图库，并在报告的 `LibraryRoot` 中记录完整路径。保留模式只用于测试；主程序必须通过 `--settings <测试设置文件>` 显式指向该图库，不能依赖修改 `%LOCALAPPDATA%` 来隔离 Windows 已知文件夹。

M1-04 及后续需要验证真实缩略图解码、缓存和调度时，传入一张测试图片并指定独立文件数量：

```powershell
--counts 30000 `
--retain-root .benchmark-ui `
--retained-image-source C:\path\to\test-image.jpg `
--retained-image-count 1200
```

工具会为保留图库生成指定数量的独立原图、480px 路径和 1600px 路径，并让 30,000 条记录循环引用这组文件。这样既不会读取真实图库，也能避免所有记录共用一张图片导致缓存测试失真。

## 外部文件夹增量索引探针

M1-06 的探针会在系统临时目录创建独立图库和外部文件夹，测量首次文件头索引、未变化完整校验、索引查询以及新增、重命名、删除的增量更新：

```powershell
& '.\.dotnet\dotnet.exe' run `
  --project tools\PromptVault.ExternalIndexProbe\PromptVault.ExternalIndexProbe.csproj `
  -c Release -- `
  --count 30000 `
  --iterations 12 `
  --output docs\performance\external-index-probe.json
```

探针默认使用可正常解码的 1×1 PNG，目的是隔离目录枚举、图片头元数据读取和 SQLite 索引成本；它不代表约 5MB 外部原图的完整像素解码耗时。正式实现只在文件首次出现或大小、修改时间变化时读取图片头，未变化的低频完整校验不重复读取图片。探针退出时清理临时目录，不打开用户图库。

## WPF 虚拟化探针

M1-03 的布局与回收面板探针不加载图片，只测量 30,000 条布局、WPF 容器实现数量和连续滚动帧时间：

```powershell
& '.\.dotnet\dotnet.exe' run `
  --project tools\PromptVault.VirtualizationProbe\PromptVault.VirtualizationProbe.csproj `
  -c Release -- `
  docs\performance\virtualization-probe.json
```

探针使用 2560×1600 逻辑视口和 150% 目标缩放，预热 500ms 后连续测量 3 秒。完整主窗口仍需另外验证，因为缩略图解码、缓存调度和刷新差分不属于面板探针。

## M1-07 大图、缓存与缩略图门禁

门禁探针会在临时目录生成约 5MB 的 3840×2160 JPEG 和 32 个独立工作副本，测量首次大图解码、已缓存切换、480/1600/2800px 唯一文件解码分布、沉浸大图 LRU 以及缩略图调度器的并发和内存：

```powershell
& '.\.dotnet\dotnet.exe' run `
  --project tools\PromptVault.PerformanceGate\PromptVault.PerformanceGate.csproj `
  -c Release -- `
  --output docs\performance\m1-07-media-gate.json
```

默认退出时删除所有图片。只有需要继续做隔离真界面验收时才传入工作区内的 `--fixture-output`；验收完成后应在确认绝对路径位于工作区后清理。探针使用真实 WPF JPEG 解码，但不会强制清空 Windows 文件系统缓存，因此报告必须保留这一限制。

## Debug UI 诊断

UI 诊断仅在 Debug 构建并显式设置环境变量时启用：

```powershell
$env:PROMPTVAULT_DIAGNOSTICS='1'
$env:PROMPTVAULT_DIAGNOSTICS_PATH=(Resolve-Path '.').Path+'\.diagnostics\performance.jsonl'
$env:PROMPTVAULT_LOG_DIRECTORY=(Resolve-Path '.').Path+'\.diagnostics\logs'
& '.\.dotnet\dotnet.exe' run --project src\PromptVault.App\PromptVault.App.csproj -c Debug
```

未设置 `PROMPTVAULT_DIAGNOSTICS_PATH` 时，结构化 JSON Lines 日志位于：

```text
%LOCALAPPDATA%\PromptVault\diagnostics\performance.jsonl
```

当前记录：

- 应用启动、数据库初始化、主窗口显示和首屏内容准备。
- 搜索、分类与普通图库刷新；`gallery-refresh-transition` 分别记录查询开始和首屏准备完成时旧图库的条目数、行数与整层透明度。
- `gallery-refresh-applied` 记录查询、首屏准备、同步提交和总耗时，以及复用行数、复用/准备卡片数、首屏总数、提交前就绪数与提交后整层透明度。
- 缩略图缓存命中、实际解码时间、目标物理像素档位、排队量和缓存占用。
- 滚动交互期间的帧耗时 P50、P95、P99 和最大值，以及同一时刻的缩略图请求、合并、取消、并发与缓存快照。
- 剪贴板事件入队、图片准备完成和提示词应用。

诊断关闭时不会写性能日志；Release 构建不包含启用路径。正式验收仍需在 4K、150% 缩放和固定 30,000 张测试图库上进行一次人工滚动与逐帧检查。M1-05 的固定验收口径要求查询开始到首屏准备完成期间旧图库仍存在、`rowsOpacity` 始终为 1，且 `ReadyFirstViewportCards == FirstViewportCards` 后才允许提交。

Debug 诊断开启时，主图库按 `Ctrl+Shift+F9` 会运行固定三秒的标准滚动探针：每个去重后的 `CompositionTarget.RenderingTime` 推进 16 个逻辑像素，防止 WPF 在同一真实渲染帧内重复回调造成虚假翻页和帧数。结果写入 `gallery-scroll-gate-start`、`gallery-scroll-gate-complete` 和 `ui-frame-sample`。

滚动报告必须同时记录显卡驱动报告的刷新率。60Hz 的名义门槛仍为 P95 16.7ms、P99 33ms；若设备实际报告 59Hz 等分数刷新链路，则以 `T = 1000 / 刷新率` 校准为 P95 不超过一个 `T`、P99 不超过两个 `T`，并保留原始毫秒值、名义门槛和调整原因。不得从卡顿样本反推刷新率，也不得让超过两个真实刷新周期的长帧通过。
