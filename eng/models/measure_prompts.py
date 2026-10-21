"""Measure how SigLIP 2 scores real photos against prompts, to choose the Likely matches cut-off.

Runs on the dev PC with the export venv (needs the Hugging Face checkpoint). Nothing here ships
or runs in the app. The photos are read the way the app reads them (bilinear squash to the model
size, then [-1, 1]) and a bare prompt becomes the caption "a photo of …", like LikelyMatchesQuery.

    python measure_prompts.py --photos <folder> --prompts jetski computer flag food

Prints, per prompt, the best-scoring photos with the model's probability and cosine, then how
many photos pass each candidate cut-off. Share that output; file names stay on the PC unless you
paste them.
"""
import argparse
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from transformers import AutoModel, AutoTokenizer

MODEL = "google/siglip2-base-patch16-224"
MAX_LENGTH = 64
TEMPLATE = "a photo of "
CUT_OFFS = [0.5, 0.3, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005]


def preprocess(path, size):
    rgb = Image.open(path).convert("RGB")
    pixels = np.asarray(rgb.resize((size, size), Image.BILINEAR), dtype=np.float32)
    return ((pixels / 255.0 - 0.5) / 0.5).transpose(2, 0, 1)


def unit(vectors):
    return vectors / np.linalg.norm(vectors, axis=-1, keepdims=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--photos", required=True, help="folder of JPEG/PNG photos (subfolders included)")
    parser.add_argument("--prompts", nargs="+", required=True)
    parser.add_argument("--limit", type=int, default=400, help="photos to read at most")
    parser.add_argument("--top", type=int, default=12, help="photos to print per prompt")
    args = parser.parse_args()

    files = sorted(p for p in Path(args.photos).rglob("*") if p.suffix.lower() in {".jpg", ".jpeg", ".png"})
    files = files[: args.limit]
    if not files:
        raise SystemExit("no JPEG/PNG photos found")

    model = AutoModel.from_pretrained(MODEL).eval()
    tokenizer = AutoTokenizer.from_pretrained(MODEL)
    size = model.config.vision_config.image_size
    scale, bias = model.logit_scale.exp().item(), model.logit_bias.item()
    print(f"model={MODEL} photos={len(files)} logitScale={scale:.4f} logitBias={bias:.4f}")

    images = []
    with torch.no_grad():
        for start in range(0, len(files), 16):
            batch = np.stack([preprocess(p, size) for p in files[start : start + 16]])
            images.append(model.vision_model(pixel_values=torch.from_numpy(batch)).pooler_output.numpy())
    images = unit(np.concatenate(images))

    for prompt in args.prompts:
        caption = prompt if "photo" in prompt.lower() else TEMPLATE + prompt
        tokens = tokenizer([caption.lower()], padding="max_length", max_length=MAX_LENGTH,
                           truncation=True, return_tensors="pt")
        with torch.no_grad():
            text = unit(model.text_model(input_ids=tokens["input_ids"]).pooler_output.numpy())[0]
        cosines = images @ text
        probabilities = 1 / (1 + np.exp(-(scale * cosines + bias)))
        order = np.argsort(-cosines)
        print(f"\n== {caption!r}")
        for i in order[: args.top]:
            print(f"  p={probabilities[i] * 100:6.2f}%  cos={cosines[i]:.4f}  {files[i].name}")
        counts = "  ".join(f"p>={c:g}: {int((probabilities >= c).sum())}" for c in CUT_OFFS)
        print(f"  photos passing: {counts}")
        print(f"  cosine median={np.median(cosines):.4f} 90th={np.percentile(cosines, 90):.4f} max={cosines.max():.4f}")


if __name__ == "__main__":
    main()
