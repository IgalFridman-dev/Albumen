# Albumen.Corpus (E3-S6)

Measures the owner's labelled photo corpus with the app's own code: the same WIC decodes, the
dHash and colour rule (Look-alike, Maybe similar), the tile sharpness score and `SharpnessComparison`
(E3-S3), and the SigLIP 2 image embedding (E4-S6). It writes one Markdown report per corpus version.
The photos, `labels.csv` and the reports stay in the corpus folder, outside both repositories.

## Corpus layout

The folders are the labels. One folder per set, named `<kind> <any name>`; photos that belong to no
set go in `single`. No subfolders, JPEG or PNG only, no commas in names.

```
Albumen-corpus\
  burst dog-park\         same moment, the subject moved
  exposure balcony\       same scene shot again: exposure, flash, HDR, a small step or zoom
  edit beach\             one photo and its crops, resizes, re-saved or filtered copies
  scan wedding-print\     one print scanned or photographed more than once
  screenshot boarding\    the same screen captured again, or a re-shared copy
  single\                 no partner here, including look-alikes from other moments
```

Photos are one set when you would keep only the best of them. A file-name prefix gives the blur
slice; without one the photo is `sharp`:

| Prefix | Slice | For |
|---|---|---|
| `blur-` | obvious-blur | missed focus or camera shake you would delete |
| `soft-` | ambiguous | portrait-mode background, night, panning, deliberate motion |
| `flat-` | not-applicable | screenshots, graphics, memes, documents |

Blur is compared only within a set, so a `blur-` or `soft-` photo needs a normal shot of the same
moment in its set. Folder and file names appear in the report.

## Commands

From the repository root on Windows, with the model files at `C:\PhotoSift\models`:

```powershell
dotnet run --project tools/Albumen.Corpus -c Release -- label  <corpus folder>
dotnet run --project tools/Albumen.Corpus -c Release -- report <corpus folder> C:\PhotoSift\models
```

`label` checks the layout and writes `labels.csv` (path, SHA-256, set, blur slice). The corpus version
is the first 12 hex digits of that file's SHA-256, so any added, removed, renamed or edited photo
gives a new version. `report` refuses to run when the folders no longer match `labels.csv`, then
writes `report-<version>.md`: pairwise precision and recall of the hash rules and of Maybe similar at
cosine thresholds 0.70 to 0.99 (overall and per kind), the closest wrong and farthest right pairs,
and the blur numbers against the 90% obvious-blur relevance target with a sweep of the "blurrier"
factor.
