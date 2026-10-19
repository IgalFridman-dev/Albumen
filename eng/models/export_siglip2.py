"""Export the SigLIP 2 files Albumen ships, and the parity reference its tests check.

Runs on the dev PC (needs Hugging Face access). Nothing here ships or runs in the app.

    python export_siglip2.py --out <dir>

Writes vision.onnx (fp32), text.onnx (fp32 with a 16-bit vocabulary table), tokenizer.model,
siglip2-reference.json (token ids and PyTorch embeddings for synthetic images and fixed prompts)
and prints sizes, SHA-256 values and the PyTorch-vs-ONNX Runtime cosines.
"""
import argparse
import hashlib
import json
import shutil
import tempfile
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch
from huggingface_hub import hf_hub_download
from onnx import TensorProto, helper, numpy_helper
from PIL import Image
from transformers import AutoConfig, AutoModel, AutoTokenizer

MODEL = "google/siglip2-base-patch16-224"
MAX_LENGTH = 64  # SigLIP text is trained padded to 64 tokens.
OPSET = 18

# Mixed case, punctuation, digits, accents, Hebrew, and one prompt longer than 64 tokens.
PROMPTS = [
    "a dog", "a cat sleeping on a sofa", "Beach at sunset", "birthday cake with candles",
    "a screenshot of a phone app", "snow-covered mountains", "two people hugging at an airport",
    "receipt", "4 kids playing football", "café in Paris", "a blurry photo",
    "חתול על הספה", "ילדים בים", "a  very   spaced    prompt", "!!!",
    " ".join(["a long prompt about a red bicycle leaning on a garden wall"] * 8),
]

# Synthetic images the C# parity test rebuilds from the same formula (OnnxEncoderParityTests).
IMAGE_SIZES = [(300, 200), (173, 451), (512, 512), (64, 48)]


class Tower(torch.nn.Module):
    def __init__(self, tower, input_name):
        super().__init__()
        self.tower, self.input_name = tower, input_name

    def forward(self, values):
        return self.tower(**{self.input_name: values}).pooler_output


def synthetic_rgb(index, width, height):
    y, x = np.mgrid[0:height, 0:width]
    channels = [(x * 3 + y * 5 + c * 85 + ((x // 16 + y // 16 + index) % 2) * 128) % 256 for c in range(3)]
    return np.stack(channels, axis=-1).astype(np.uint8)


def preprocess(rgb, size):
    """What the app does: Pillow bilinear squash to the model size, then [-1, 1]."""
    pixels = np.asarray(Image.fromarray(rgb, "RGB").resize((size, size), Image.BILINEAR), dtype=np.float32)
    return ((pixels / 255.0 - 0.5) / 0.5).transpose(2, 0, 1)


def vocabulary_to_fp16(source, target):
    """Store the 256k-token vocabulary table in 16 bits and cast each looked-up row back to fp32."""
    model = onnx.load(str(source))
    graph = model.graph
    tables = {i.name: i for i in graph.initializer if len(i.dims) == 2 and i.dims[0] > 100_000}
    assert len(tables) == 1, list(tables)
    table = next(iter(tables.values()))
    users = [n for n in graph.node if table.name in n.input]
    assert len(users) == 1 and users[0].op_type == "Gather" and users[0].input[0] == table.name, users
    gather = users[0]
    table.CopyFrom(numpy_helper.from_array(numpy_helper.to_array(table).astype(np.float16), table.name))
    looked_up = gather.output[0]
    gather.output[0] = looked_up + "_fp16"
    cast = helper.make_node("Cast", [gather.output[0]], [looked_up], to=TensorProto.FLOAT)
    graph.node.insert(list(graph.node).index(gather) + 1, cast)
    onnx.checker.check_model(model)
    onnx.save(model, str(target))


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def cosine(a, b):
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    model = AutoModel.from_pretrained(MODEL, dtype=torch.float32).eval()
    tokenizer = AutoTokenizer.from_pretrained(MODEL)
    size = AutoConfig.from_pretrained(MODEL).vision_config.image_size
    preprocessor = json.loads(Path(hf_hub_download(MODEL, "preprocessor_config.json")).read_text(encoding="utf-8"))
    # The C# preprocessing hard-codes these; stop if the checkpoint differs.
    assert preprocessor.get("resample") == 2, preprocessor
    assert preprocessor.get("image_mean") == [0.5] * 3 and preprocessor.get("image_std") == [0.5] * 3, preprocessor
    assert not preprocessor.get("do_center_crop", False), preprocessor

    with tempfile.TemporaryDirectory() as scratch:
        text_fp32 = Path(scratch) / "text.fp32.onnx"
        for tower, dummy, input_name, path in [
            (model.vision_model, torch.zeros(1, 3, size, size), "pixel_values", out / "vision.onnx"),
            (model.text_model, torch.zeros(1, MAX_LENGTH, dtype=torch.int64), "input_ids", text_fp32),
        ]:
            torch.onnx.export(Tower(tower, input_name), (dummy,), str(path), input_names=[input_name],
                              output_names=["embedding"], dynamic_axes={input_name: {0: "batch"}, "embedding": {0: "batch"}},
                              opset_version=OPSET, dynamo=False)
        vocabulary_to_fp16(text_fp32, out / "text.onnx")

        prompts = [{"text": t, "ids": tokenizer(t.lower(), padding="max_length", max_length=MAX_LENGTH,
                                                truncation=True)["input_ids"]} for t in PROMPTS]
        images = [{"width": w, "height": h} for w, h in IMAGE_SIZES]
        pixels = np.stack([preprocess(synthetic_rgb(i, w, h), size) for i, (w, h) in enumerate(IMAGE_SIZES)])
        ids = np.array([p["ids"] for p in prompts], dtype=np.int64)
        with torch.no_grad():
            image_embeddings = model.vision_model(pixel_values=torch.from_numpy(pixels)).pooler_output.numpy()
            text_embeddings = model.text_model(input_ids=torch.from_numpy(ids)).pooler_output.numpy()

        def min_cosine(path, feed, expected):
            got = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"]).run(None, feed)[0]
            return round(min(cosine(a, b) for a, b in zip(got, expected)), 6)

        checks = {
            "vision.onnx": min_cosine(out / "vision.onnx", {"pixel_values": pixels}, image_embeddings),
            "text.onnx (16-bit vocabulary)": min_cosine(out / "text.onnx", {"input_ids": ids}, text_embeddings),
            "text fp32": min_cosine(text_fp32, {"input_ids": ids}, text_embeddings),
        }
        text_fp32_mb = round(text_fp32.stat().st_size / 2**20, 1)

    shutil.copy(hf_hub_download(MODEL, "tokenizer.model"), out / "tokenizer.model")
    reference = {
        "model": MODEL,
        "image_size": size,
        "max_length": MAX_LENGTH,
        "pad_id": tokenizer.pad_token_id,
        "prompts": [{**p, "embedding": e.tolist()} for p, e in zip(prompts, text_embeddings)],
        "images": [{**i, "embedding": e.tolist()} for i, e in zip(images, image_embeddings)],
    }
    (out / "siglip2-reference.json").write_text(json.dumps(reference, ensure_ascii=False), encoding="utf-8")
    files = {name: {"length": (out / name).stat().st_size, "sha256": sha256(out / name)}
             for name in ["vision.onnx", "text.onnx", "tokenizer.model"]}
    print(json.dumps({"files": files, "text_fp32_mb": text_fp32_mb, "pad_id": tokenizer.pad_token_id,
                      "min_cosine_torch_vs_onnx": checks}, indent=2))


if __name__ == "__main__":
    main()
