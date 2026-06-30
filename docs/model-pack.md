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
