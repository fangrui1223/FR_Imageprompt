# 发布与验证

## 用户包与内部诊断布局

用户包使用 Release、win-x64、self-contained。发布脚本会删除 PDB、`.lib`、`.exp`、临时文件和 `DirectML.Debug.*`，然后生成 `release-manifest.json`。清单列出每个文件的用途、大小和 SHA-256。

M9 起必须把三类文件物理分离：普通用户 ZIP 只含 `FR_Imageprompt.exe`；授权人 ZIP 只含 `FR_LicenseManager.exe`；个人 `.frlicense` 单独保存。普通用户 ZIP 禁止包含签发器、私钥、权威备份、许可证、设置、数据库、日志或图片。签发私钥只存在于授权人 Windows 凭据管理器和口令认证加密备份中，不进入源码或任何用户包。

内部诊断布局必须通过 `Publish-Release.ps1 -InternalDiagnostics` 单独生成，文件名含 `internal-diagnostics`，不得交付给普通用户。

## 标准命令

```powershell
powershell -ExecutionPolicy Bypass -File tools\release\Invoke-M6Gate.ps1
powershell -ExecutionPolicy Bypass -File tools\release\Publish-Release.ps1 `
  -Version 2.0.0 -OutputRoot publish\m6-final
```

门禁会顺序运行全部测试、固定 30,000 条数据库基准、4K 解码/缓存、虚拟化滚动和 30,000 × 512 维相似度探针，再由统一评估器按蓝图阈值返回成功或失败。`PromptVault.M6Gate --simulate-regression` 用于确认故意回归会使流程失败。

发布前还必须在 4K、150% 缩放下使用显式 `--settings` 的合成图库完成：

- 启动、搜索、分类和布局切换；
- 三秒标准滚动探针及截图；
- 沉浸大图前后切换；
- 收件箱、AI 审核和画板重启恢复；
- 导出诊断包并人工确认无图片、提示词和密钥。

真界面设置必须包含 `"CaptureListeningEnabled": false`，且 `LibraryRoot` 只能指向隔离的合成图库。

## M9 许可证发布门禁

- 缺少许可证和错设备许可证必须在 `AppSettings.Load`、图库初始化、数据库迁移、剪贴板监听、外部索引和 AI Worker 之前拒绝；目标合成图库不得生成 `promptvault.db`。
- 正确设备的永久许可证必须允许 V2.0、V2.1 和 V2.99，拒绝 V3；一年许可证必须在到期前通过、到期后拒绝。
- 真界面验证使用显式 `--license` 和显式 `--settings`，合成图库、关闭剪贴板与在线 AI，并记录真实图库访问 0 和网络请求 0。
- 发布后分别计算用户 ZIP、签发器 ZIP 和个人许可证的字节数与 SHA-256，并审计两个 ZIP 的逐文件清单。
- 签发器只供授权人使用。先把 V2 私钥做口令认证加密备份，再离线保存；私钥丢失不会破坏已签许可证，但会失去继续签发能力。

## 模型兼容

应用版本 2.0.0 支持 `clip/image_encoder.onnx` + `clip/manifest.json` 模型包。内置提供方标识为 `local-clip-vit-b32`，当前模型版本为 `d15189d7028b43f1d3e65039190477f6af591c2a`。模型包单独发布时，同样记录完整路径、字节数和 SHA-256。
