"""Export a SigLIP 2 checkpoint to ONNX (fp32 + int8) and write reference embeddings.

Runs once on the dev PC. Nothing here ships or runs in the app.

    python export.py --model google/siglip2-base-patch16-224 --photos <folder> --out <dir>
"""
import argparse
import hashlib
import json
import os
import platform
import shutil
from importlib.metadata import version
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
from huggingface_hub import hf_hub_download
from onnxruntime.quantization import QuantType, quantize_dynamic
from PIL import Image, ImageOps
from transformers import AutoConfig, AutoModel, AutoTokenizer

MAX_LENGTH = 64  # SigLIP text is trained padded to 64 tokens.
OPSET = 18

# Mixed case, punctuation, digits, accents and Hebrew, so tokenizer parity is not a happy-path check.
PROMPTS = [
    "a dog", "a cat sleeping on a sofa", "Beach at sunset", "birthday cake with candles",
    "a screenshot of a phone app", "snow-covered mountains", "two people hugging at an airport",
    "receipt", "4 kids playing football", "café in Paris", "a blurry photo",
    "חתול על הספה", "ילדים בים", "a  very   spaced    prompt", "!!!",
]


class Vision(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model.vision_model

    def forward(self, pixel_values):
        return self.model(pixel_values=pixel_values).pooler_output


class Text(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model.text_model

    def forward(self, input_ids):
        return self.model(input_ids=input_ids).pooler_output


def preprocess(path, size):
    """The reference the C# side must match: EXIF-oriented RGB, PIL bilinear squash, [-1, 1]."""
    with Image.open(path) as image:
        rgb = ImageOps.exif_transpose(image).convert("RGB")
        pixels = np.asarray(rgb.resize((size, size), Image.BILINEAR), dtype=np.float32)
    return ((pixels / 255.0 - 0.5) / 0.5).transpose(2, 0, 1)


def file_from(model_id, name):
    local = Path(model_id) / name
    return str(local) if local.is_file() else hf_hub_download(model_id, name)


def tokenize(tokenizer, text):
    return tokenizer(text.lower(), padding="max_length", max_length=MAX_LENGTH, truncation=True)["input_ids"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--photos", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--max-photos", type=int, default=40)
    args = parser.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    model = AutoModel.from_pretrained(args.model, dtype=torch.float32).eval()
    tokenizer = AutoTokenizer.from_pretrained(args.model)
    size = AutoConfig.from_pretrained(args.model).vision_config.image_size
    preprocessor = json.loads(Path(file_from(args.model, "preprocessor_config.json")).read_text(encoding="utf-8"))
    # The C# preprocessing hard-codes these; stop if a checkpoint differs.
    assert preprocessor.get("resample") == 2, preprocessor
    assert preprocessor.get("image_mean") == [0.5] * 3 and preprocessor.get("image_std") == [0.5] * 3, preprocessor
    assert not preprocessor.get("do_center_crop", False), preprocessor

    vision, text = Vision(model), Text(model)
    dummy_pixels = torch.zeros(1, 3, size, size)
    dummy_ids = torch.zeros(1, MAX_LENGTH, dtype=torch.int64)
    for module, dummy, name, input_name in [(vision, dummy_pixels, "vision", "pixel_values"),
                                            (text, dummy_ids, "text", "input_ids")]:
        fp32 = out / f"{name}.onnx"
        torch.onnx.export(module, (dummy,), str(fp32), input_names=[input_name], output_names=["embedding"],
                          dynamic_axes={input_name: {0: "batch"}, "embedding": {0: "batch"}},
                          opset_version=OPSET, dynamo=False)
        # Weights only, per channel: quantizing activation x activation MatMuls (attention scores)
        # and per-tensor weight scales cost too much accuracy on a ViT.
        quantize_dynamic(str(fp32), str(out / f"{name}.int8.onnx"), weight_type=QuantType.QInt8,
                         op_types_to_quantize=["MatMul", "Gemm", "Gather"], per_channel=True,
                         extra_options={"MatMulConstBOnly": True})
    shutil.copy(file_from(args.model, "tokenizer.model"), out / "tokenizer.model")

    photos = sorted(p for p in Path(args.photos).iterdir()
                    if p.suffix.lower() in {".jpg", ".jpeg", ".png"})[: args.max_photos]
    prompts = [{"text": t, "ids": tokenize(tokenizer, t)} for t in PROMPTS]
    with torch.no_grad():
        pixels = torch.from_numpy(np.stack([preprocess(p, size) for p in photos]))
        image_embeddings = torch.cat([vision(pixels[i:i + 8]) for i in range(0, len(photos), 8)]).numpy()
        text_embeddings = text(torch.tensor([p["ids"] for p in prompts])).numpy()

    # Torch vs ONNX Runtime from identical inputs: isolates export and quantization error from C# preprocessing.
    def onnx_cosines(name, feed, expected):
        got = ort.InferenceSession(str(out / name), providers=["CPUExecutionProvider"]).run(None, feed)[0]
        return round(float(min(cosine(a, b) for a, b in zip(got, expected))), 6)

    ids = np.array([p["ids"] for p in prompts], dtype=np.int64)
    onnx_check = {
        name: onnx_cosines(name, {"pixel_values": pixels.numpy()} if name.startswith("vision")
                           else {"input_ids": ids}, image_embeddings if name.startswith("vision") else text_embeddings)
        for name in ["vision.onnx", "vision.int8.onnx", "text.onnx", "text.int8.onnx"]
    }

    special = tokenizer("x")["input_ids"]
    plain = tokenizer("x", add_special_tokens=False)["input_ids"]
    reference = {
        "model": args.model,
        "image_size": size,
        "max_length": MAX_LENGTH,
        "pad_id": tokenizer.pad_token_id,
        "adds_bos": special[0] != plain[0],
        "adds_eos": special[-1] != plain[-1],
        "pads_left": tokenizer.padding_side == "left",
        "environment": {"platform": platform.platform(), "processor": platform.processor(),
                        **{p: version(p) for p in ["torch", "transformers", "onnx", "onnxruntime", "pillow"]}},
        "min_cosine_torch_vs_onnx": onnx_check,
        "prompts": [{**p, "embedding": e.tolist()} for p, e in zip(prompts, text_embeddings)],
        "photos": [{"file_hash": name_hash(p.name), "embedding": e.tolist()} for p, e in zip(photos, image_embeddings)],
    }
    (out / "reference.json").write_text(json.dumps(reference, ensure_ascii=False), encoding="utf-8")
    sizes = {f.name: round(f.stat().st_size / 2**20, 1) for f in sorted(out.glob("*.onnx"))}
    print(json.dumps({"sizes_mb": sizes, "min_cosine_torch_vs_onnx": onnx_check,
                      **{k: reference[k] for k in ["adds_bos", "adds_eos", "pads_left"]}}, indent=2))


def name_hash(name):
    """Photo names stay out of the reference file; C# finds each photo by the same hash."""
    return hashlib.sha256(name.encode()).hexdigest()[:16]


def cosine(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))


if __name__ == "__main__":
    main()
