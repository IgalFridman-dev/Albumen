# E4-S1 SigLIP 2 encoder spike: findings

2026-10-19. Raw numbers: [`results/dev-pc-2026-10-19.json`](results/dev-pc-2026-10-19.json).
Machine: Igal's dev PC, Intel Core Ultra 7 270K Plus (24 logical processors), 47.4 GB RAM,
Windows 10.0.26300, idle, no VM running. Corpus: 37 Wikimedia Commons CC0/public-domain camera
images (35 JPEG of 3 to 16 MP, 8 with EXIF rotation, 2 PNG), not in the repo. 15 prompts, including
accents, digits and Hebrew. Reference: PyTorch, transformers, Pillow (Python 3.12.10).

## Verdict

SigLIP 2 passes. C# reproduces the reference: SentencePiece token ids are identical (15/15 in every
run) and fp32 embeddings match PyTorch to cosine 1.0 on the same pixels. No fallback to SigLIP 1 or CLIP
is needed; CLIP was not evaluated.

## Recommendations

| Decision | Recommendation | Why |
|---|---|---|
| D1 variant | `siglip2-base-patch32-256` | Half the vision CPU of patch16-224 (13 vs 28 ms, 85 vs 257 ms on one thread) with equal or better parity. Retrieval quality versus patch16 is not measured here; story 5's corpus checks it, and switching back is a re-export with no code change. |
| D1 precision | fp32 | Both int8 schemes lost accuracy and ran slower on this CPU (table below). |
| Decode for the encoder | WIC decode at a 512 edge, then the Pillow-style resize | Reusing the Near stage's 256 decode drops the mean image cosine to 0.97 to 0.98 and moves the best photo for half the prompts. A 512 decode costs the same as 256 here and gives 0.995. |
| D2 signature reuse | Add the reduced E2-S4 (embeddings only, keyed by SHA-256 + profile) | About 46 ms per photo end to end for patch32 (decode 27 + resize 6 + inference 13), above the plan's 40 ms line: a 10,000-photo rescan is about 8 minutes here and about 20 on one core. |
| Text tower size (story 3) | Open | fp32 text is 1,077 MB of the 1.44 GB total. An 8-bit vocabulary table halves it but drops text cosine to 0.990 and moves the best photo for 2 to 3 more prompts. Try fp16 weight storage first; otherwise ship the text tower as an optional download (`Unavailable` without it). |

## Numbers (medians; p95 in the raw file)

| Run (decode edge) | min / mean image cos | same best photo | vision ms, all / 1 thread | ms per prompt | peak RAM |
|---|---|---|---|---|---|
| p16 fp32, 256 | 0.905 / 0.971 | 8/15 | 29.6 / 264 | 11.9 | 2.0 GB |
| p16 fp32, 384 | 0.956 / 0.991 | 12/15 | 27.3 / 255 | 11.5 | 2.0 GB |
| p16 fp32, 512 | 0.946 / 0.995 | 13/15 | 27.9 / 257 | 11.9 | 2.0 GB |
| p16 fp32, full | 0.997 / 0.999 | 13/15 | 30.3 / 394 | 10.7 | 2.4 GB |
| p32 fp32, 256 | 0.927 / 0.978 | 6/15 | 14.6 / 89 | 11.7 | 2.0 GB |
| p32 fp32, 384 | 0.955 / 0.990 | 10/15 | 13.7 / 85 | 12.7 | 2.0 GB |
| p32 fp32, 512 | 0.970 / 0.995 | 11/15 | 12.8 / 85 | 12.5 | 2.0 GB |
| p32 fp32, full | 0.993 / 0.999 | 13/15 | 14.4 / 87 | 11.5 | 2.4 GB |
| p16 int8 (MatMulNBits), 256 | 0.861 / 0.941 | 7/15 | 138 / 470 | 110 | 0.75 GB |
| p32 int8 (MatMulNBits), 256 | 0.855 / 0.932 | 6/15 | 116 / 290 | 112 | 0.74 GB |

Decode at 256, 384 and 512 costs 26 to 28 ms; a full decode costs about 185 ms plus 92 ms of resize.
Model files: vision 355 MB (p16) / 361 MB (p32), text 1,077 MB, both fp32. Load: text 1.4 s, vision 0.6 to 0.8 s.
Full-decode cosine is 0.993 to 0.999 rather than 1.0 because WIC and libjpeg decode JPEGs slightly differently;
on PNG-equivalent pixels the dry run gave exactly 1.0.

## What int8 cost

- Dynamic int8 (`quantize_dynamic`) quantizes activations per tensor; ViT activation outliers took
  PyTorch-vs-ONNX cosine to 0.72 (vision) and 0.84 (text).
- Weight-only 8-bit (`MatMulNBits`) fixes most of that on random inputs but still gives vision 0.88 to
  0.92 on real photos, and dequantizing on this CPU makes it 4 to 8 times slower than fp32.
- Vocabulary-only 8-bit (`emb8`) costs no time but gives text cosine 0.990.

## Not covered (left to later stories)

- Retrieval usefulness: "same best photo" over 37 mostly unrelated photos is a coarse signal; the
  labelled corpus (story 2) and story 5 measure precision and recall.
- Golden VM (2 CPU, 4 GiB): not run; the one-thread column is the proxy.
- Cancellation, restart, corrupt or missing model files, the offline canary, licence and SHA-256
  pinning: stories 3, 4 and 8 (E4-S2, E4-S3, E4-S5).
- Why the p16 512 minimum (0.946) is below its 384 minimum on one photo is not investigated.
