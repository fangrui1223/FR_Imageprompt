import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { AutoTokenizer, CLIPTextModelWithProjection, env } from "@huggingface/transformers";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../..");
const output = path.join(root, "artifacts", "PromptVault-CLIP-ViT-B32", "clip");
env.cacheDir = path.join(root, ".model-cache");
env.localModelPath = path.join(here, "local-models") + path.sep;
env.allowRemoteModels = false;
env.allowLocalModels = true;

const categories = {
  "\u4eba\u7269": "a portrait, person, character, fashion model, or human figure",
  "\u573a\u666f": "a landscape, environment, nature scene, cityscape, or outdoor view",
  "\u4ea7\u54c1": "a product photograph, commercial object, packaging, or merchandise",
  "\u5efa\u7b51": "architecture, building, interior design, room, or structural space",
  "\u63d2\u753b": "an illustration, anime artwork, painting, concept art, or drawing",
  "\u754c\u9762\u8bbe\u8ba1": "a user interface, mobile application, website, dashboard, or screen design",
  "\u88e4\u5b50\u53c2\u8003": "denim pants, ripped jeans, casual trousers, wide leg jeans, cargo pants, fashion bottoms product photo"
};

const guardVectors = {
  "\u4e0a\u8863\u53c2\u8003": "a fashion product photo focused on upper body clothing, t-shirt, shirt, blouse, hoodie, sweatshirt, jacket, denim jacket, torso wearing a top",
  "\u4eba\u7269": "a portrait or full body photo of a person, fashion model, human figure",
  "\u4ea7\u54c1": "a product photograph, commercial merchandise, studio product image, fashion product photography"
};

const tags = {
  "\u5199\u5b9e": "photorealistic", "\u7535\u5f71\u611f": "cinematic", "\u8d5b\u535a\u670b\u514b": "cyberpunk", "\u79d1\u5e7b": "science fiction",
  "\u5947\u5e7b": "fantasy", "\u6781\u7b80": "minimalist", "\u590d\u53e4": "vintage", "\u9ed1\u767d": "black and white",
  "\u9713\u8679": "neon lighting", "\u67d4\u548c\u8272\u5f69": "pastel colors", "\u591c\u666f": "night scene", "\u65e5\u5149": "daylight",
  "\u5de5\u4f5c\u5ba4\u706f\u5149": "studio lighting", "\u67d4\u5149": "soft light", "\u620f\u5267\u5149\u5f71": "dramatic lighting",
  "\u4eba\u50cf": "portrait", "\u5168\u8eab": "full body", "\u7279\u5199": "close-up", "\u5e7f\u89d2": "wide angle",
  "\u822a\u62cd": "aerial view", "\u5fae\u8ddd": "macro photography", "\u98ce\u666f": "landscape", "\u5ba4\u5185": "interior",
  "\u5efa\u7b51": "architecture", "\u4ea7\u54c1\u6444\u5f71": "product photography", "\u7f8e\u98df": "food photography",
  "\u52a8\u7269": "animal", "\u690d\u7269": "plant", "\u6c7d\u8f66": "vehicle", "\u592a\u7a7a": "outer space",
  "\u52a8\u6f2b": "anime", "\u6c34\u5f69": "watercolor painting", "\u6cb9\u753b": "oil painting", "3D\u6e32\u67d3": "3D render",
  "\u754c\u9762\u8bbe\u8ba1": "user interface design", "\u6982\u5ff5\u827a\u672f": "concept art",
  "\u725b\u4ed4\u88e4": "denim jeans", "\u4f11\u95f2\u88e4": "casual trousers", "\u7834\u6d1e\u725b\u4ed4\u88e4": "ripped jeans",
  "\u9614\u817f\u88e4": "wide leg pants", "\u5de5\u88c5\u88e4": "cargo pants"
};

const autoCategoryRules = [
  {
    name: "\u88e4\u5b50\u53c2\u8003",
    minScore: 0.28,
    minMargin: 0.02,
    compareWith: ["\u4e0a\u8863\u53c2\u8003", "\u4eba\u7269", "\u4ea7\u54c1"],
    tagNames: ["\u725b\u4ed4\u88e4", "\u4f11\u95f2\u88e4", "\u7834\u6d1e\u725b\u4ed4\u88e4", "\u9614\u817f\u88e4", "\u5de5\u88c5\u88e4"],
    maxTagCount: 2,
    maxTagScoreGap: 0.02
  }
];

function normalize(values) {
  const length = Math.sqrt(values.reduce((sum, value) => sum + value * value, 0));
  return values.map(value => value / length);
}

async function encode(entries, tokenizer, model) {
  const result = {};
  for (const [label, prompt] of Object.entries(entries)) {
    const inputs = tokenizer(prompt, { padding: true, truncation: true });
    const output = await model(inputs);
    result[label] = normalize(Array.from(output.text_embeds.data));
    process.stdout.write(`encoded ${label}\n`);
  }
  return result;
}

await fs.mkdir(output, { recursive: true });
await fs.copyFile(path.join(here, "local-models", "Xenova", "clip-vit-base-patch32", "onnx", "vision_model_quantized.onnx"), path.join(output, "image_encoder.onnx"));
const modelId = "Xenova/clip-vit-base-patch32";
const tokenizer = await AutoTokenizer.from_pretrained(modelId);
const model = await CLIPTextModelWithProjection.from_pretrained(modelId, { dtype: "q8" });
const manifest = {
  imageSize: 224,
  preferDirectMl: false,
  inputName: "pixel_values",
  outputName: "image_embeds",
  categorySuggestionMode: "ranked",
  categoryVectors: await encode(categories, tokenizer, model),
  guardVectors: await encode(guardVectors, tokenizer, model),
  tagVectors: await encode(tags, tokenizer, model),
  autoCategoryRules,
  source: modelId,
  sourceRevision: "d15189d7028b43f1d3e65039190477f6af591c2a"
};
await fs.writeFile(path.join(output, "manifest.json"), JSON.stringify(manifest));
await model.dispose();