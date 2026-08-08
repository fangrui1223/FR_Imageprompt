# AGENTS.md

本文件用于帮助新的 Codex 对话快速接手本项目。内容以项目事实、目录分工、编码约定和验证方式为主；具体功能取舍以用户最新要求为准。

## 0. 当前长期实施蓝图

FR_Imageprompt 2.0 的产品决策、分阶段实施步骤、性能验收线和跨对话进度记录统一维护在：

- `docs/FR_Imageprompt-2.0-implementation-blueprint.md`

M7.2 自适应极速浏览层的 82 个子步骤、分规模缓存策略、冷图门禁和真实数据单独维护在：

- `docs/FR_Imageprompt-M7.2-adaptive-fast-browsing-blueprint.md`

M7.2 已于 2026-07-30 完成；后续性能修改必须以该文档和 `docs/performance/2026-07-30-m7.2-adaptive-fast-browsing.json` 为回归基线，不得重新引入“只加载 240”、全量位图常驻或滚动时清空旧图。

M7.2.1 外观回归修复也记录在同一蓝图：外观弹层文字必须使用主文字色，图片本体、边框和悬停遮罩必须共享圆角轮廓；不得重新使用矩形 `ClipToBounds` 代替图片圆角裁切。

M8 PureRef 式沉浸自由画板的产品契约、约百个可勾选子步骤、4K/150% 验收矩阵、置顶按钮和跨对话反馈账本单独维护在：

- `docs/FR_Imageprompt-M8-pureref-immersive-board-blueprint.md`

M8.2 图片/便签真实缩放、Shift/Alt 变换、Ctrl+右键整窗移动和固定相框裁剪的独立回归蓝图维护在：

- `docs/FR_Imageprompt-M8.2-transform-crop-window-drag-blueprint.md`

M8.3 图片角点输入所有权和右键拖动职责的独立回归蓝图维护在：

- `docs/FR_Imageprompt-M8.3-image-transform-right-drag-blueprint.md`

M8.4 图片等比缩放跟手与无闪烁回归蓝图维护在：

- `docs/FR_Imageprompt-M8.4-smooth-proportional-image-resize-blueprint.md`

M8.5 画板选择轮廓与产品副标题回归蓝图维护在：

- `docs/FR_Imageprompt-M8.5-board-selection-visuals-branding-blueprint.md`

M8 已于 2026-08-02 完成。自由画板现采用 PureRef 式画布优先交互：Space 聚焦/精确恢复、滚轮缩放、中键、Alt+左键或普通右键平移、右键短按命令、隐藏顶部栏、窗口置顶、临时检查器、共同选择变换和渐进高清。M8.1 首轮体验回归在同日完成并生成 `publish/FR_Imageprompt-M8.1-regression-win-x64.zip`；M8.2 于 2026-08-03 完成图片/便签四角真实缩放、Shift/Alt 变换、统一便签历史、Ctrl+右键整窗移动和固定相框裁剪；M8.3 同日修复图片角点被父级 Preview 空白画布入口抢占的问题，并把普通右拖统一为画布平移、Ctrl+右拖统一为整窗移动。后续画板修改必须保持四角 28 DIP 命中区、图片变换期间选择快照与输入所有权、裁剪期间零位图重建/零数据库写入和完成时单次提交，不得恢复 M8.1 的四边手柄、Ctrl+右键平移或旧收紧式裁剪。不得回退 M5 的画板持久化与 5,000 项视口裁剪、M7.2 的受控缓存与无黑场协议，并继续以 `docs/performance/2026-08-01-m8-pureref-board-gate.json`、`docs/performance/2026-08-02-m8.1-experience-regression.json`、M8.2 和 M8.3 最终性能记录为回归基线。

M8.4 于 2026-08-03 修复大图普通等比拖角时的闪烁与尺寸跳变：生产鼠标路径必须以固定按下点到当前鼠标点的绝对屏幕位移计算，不得重新累加会被手柄自身移动污染的 `Thumb.DragDelta`；拖动预览只更新冻结选择集及选择框，完成时复用现有图片视觉和位图。后续不得恢复逐帧全量 `RenderVisibleItems()` 或完成时 `RecreateSelectedVisuals()` 的旧路径；Shift 自由拉伸、Alt 中心缩放、裁剪和便签行为必须保持。性能记录见 `docs/performance/2026-08-03-m8.4-smooth-proportional-image-resize.json`。

