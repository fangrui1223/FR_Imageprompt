# PromptVault

PromptVault 是一款 Windows 本地 AI 图片提示词收藏器。复制图片后，右下角轻量胶囊会在后台等待下一段完整文本；文本稳定 600ms 后自动保存，不打断当前工作。

## 已实现

- 浏览器位图、截图和资源管理器图片文件的剪贴板监听
- 不抢焦点的右下角收录胶囊、600ms 文本防抖和 10 秒撤销
- 两分钟无提示词时安全保留图片，并进入支持单项/批量处理的待补收件箱
- 未完成收录会话、暂存图片和剩余计时可在应用重启后恢复
- 按解码像素 SHA-256 去重，同图再次收录时更新原记录
- 原图、480px 和 1600px 两级缩略图分离存储
- SQLite WAL 数据库、FTS5 中英文片段搜索、分类和标签筛选
- 纵向回收式虚拟化卡片列表，十万级记录不会一次创建全部控件
- 单击复制提示词、双击大图、滚轮缩放、拖动查看和悬停动画
- 分类管理、标签、备注、回收站与 30 天清理策略
- ONNX Runtime CPU 基线与 DirectML 显卡加速回退
- 在线/离线 AI 模型包安装核心，以及主界面的离线导入入口
- Windows 系统 SQLite 提供程序，避免携带存在安全公告的原生 SQLite 包

## 使用

1. 启动 `PromptVault.exe`，首次运行选择一个图库文件夹。
2. 从浏览器、聊天软件、截图工具或资源管理器复制图片。
3. 在原软件中继续复制完整提示词；文本稳定约 600ms 后自动保存，可在胶囊中短时撤销。
4. 需要补充分类、标签或备注时，点击胶囊“展开”；两分钟没有提示词的图片可稍后在“待补提示词”中批量处理。
5. 单击图库卡片选中，按 `Ctrl+C` 或鼠标中键复制提示词，双击或按空格查看大图；关闭主窗口后软件继续驻留托盘。

## 构建

需要 .NET 10 SDK 和 Windows 10/11 x64：

```powershell
dotnet restore PromptVault.slnx --configfile NuGet.Config
dotnet test tests\PromptVault.Tests\PromptVault.Tests.csproj -c Release
dotnet publish src\PromptVault.App\PromptVault.App.csproj -c Release -r win-x64 --self-contained true -o publish\PromptVault-win-x64
```

也可以运行 `scripts\build.ps1`。发布产物自带 .NET 运行环境。

## AI 模型包

模型包为 ZIP，必须包含：

```text
clip/
  image_encoder.onnx
  manifest.json
```

`manifest.json` 定义 ONNX 输入/输出、图像尺寸、分类向量和候选标签向量。详细格式见 `docs/model-pack.md`。没有模型包时，收录、搜索和图库功能不受影响。
