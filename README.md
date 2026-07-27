# FR_Imageprompt 2.0

FR_Imageprompt（内部项目名 PromptVault）是 Windows 10/11 x64 的本地图像提示词收藏工具。它把原图、提示词、分类、标签、AI 草稿和自由画板保存在用户选择的图库中；默认不上传图片，也不要求在线账号。

## 已实现功能

- 沉浸式虚拟化图库：瀑布流/等高布局、即时中英文片段搜索、分类、标签、收藏、回收站、键盘选择和大图浏览。
- 无感收录：监听剪贴板图片或图片文件，等待随后复制的提示词；超时项目进入“待补提示词”收件箱，重启后继续恢复。
- 外部文件夹浏览：后台增量索引，不复制源文件；需要时再收藏到主图库。
- AI 元数据：本地 CLIP + DirectML/CPU 生成候选分类、标签和审美属性；候选必须确认、修改或拒绝后才成为用户数据。
- 可选在线 AI：默认关闭，密钥只进入 Windows 凭据管理器；是否发送既有提示词由用户单独控制。
- 相似图片与文本找图：使用本地向量索引。
- 多命名自由画板：拖放图片、平移缩放、旋转裁剪、层级、分组、便签、背景、撤销/重做和缺失源图重新定位。
- 发布级恢复：升级前完整性/空间/权限检查，数据库与设置备份，阶段标记，崩溃后继续验证或自动回滚。
- 脱敏诊断包：只导出日志、版本和白名单环境信息，不包含图片、完整提示词、密钥或设置。

## 安装与第一次运行

1. 解压 `FR_Imageprompt-2.0.0-win-x64.zip`。
2. 运行 `PromptVault.exe`。发布包自带 .NET 运行时，不需要另装 .NET。
3. 首次运行选择一个新的或已有的图库文件夹。图库中的 `promptvault.db`、`originals`、`thumbnails` 和 `backups` 均由应用管理。
4. 默认开启剪贴板收录。若暂时不需要，在顶部菜单点击“收录监听”关闭。

不要手工删除 `promptvault.db` 或 `backups`。升级旧图库前请先阅读[升级、备份与恢复](docs/upgrade-and-recovery.md)。

## 最短使用路径

1. 复制一张图片或图片文件。
2. 右下角出现收录胶囊后，继续复制完整提示词；文本稳定约 600ms 后自动保存。
3. 在图库输入任意中文或英文片段即时搜索。单击图片打开检查器，`Ctrl+C` 复制选中图片的提示词，空格打开沉浸大图。
4. 两分钟内没有提示词的图片会进入“待补提示词”，稍后可以单项或批量补全。
5. 从卡片菜单、右侧检查器或 `Ctrl+K` 命令面板把图片加入自由画板。

完整说明见[用户指南](docs/user-guide.md)，AI 数据边界见[本地与在线 AI 隐私](docs/privacy-and-ai.md)，模型包格式见[模型包文档](docs/model-pack.md)。

## 常用快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+F` | 聚焦即时搜索 |
| `Ctrl+K` | 打开命令面板，可发现筛选、布局、AI、画板和诊断入口 |
| 搜索框 `↓` / `Enter` / `Esc` | 把焦点交给图库 / 返回图库 |
| 命令面板 `↑` / `↓` / `Enter` / `Esc` | 选择、执行或关闭命令 |
| 方向键 / `Home` / `End` | 在图库中移动选择 |
| `Shift` + 方向键 | 扩展连续选择 |
| `Ctrl+C` | 复制选中图片提示词 |
| 空格 | 打开大图；画板中按住空格拖动平移 |
| 大图 `←` / `→` / `0` / `Esc` | 上一张 / 下一张 / 复位缩放 / 退出 |
| `Ctrl+M` | 切换透明模式 |
| `Alt+M` | 把外部文件夹中的当前图片收藏到主图库 |
| 画板 `Ctrl+A` / `Ctrl+Z` / `Ctrl+Y` | 全选 / 撤销 / 重做 |
| 画板 `Delete` | 删除选中画板项目或便签 |
| AI 审核 `Ctrl+Enter` | 确认当前草稿 |
| AI 审核 `Ctrl+R` | 拒绝当前草稿 |
| AI 审核 `Alt+←` / `Alt+→` | 上一条 / 下一条 |

顶部和左侧菜单默认会自动隐藏。把指针停在屏幕顶边或左边约 90–140ms 即可显示；也可通过命令面板或顶部设置把边栏设为常显。

## 从源码构建

需要 .NET 10 SDK、Windows 10/11 x64。仓库自带的本地 SDK 可按 `AGENTS.md` 的隔离环境变量使用。

```powershell
dotnet restore PromptVault.slnx --configfile NuGet.Config
dotnet test tests\PromptVault.Tests\PromptVault.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools\release\Publish-Release.ps1 -Version 2.0.0 -OutputRoot publish\m6-final
```

发布脚本生成用户包、逐文件清单、体积对比、ZIP SHA-256 和发布报告。内部诊断布局必须使用脚本的 `-InternalDiagnostics` 开关单独生成，不得混入用户包。详见[发布与验证](docs/release-and-validation.md)。

## 数据与支持

- 用户图片不会被迁移回滚覆盖；回滚只恢复数据库与设置。
- 回收站项目默认 30 天后清理。
- 在线 AI 默认关闭；本地 AI 不上传图片。
- “导出诊断包”位于左侧菜单和 `Ctrl+K` 命令面板。导出前仍建议人工检查 ZIP 内容。

遇到启动或升级问题时，先保留整个图库目录，不要删除数据库，然后按[恢复说明](docs/upgrade-and-recovery.md)操作。