M8.5 于 2026-08-08 完成画板选择视觉收口：未选中图片和单选图片根元素必须保持零边框；单选只显示共同变换细框，多选才为每张成员显示独立蓝色细框并同时保留共同外框。普通选择线必须按画布 Zoom 与窗口 DPI 补偿到约一个物理像素，不得恢复图片常驻 2 DIP 粗边框；裁剪继续使用 2 DIP 橙框。主界面蓝色副标题固定为“图片资产库 V2.0”，主标题、字号、颜色和位置不变。性能与体验记录见 `docs/performance/2026-08-08-m8.5-board-selection-visuals-branding.json`。

新的 Codex 对话在进行功能开发、性能优化、UI 重构、无感收录、AI 元数据或自由画板工作前，必须先完整阅读适用蓝图。自由画板还必须完整阅读 M8、M8.2、M8.3、M8.4 与 M8.5；实施时从适用蓝图中第一个未勾选步骤继续。每完成一步，必须在对应蓝图中勾选并追加日期、主要文件、验证结果和遗留问题。

## 1. 技术栈说明

项目名称：FR_Imageprompt / PromptVault

仓库地址：https://github.com/fangrui1223/FR_Imageprompt.git

项目类型：Windows 本地图像提示词收藏工具。

正式用户程序名称为 `FR_Imageprompt.exe`；主应用程序集名为 `FR_Imageprompt`，图标来自仓库根目录 `FR_Imageprompt.ico`。内部项目目录和 C# 命名空间继续保留 `PromptVault`，不要为了程序文件名再大范围重命名源码。

主要技术：

- 语言：C#，启用 nullable 和 implicit usings。
- 框架：.NET 10。
- 桌面 UI：WPF。
- 主程序目标框架：`net10.0-windows10.0.19041.0`。
- 核心库目标框架：`net10.0`。
- 运行目标：Windows 10/11 x64。
- 发布方式：`win-x64`，self-contained，single-file executable。
- 数据库：SQLite。
- SQLite 访问：`Microsoft.Data.Sqlite.Core` `10.0.0`。
- SQLite provider：`SQLitePCLRaw.provider.winsqlite3` `2.1.11`。
- AI 推理：ONNX Runtime DirectML。
- ONNX 包：`Microsoft.ML.OnnxRuntime.DirectML` `1.22.0`。
- 模型包构建：Node.js ESM scripts。
- 文本向量生成：`@huggingface/transformers` `^3.7.2`。
- 本地 CLIP 模型：`tools/model-pack/local-models/Xenova/clip-vit-base-patch32`。
- 测试框架：xUnit。
- 测试依赖：`xunit` `2.9.3`，`xunit.runner.visualstudio` `3.1.4`，`Microsoft.NET.Test.Sdk` `17.14.1`，`coverlet.collector` `6.0.4`。
- C# 语言版本：`latest`，见 `Directory.Build.props`。

常用命令需要在仓库根目录执行。为了避免写入用户系统目录，优先设置本地环境变量：

```powershell
$env:APPDATA=(Resolve-Path '.').Path+'\.appdata'
$env:LOCALAPPDATA=(Resolve-Path '.').Path+'\.localappdata'
$env:DOTNET_CLI_HOME=(Resolve-Path '.').Path+'\.dotnethome'
$env:NUGET_PACKAGES=(Resolve-Path '.').Path+'\.packages'
```

常用验证命令：

```powershell
& '.\.dotnet\dotnet.exe' build src\PromptVault.App\PromptVault.App.csproj -c Debug --no-restore
& '.\.dotnet\dotnet.exe' test tests\PromptVault.Tests\PromptVault.Tests.csproj -c Debug --no-restore
```

常用发布命令：

