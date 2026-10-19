using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Siglip2Spike;

if (args.Length is < 3 or > 4 || args[2] is not ("fp32" or "int8") || args.Length == 4 && args[3] != "full")
{
    Console.Error.WriteLine("usage: Siglip2Spike <export dir> <photos dir> <fp32|int8> [full]");
    return 2;
}
var (modelDir, photosDir, precision) = (args[0], args[1], args[2]);
// "full" decodes at full resolution instead of reusing the Near stage's 256-edge decode,
// to separate the cost of that shortcut from any preprocessing error.
var decodeEdge = args.Length == 4 ? int.MaxValue : WicDecode.MaximumEdge;
var suffix = precision == "int8" ? ".int8.onnx" : ".onnx";
var reference = JsonNode.Parse(File.ReadAllText(Path.Combine(modelDir, "reference.json")))!;
var size = (int)reference["image_size"]!;
var maxLength = (int)reference["max_length"]!;

// Text tower: tokenizer parity, embedding parity, time per prompt (tokenize + inference).
var clock = Stopwatch.StartNew();
SentencePieceTokenizer tokenizer;
using (var stream = File.OpenRead(Path.Combine(modelDir, "tokenizer.model")))
    tokenizer = SentencePieceTokenizer.Create(stream, (bool)reference["adds_bos"]!, (bool)reference["adds_eos"]!);
using var text = new InferenceSession(Path.Combine(modelDir, "text" + suffix));
var textLoadMs = clock.Elapsed.TotalMilliseconds;
long[] Tokens(string prompt) => SiglipInputs.TokenIds(
    tokenizer, prompt, maxLength, (int)reference["pad_id"]!, (bool)reference["pads_left"]!);

var prompts = reference["prompts"]!.AsArray();
var promptEmbeddings = new List<float[]>();
var tokenMismatches = new List<string>();
var promptMs = new List<double>();
foreach (var prompt in prompts)
{
    var ids = Tokens((string)prompt!["text"]!);
    if (!ids.SequenceEqual(prompt["ids"]!.AsArray().Select(id => (long)id!)))
        tokenMismatches.Add((string)prompt["text"]!);
    promptEmbeddings.Add(Run(text, "input_ids", new DenseTensor<long>(ids, [1, maxLength])));
    for (var pass = 0; pass < 3; pass++)
    {
        clock.Restart();
        Run(text, "input_ids", new DenseTensor<long>(Tokens((string)prompt["text"]!), [1, maxLength]));
        promptMs.Add(clock.Elapsed.TotalMilliseconds);
    }
}

// Vision tower: the app's 256-edge WIC decode, then resize + normalize, then inference.
clock.Restart();
using var vision = new InferenceSession(Path.Combine(modelDir, "vision" + suffix));
var visionLoadMs = clock.Elapsed.TotalMilliseconds;
using var singleThread = new SessionOptions { IntraOpNumThreads = 1 };
using var visionOneThread = new InferenceSession(Path.Combine(modelDir, "vision" + suffix), singleThread);

var photos = reference["photos"]!.AsArray();
var photoPaths = Directory.GetFiles(photosDir).ToDictionary(f => NameHash(Path.GetFileName(f)));
var photoEmbeddings = new List<float[]>();
List<double> decodeMs = [], preprocessMs = [], inferMs = [], inferOneThreadMs = [];
foreach (var (photo, index) in photos.Select((p, i) => (p!, i)))
{
    clock.Restart();
    var (bgra, width, height) = WicDecode.Decode(photoPaths[(string)photo["file_hash"]!], decodeEdge);
    decodeMs.Add(clock.Elapsed.TotalMilliseconds);
    clock.Restart();
    var pixels = new DenseTensor<float>(SiglipInputs.Pixels(bgra, width, height, size), [1, 3, size, size]);
    preprocessMs.Add(clock.Elapsed.TotalMilliseconds);
    if (index == 0) { Run(vision, "pixel_values", pixels); Run(visionOneThread, "pixel_values", pixels); } // warm-up
    clock.Restart();
    photoEmbeddings.Add(Run(vision, "pixel_values", pixels));
    inferMs.Add(clock.Elapsed.TotalMilliseconds);
    clock.Restart();
    Run(visionOneThread, "pixel_values", pixels);
    inferOneThreadMs.Add(clock.Elapsed.TotalMilliseconds);
}

