"""Regenerate the tiny stand-in model files the Onnx adapter tests use (no real model in CI).

    pip install onnx==1.23.2 sentencepiece==0.2.2 pillow numpy
    python make_fixtures.py
"""
import hashlib
import io
import json
from pathlib import Path

import numpy as np
import onnx
import sentencepiece as spm
from onnx import TensorProto, helper, numpy_helper
from PIL import Image

HERE = Path(__file__).parent
IMAGE_SIZE, MAX_TOKENS, DIMENSION = 224, 8, 4


def save(graph, name):
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 18)], ir_version=8)
    onnx.checker.check_model(model)
    onnx.save(model, str(HERE / name))


def vision(name, output_name="embedding"):
    """Mean of each channel times a fixed matrix: [batch, 3, 224, 224] -> [batch, DIMENSION]."""
    weights = numpy_helper.from_array(np.array([[1, 0, 0, 1], [0, 1, 0, 1], [0, 0, 1, 1]], np.float32), "w")
    save(helper.make_graph(
        [helper.make_node("ReduceMean", ["pixel_values", "axes"], ["mean"], keepdims=0),
         helper.make_node("MatMul", ["mean", "w"], [output_name])],
        "vision",
        [helper.make_tensor_value_info("pixel_values", TensorProto.FLOAT, ["batch", 3, IMAGE_SIZE, IMAGE_SIZE])],
        [helper.make_tensor_value_info(output_name, TensorProto.FLOAT, ["batch", DIMENSION])],
        [weights, numpy_helper.from_array(np.array([2, 3], np.int64), "axes")]), name)


def text(name):
    """Token ids as floats times a fixed matrix: [batch, MAX_TOKENS] -> [batch, DIMENSION]."""
    weights = numpy_helper.from_array(np.arange(MAX_TOKENS * DIMENSION, dtype=np.float32).reshape(MAX_TOKENS, DIMENSION) + 1, "w")
    save(helper.make_graph(
        [helper.make_node("Cast", ["input_ids"], ["ids"], to=TensorProto.FLOAT),
         helper.make_node("MatMul", ["ids", "w"], ["embedding"])],
        "text",
        [helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", MAX_TOKENS])],
        [helper.make_tensor_value_info("embedding", TensorProto.FLOAT, ["batch", DIMENSION])],
        [weights]), name)


def tokenizer():
    """A small BPE SentencePiece model with Gemma's special ids: pad 0, eos 1, bos 2, unk 3."""
    corpus = ["a dog on the beach", "a cat sleeping on a sofa", "beach at sunset", "birthday cake with candles",
              "snow covered mountains", "two people at an airport", "kids playing football in the park"] * 20
    model = io.BytesIO()
    spm.SentencePieceTrainer.train(sentence_iterator=iter(corpus), model_writer=model, model_type="bpe",
                                   vocab_size=60, normalization_rule_name="identity", pad_id=0, eos_id=1, bos_id=2, unk_id=3, character_coverage=1.0)
    (HERE / "tokenizer.model").write_bytes(model.getvalue())
    processor = spm.SentencePieceProcessor(model_proto=model.getvalue())
    prompts = ["A Dog", "beach at sunset", "kids playing football in the park on a sunny afternoon"]
    expected = []
    for prompt in prompts:
        ids = processor.encode(prompt.lower())[: MAX_TOKENS - 1] + [1]
        expected.append({"text": prompt, "ids": ids + [0] * (MAX_TOKENS - len(ids))})
    return expected


def synthetic_rgb(index, width, height):
    """Same formula as eng/models/export_siglip2.py and the C# tests."""
    y, x = np.mgrid[0:height, 0:width]
    channels = [(x * 3 + y * 5 + c * 85 + ((x // 16 + y // 16 + index) % 2) * 128) % 256 for c in range(3)]
    return np.stack(channels, axis=-1).astype(np.uint8)


def resize_reference():
    """Pillow bilinear resizes of synthetic images (down and up), the bytes SiglipPreprocessing must reproduce."""
    cases = []
    for index, (width, height, size) in enumerate([(37, 23, 8), (300, 200, 24), (5, 9, 16)]):
        resized = Image.fromarray(synthetic_rgb(index, width, height), "RGB").resize((size, size), Image.BILINEAR)
        cases.append({"index": index, "width": width, "height": height, "size": size,
                      "rgb": np.asarray(resized).reshape(-1).tolist()})
    return cases


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main():
    vision("vision.onnx")
    vision("vision-wrong-output.onnx", output_name="features")
    text("text.onnx")
    prompts = tokenizer()
    files = {key: {"path": name, "length": (HERE / name).stat().st_size, "sha256": sha256(HERE / name)}
             for key, name in [("vision", "vision.onnx"), ("text", "text.onnx"), ("tokenizer", "tokenizer.model")]}
    manifest = {"schemaVersion": 1, "model": "fixture/tiny", "license": "MIT", "imageSize": IMAGE_SIZE,
                "maxTokens": MAX_TOKENS, "padId": 0, "dimension": DIMENSION, "logitScale": 10.0, "logitBias": -1.0,
                "files": files}
    (HERE / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (HERE / "expected.json").write_text(json.dumps({"prompts": prompts, "resize": resize_reference()}) + "\n",
                                        encoding="utf-8")


if __name__ == "__main__":
    main()