```powershell
& '.\.dotnet\dotnet.exe' publish src\PromptVault.App\PromptVault.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o publish\FR_Imageprompt-winsqlite-tags-edit-ai-pants-win-x64
Compress-Archive -LiteralPath 'publish\FR_Imageprompt-winsqlite-tags-edit-ai-pants-win-x64' -DestinationPath 'publish\FR_Imageprompt-winsqlite-tags-edit-ai-pants-win-x64.zip' -Force
```

模型包生成命令：

```powershell
node tools\model-pack\build.mjs
Compress-Archive -LiteralPath 'artifacts\PromptVault-CLIP-ViT-B32\clip' -DestinationPath 'artifacts\PromptVault-CLIP-ViT-B32-pants-reference.zip' -Force
```

模型分类烟测命令：

```powershell
& '.\.dotnet\dotnet.exe' 'tools\PromptVault.ModelSmoke\bin\Debug\net10.0-windows10.0.19041.0\win-x64\PromptVault.ModelSmoke.dll' 'artifacts\PromptVault-CLIP-ViT-B32' 'C:\path\to\image.png'
```

## 2. 目录结构

根目录：

- `PromptVault.slnx`：解决方案入口。
- `Directory.Build.props`：共享 C# 构建设置。
- `NuGet.Config`：NuGet 配置。
- `FR_Imageprompt.ico`：应用图标，也是正式 `FR_Imageprompt.exe` 的图标来源。
- `README.md`：项目说明。
- `AGENTS.md`：给 Codex 的项目接手说明。

源码目录：

- `src/PromptVault.Core/`：核心库。
- `src/PromptVault.Core/LibraryRepository.cs`：图库数据库、搜索、分类、标签、回收站等核心存取逻辑。
- `src/PromptVault.Core/LibraryRepository.ExternalIndex.cs`：外部文件夹持久化索引、稳定键分页和增量状态存取。
- `src/PromptVault.Core/Models.cs`：核心数据记录。
- `src/PromptVault.Core/ModelPackInstaller.cs`：模型包导入和校验。
- `src/PromptVault.App/`：WPF 主程序。
- `src/PromptVault.App/MainWindow.xaml`：主窗口界面。
- `src/PromptVault.App/MainWindow.xaml.cs`：主窗口主要交互逻辑。
- `src/PromptVault.App/MainWindow.*.cs`：主窗口按功能拆出的 partial 文件。
- `src/PromptVault.App/CaptureWindow.xaml`：新图片收录窗口界面。
- `src/PromptVault.App/CaptureWindow.xaml.cs`：收录窗口交互逻辑。
- `src/PromptVault.App/CaptureWindow.Drop.cs`：收录窗口拖放处理。
- `src/PromptVault.App/Services/`：应用服务，例如剪贴板监听、图片处理、AI 分类、Toast、托盘等。
- `src/PromptVault.App/Services/ExternalFolderIndexService.cs`：外部目录后台扫描、文件监控、低频校验和图片头元数据读取。
- `src/PromptVault.App/ViewModels.cs`：图库卡片和列表相关 ViewModel。

测试和工具：

- `tests/PromptVault.Tests/`：xUnit 测试。
- `tools/model-pack/`：CLIP 模型包构建、验证脚本。
- `tools/model-pack/build.mjs`：生成 `manifest.json` 并复制 ONNX 图像编码器。
- `tools/model-pack/verify.mjs`：模型包验证脚本。
- `tools/model-pack/local-models/`：离线模型文件。
- `tools/PromptVault.ModelSmoke/`：调用真实 `LocalAiClassifier` 的命令行烟测工具。
- `tools/PromptVault.ExternalIndexProbe/`：30,000 文件外部索引、复检、查询与增量变化性能探针。
- `tools/PromptVault.PerformanceGate/`：M1-07 的约 5MB/4K 图片解码、沉浸大图缓存和缩略图内存门禁探针。

文档、模型和产物：

- `docs/`：补充文档。
- `artifacts/PromptVault-CLIP-ViT-B32/clip/`：当前生成的模型包目录，包含 `image_encoder.onnx` 和 `manifest.json`。
- `artifacts/*.zip`：可导入的模型包。
- `publish/`：发布产物。
- `PromptVaultLibrary/`：本地运行数据或样例图库，不属于源代码。

命名规则：

