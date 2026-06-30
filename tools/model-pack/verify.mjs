import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const ort = require("./node_modules/.pnpm/onnxruntime-node@1.21.0/node_modules/onnxruntime-node");
const sharp = require("./node_modules/.pnpm/sharp@0.34.1/node_modules/sharp");
const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../..");
const clip = path.join(root, "artifacts", "PromptVault-CLIP-ViT-B32", "clip");
const imagePath = process.argv[2];
if (!imagePath) throw new Error("Pass an image path to verify.mjs");

const size = 224;
const { data } = await sharp(imagePath).resize(size, size, { fit: "fill" }).removeAlpha().raw().toBuffer({ resolveWithObject: true });
const values = new Float32Array(1 * 3 * size * size);
const mean = [0.48145466, 0.4578275, 0.40821073];
const std = [0.26862954, 0.26130258, 0.27577711];
for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
  const pixel = (y * size + x) * 3;
  for (let channel = 0; channel < 3; channel++)
    values[channel * size * size + y * size + x] = (data[pixel + channel] / 255 - mean[channel]) / std[channel];
}

const session = await ort.InferenceSession.create(path.join(clip, "image_encoder.onnx"));
const result = await session.run({ pixel_values: new ort.Tensor("float32", values, [1, 3, size, size]) });
const imageVector = Array.from(result.image_embeds.data);
const norm = Math.sqrt(imageVector.reduce((sum, value) => sum + value * value, 0));
for (let i = 0; i < imageVector.length; i++) imageVector[i] /= norm;
const manifest = JSON.parse(fs.readFileSync(path.join(clip, "manifest.json"), "utf8"));
const score = vector => vector.reduce((sum, value, index) => sum + value * imageVector[index], 0);
const rank = values => Object.entries(values).map(([name, vector]) => ({ name, score: score(vector) })).sort((a, b) => b.score - a.score);
console.log(JSON.stringify({ categories: rank(manifest.categoryVectors).slice(0, 3), tags: rank(manifest.tagVectors).slice(0, 5) }, null, 2));