// Text search sanity: does each prompt pick the same best photo as the reference?
var referencePhotos = photos.Select(p => Floats(p!["embedding"]!)).ToList();
var sameTopPhoto = prompts.Select((p, i) =>
    Best(Floats(p!["embedding"]!), referencePhotos) == Best(promptEmbeddings[i], photoEmbeddings)).Count(same => same);

var report = new
{
    model = (string)reference["model"]!,
    precision,
    decode_edge = decodeEdge == int.MaxValue ? "full" : decodeEdge.ToString(System.Globalization.CultureInfo.InvariantCulture),
    machine = new { Environment.MachineName, Environment.ProcessorCount, os = Environment.OSVersion.ToString() },
    sizes_mb = Directory.GetFiles(modelDir, "*" + suffix).Where(f => precision == "int8" || !f.Contains(".int8."))
        .ToDictionary(f => Path.GetFileName(f), f => Math.Round(new FileInfo(f).Length / 1048576.0, 1)),
    load_ms = new { text = Math.Round(textLoadMs), vision = Math.Round(visionLoadMs) },
    peak_working_set_mb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576,
    parity = new
    {
        token_ids_exact = $"{prompts.Count - tokenMismatches.Count}/{prompts.Count}",
        token_mismatches = tokenMismatches,
        min_text_cosine = MinCosine(promptEmbeddings, prompts.Select(p => Floats(p!["embedding"]!)).ToList()),
        min_image_cosine = MinCosine(photoEmbeddings, referencePhotos),
        mean_image_cosine = Math.Round(photoEmbeddings.Select((e, i) => Cosine(e, referencePhotos[i])).Average(), 5),
        same_top_photo_per_prompt = $"{sameTopPhoto}/{prompts.Count}",
    },
    ms_per_prompt = Stats(promptMs),
    ms_per_photo = new
    {
        decode_256 = Stats(decodeMs),
        preprocess = Stats(preprocessMs),
        inference_all_threads = Stats(inferMs),
        inference_one_thread = Stats(inferOneThreadMs),
    },
    photos = photos.Count,
};
var json = JsonSerializer.Serialize(report, Indented.Options);
File.WriteAllText(Path.Combine(modelDir, $"spike-{precision}{(decodeEdge == int.MaxValue ? "-full" : "")}.json"), json);
Console.WriteLine(json);
return 0;

static float[] Run<T>(InferenceSession session, string input, DenseTensor<T> tensor)
{
    using var results = session.Run([NamedOnnxValue.CreateFromTensor(input, tensor)]);
    return [.. results[0].AsEnumerable<float>()];
}

static string NameHash(string name) =>
    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..16];

static float[] Floats(JsonNode node) => [.. node.AsArray().Select(v => (float)v!)];

static double Cosine(float[] a, float[] b)
{
    double dot = 0, na = 0, nb = 0;
    for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
    return dot / Math.Sqrt(na * nb);
}

static double MinCosine(List<float[]> actual, List<float[]> expected) =>
    Math.Round(actual.Select((e, i) => Cosine(e, expected[i])).Min(), 5);

static int Best(float[] query, List<float[]> candidates) =>
    candidates.Select((c, i) => (score: Cosine(query, c), i)).MaxBy(x => x.score).i;

static object Stats(List<double> values)
{
    var sorted = values.Order().ToList();
    return new
    {
        median = Math.Round(sorted[sorted.Count / 2], 1),
        p95 = Math.Round(sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1], 1),
    };
}

internal static class Indented
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