- WPF 窗口使用 `*Window.xaml` 和对应 `*Window.xaml.cs`。
- 大型窗口代码按功能拆成 partial 文件，命名为 `MainWindow.Feature.cs` 或 `CaptureWindow.Feature.cs`。
- 对话框使用 `*Dialog.cs` 或现有同类模式。
- 服务类放在 `src/PromptVault.App/Services/`，命名为清晰名词，例如 `LocalAiClassifier`、`ClipboardMonitor`。
- 数据库和业务存取逻辑放在 `PromptVault.Core`，不要塞进 WPF code-behind。
- 测试文件按被测对象或行为命名，放在 `tests/PromptVault.Tests/`。

## 3. 编码规范

总体风格：

- 优先做小范围修改，保持现有结构和风格。
- 不为单个小需求引入大抽象。
- 保持 UI 逻辑、业务逻辑、存储逻辑分层清楚。
- 修改公共行为时同步补充或更新测试。
- Windows 路径和 PowerShell 命令优先按本项目现有方式书写。

C# 规范：

- 保持 nullable 友好写法。
- 优先使用 async API 处理文件、数据库和长耗时操作。
- SQLite 查询必须参数化，禁止拼接用户输入。
- 使用已有模型和记录类型，避免重复定义近似结构。
- 捕获异常时给用户可读提示，不吞掉关键错误。
- 注释要少而有用，只解释不明显的意图、阈值或兼容性原因。

WPF/UI 规范：

- 保持现有深色紧凑界面风格。
- 小修只改相关控件和事件处理，不顺手重排整体 UI。
- 需要拖动、右键菜单、选择状态等交互时，先查看现有模式再复用。
- 用户可见中文文案要清楚直接。
- 修改中文字符串时注意文件当前编码风格；若周围使用 Unicode escape，可继续使用 escape，避免编码漂移。

AI/模型包规范：

- 运行时只加载本地图像编码器和 `manifest.json`，文本向量由构建脚本预先生成。
- 修改分类、标签、规则或阈值时，同时检查 `LocalAiClassifier.cs` 和 `tools/model-pack/build.mjs`。
- 修改模型包后需要重新运行 `node tools\model-pack\build.mjs`。
- 涉及分类效果的改动，应至少用一张目标类型图片和一张非目标类型图片跑 `PromptVault.ModelSmoke`。
- 不要把某类标签固定塞给所有图片；标签应来自模型评分或明确规则。

数据库规范：

- 数据库 schema 和迁移逻辑集中在 `PromptVault.Core`。
- 保持 WAL、FTS、标签、回收站等现有机制兼容。
- 任何会影响用户图库数据的改动都要谨慎验证。
- 不要删除或重建用户数据库，除非用户明确要求。

文件和产物规范：

- `bin/`、`obj/`、`publish/`、`artifacts/` 下多数内容是生成产物，可以重建。
- 不要随意删除用户图库、原图、缩略图、设置文件。
- 临时评估脚本或临时 CSV 用完可以清理，但不要删除不确定来源的素材。
- 发布给用户时说明 zip 的完整路径。

禁止事项：

- 禁止使用破坏性命令重置或删除用户数据。
- 禁止在未确认需求时大范围重构。
- 禁止把本机绝对路径硬编码进正式逻辑。
- 禁止把测试用脚本、临时阈值实验逻辑混入正式代码。
- 禁止为了修一个提示文案而改分类、数据库或 UI 布局等无关逻辑。

## 4. 验证建议

普通代码改动：

```powershell
& '.\.dotnet\dotnet.exe' build src\PromptVault.App\PromptVault.App.csproj -c Debug --no-restore
```

核心库或数据库改动：

```powershell
& '.\.dotnet\dotnet.exe' test tests\PromptVault.Tests\PromptVault.Tests.csproj -c Debug --no-restore
```

模型相关改动：

```powershell
node tools\model-pack\build.mjs
& '.\.dotnet\dotnet.exe' build tools\PromptVault.ModelSmoke\PromptVault.ModelSmoke.csproj -c Debug --no-restore
```

发布前：

- 确认 Debug build 或测试通过。
- 重新 publish。
- 重新压缩发布目录。
- 若模型包有变化，重新压缩模型包。
