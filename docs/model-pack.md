# PromptVault CLIP 模型包

模型包用于将模型二进制与应用更新解耦。ZIP 根目录必须包含 `clip/image_encoder.onnx` 与 `clip/manifest.json`。

```json
{
  "imageSize": 224,
  "inputName": "pixel_values",
  "outputName": "image_embeds",
  "categoryVectors": {
    "人物": [0.01, -0.02],
    "场景": [-0.03, 0.04]
  },
  "tagVectors": {
    "赛博朋克": [0.05, -0.06]
  }
}
```

- 向量维度必须与 `image_embeds` 的最后一维一致。
- 应使用与图像编码器配套的多语言文本编码器，提前为分类名称、AI 描述和候选标签生成归一化向量。
- 应用使用 CLIP 标准 RGB 均值与方差，并将图片缩放到 `imageSize × imageSize`。
- 应用优先尝试 DirectML；失败后自动使用 CPU。
- 在线安装必须由发布方提供 HTTPS 地址及 SHA-256；离线导入会在解压前计算 SHA-256，并拒绝目录穿越路径。
- 安装新模型时，当前 `clip` 会先原子移动为 `clip.old-时间戳`；新模型就位失败会恢复旧目录。
- 旧模型备份默认保留 2 份，设置字段 `ModelBackupRetentionCount` 可在 0–10 范围调整，防止 `.old-*` 无限增长。
- 本地模型不上传图片；在线 AI 的数据边界见[本地与在线 AI 隐私](privacy-and-ai.md)。
