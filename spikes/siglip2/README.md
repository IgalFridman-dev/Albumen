# SigLIP 2 encoder spike (E4-S1)

Throwaway harness for the Albumen 1.1 encoder decision. It is outside `Albumen.slnx`,
the release package gate and CI; nothing here ships. Findings: `FINDINGS.md`.

1. `export.py` (Python, dev PC, needs Hugging Face access) exports the vision and text
   towers to ONNX in fp32, plus a text tower whose vocabulary table is 8-bit (`emb8`), copies `tokenizer.model`, and writes
   `reference.json`: token ids and PyTorch embeddings for a fixed prompt list and the
   photos in a folder. The reference preprocessing is EXIF orientation, Pillow bilinear
   squash to the model size, then `[-1, 1]`.
2. `Siglip2Spike` (C#, Windows) reproduces that with what the app would use: the Near
   stage's 256-edge WIC decode, a port of Pillow's bilinear resize, `Microsoft.ML.Tokenizers`
   SentencePiece and ONNX Runtime CPU. It reports token-id equality, embedding cosines
   against the reference, same-best-photo per prompt, model sizes, load time, peak
   working set and ms per photo and per prompt.

```powershell
py -m venv .venv; .venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python export.py --model google/siglip2-base-patch16-224 --photos <photos> --out out\p16
dotnet run -c Release -- out\p16 <photos> fp32          # the Near stage's 256-edge decode
dotnet run -c Release -- out\p16 <photos> fp32 512      # a 512-edge decode
dotnet run -c Release -- out\p16 <photos> emb8 full     # 8-bit vocabulary, full-size decode
```

Each run writes `spike-<precision>-<decode edge>.json` next to the model. Model files stay out of git.
