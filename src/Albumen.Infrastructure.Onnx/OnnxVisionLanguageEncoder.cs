using System.Security.Cryptography;
using System.Text.Json;
using Albumen.Application.Semantic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Albumen.Infrastructure.Onnx;

/// <summary>
/// SigLIP 2 on ONNX Runtime (CPU), from the files listed in <c>models/manifest.json</c>. Each tower is
/// checksum-verified and opened on first use; one session per tower is shared by all callers.
/// </summary>
public sealed class OnnxVisionLanguageEncoder : IVisionLanguageEncoder, IDisposable
{
    /// <summary>Longest edge of the decode handed to <see cref="EncodeImage"/> (spike: 512 keeps cosine 0.995 at 256's cost).</summary>
    public const int DecodeEdge = 512;

    /// <summary>Bump when <see cref="SiglipPreprocessing"/>, the tensor names or the normalization change.</summary>
    internal const int PipelineRevision = 1;

    private const string PixelInput = "pixel_values";
    private const string TokenInput = "input_ids";
    private const string EmbeddingOutput = "embedding";

    private readonly string modelsDirectory;
    private readonly Lazy<(ModelManifest? Manifest, EncoderProfileResult Result)> installed;
    private readonly Lazy<Tower> vision;
    private readonly Lazy<Tower> text;

    public OnnxVisionLanguageEncoder(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        this.modelsDirectory = modelsDirectory;
        installed = new(Install);
        // Callers embed photos in parallel, one inference thread each (spike: 2.5x the throughput of one photo on all threads).
        vision = new(() => OpenTower(m => m.Files.Vision, PixelInput, m => [3, m.ImageSize, m.ImageSize], intraOpThreads: 1));
        text = new(() => OpenTower(m => m.Files.Text, TokenInput, m => [m.MaxTokens], intraOpThreads: 0));
    }

    public EncoderProfileResult LoadProfile() => installed.Value.Result;

    public EmbeddingResult EncodeImage(EncoderProfile profile, EncoderImage image)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || Math.Max(image.Width, image.Height) > DecodeEdge
            || image.Bgra.Length != image.Width * image.Height * 4)
        {
            throw new ArgumentException("The image must be BGRA32 pixels within the decode edge.", nameof(image));
        }

        if (Prepare(profile, vision) is { } failure)
        {
            return failure;
        }

        var size = profile.ImageSize;
        var pixels = SiglipPreprocessing.Pixels(image.Bgra.Span, image.Width, image.Height, size);
        return Run(vision.Value, PixelInput, new DenseTensor<float>(pixels, [1, 3, size, size]), profile.Dimension);
    }

    public EmbeddingResult EncodeText(EncoderProfile profile, string prompt)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(prompt);
        if (Prepare(profile, text) is { } failure)
        {
            return failure;
        }

        var ids = SiglipPreprocessing.TokenIds(text.Value.Tokenizer!, prompt, profile.MaxTokens, profile.PadId);
        return Run(text.Value, TokenInput, new DenseTensor<long>(ids, [1, profile.MaxTokens]), profile.Dimension);
    }

    /// <summary>Verifies and opens the text tower and its tokenizer, as the first prompt would.</summary>
    public EncoderFailure? PrepareText(EncoderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Prepare(profile, text)?.Failure;
    }

    public void Dispose()
    {
        if (vision.IsValueCreated)
        {
            vision.Value.Session?.Dispose();
        }

        if (text.IsValueCreated)
        {
            text.Value.Session?.Dispose();
        }
    }

    private static EncoderFailure Unavailable(string reason) => new(EncoderFailureKind.Unavailable, reason);

    private static EncoderFailure Failed(string reason) => new(EncoderFailureKind.Failed, reason);

    private static EmbeddingResult Run<T>(Tower tower, string input, DenseTensor<T> tensor, int dimension)
    {
        float[] vector;
        try
        {
            using var results = tower.Session!.Run([NamedOnnxValue.CreateFromTensor(input, tensor)]);
            vector = [.. results[0].AsEnumerable<float>()];
        }
        catch (OnnxRuntimeException exception)
        {
            return new(default, Failed($"The model could not run: {exception.Message}"));
        }

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (vector.Length != dimension || !double.IsFinite(norm) || norm == 0)
        {
            return new(default, Failed("The model returned an unusable vector."));
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return new(vector, null);
    }

    private EmbeddingResult? Prepare(EncoderProfile profile, Lazy<Tower> tower)
    {
        if (LoadProfile() is { Failure: { } notInstalled })
        {
            return new(default, notInstalled);
        }

        if (profile != LoadProfile().Profile)
        {
            return new(default, Failed("The vectors were requested for a different encoder profile."));
        }

        return tower.Value.Failure is { } failure ? new(default, failure) : null;
    }

    private (ModelManifest?, EncoderProfileResult) Install()
    {
        var manifestPath = Path.Combine(modelsDirectory, ModelManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return (null, new(null, Unavailable("The model files are not installed.")));
        }

        ModelManifest manifest;
        try
        {
            manifest = ModelManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, new(null, Failed($"The model manifest is unreadable: {exception.Message}")));
        }

        foreach (var file in manifest.All)
        {
            var path = Path.Combine(modelsDirectory, file.Path);
            if (!File.Exists(path))
            {
                return (null, new(null, Unavailable($"The model file {file.Path} is not installed.")));
            }

            long length;
            try
            {
                using var stream = File.OpenRead(path);
                length = stream.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (null, new(null, Unavailable($"The model file {file.Path} could not be read: {exception.Message}")));
            }

            if (length != file.Length)
            {
                return (null, new(null, Failed($"The model file {file.Path} has the wrong size.")));
            }
        }

        var profile = new EncoderProfile(
            manifest.Model,
            manifest.Files.Vision.Sha256,
            manifest.Files.Text.Sha256,
            manifest.Files.Tokenizer.Sha256,
            DecodeEdge,
            manifest.ImageSize,
            manifest.MaxTokens,
            manifest.PadId,
            manifest.Dimension,
            PipelineRevision,
            manifest.LogitScale,
            manifest.LogitBias);
        return (manifest, new(profile, null));
    }

    private Tower OpenTower(
        Func<ModelManifest, ModelFile> select,
        string input,
        Func<ModelManifest, int[]> inputShape,
        int intraOpThreads)
    {
        var manifest = installed.Value.Manifest!;
        var file = select(manifest);
        var (model, corrupt) = OpenVerified(file);
        if (corrupt is not null)
        {
            return new(null, null, corrupt);
        }

        // The verified file stays open, so it cannot be replaced before the session has read it.
        using (model)
        {
            SentencePieceTokenizer? tokenizer = null;
            if (input == TokenInput)
            {
                var (tokenizerModel, corruptTokenizer) = OpenVerified(manifest.Files.Tokenizer);
                if (corruptTokenizer is not null)
                {
                    return new(null, null, corruptTokenizer);
                }

                try
                {
                    using (tokenizerModel)
                    {
                        tokenizer = SentencePieceTokenizer.Create(tokenizerModel!, addBeginningOfSentence: false, addEndOfSentence: false);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException
                                                      or Google.Protobuf.InvalidProtocolBufferException)
                {
                    return new(null, null, Failed($"The tokenizer could not be loaded: {exception.Message}"));
                }
            }

            InferenceSession session;
            try
            {
                // Albumen sends no telemetry; ONNX Runtime's Windows diagnostic events are on by default.
                OrtEnv.Instance().DisableTelemetryEvents();
                using var options = new SessionOptions { IntraOpNumThreads = intraOpThreads };
                session = new InferenceSession(Path.Combine(modelsDirectory, file.Path), options);
            }
            catch (OnnxRuntimeException exception)
            {
                return new(null, null, Failed($"The model file {file.Path} could not be opened: {exception.Message}"));
            }
            catch (Exception exception) when (exception is TypeInitializationException or DllNotFoundException
                                                  or EntryPointNotFoundException or BadImageFormatException)
            {
                return new(null, null, Failed($"ONNX Runtime could not be loaded: {exception.Message}"));
            }

            return CheckShapes(session, input, inputShape(manifest), manifest.Dimension, file)
                ?? new(session, tokenizer, null);
        }
    }

    private static Tower? CheckShapes(InferenceSession session, string input, int[] expectedInput, int expectedDimension, ModelFile file)
    {
        if (session.InputMetadata.Count != 1
            || !session.InputMetadata.TryGetValue(input, out var inputMetadata)
            || inputMetadata.ElementType != (input == TokenInput ? typeof(long) : typeof(float))
            || !inputMetadata.Dimensions.Skip(1).SequenceEqual(expectedInput)
            || session.OutputMetadata.Count != 1
            || !session.OutputMetadata.TryGetValue(EmbeddingOutput, out var outputMetadata)
            || outputMetadata.ElementType != typeof(float)
            || outputMetadata.Dimensions is not [_, var dimension]
            || dimension != expectedDimension)
        {
            session.Dispose();
            return new(null, null, Failed($"The model file {file.Path} does not have the expected inputs or outputs."));
        }

        return null;
    }

    /// <summary>Opens the file read-only (writers and renames blocked) and returns it rewound if its checksum matches.</summary>
    private (FileStream? Stream, EncoderFailure? Failure) OpenVerified(ModelFile file)
    {
        FileStream? stream = null;
        try
        {
            stream = File.OpenRead(Path.Combine(modelsDirectory, file.Path));
            if (stream.Length == file.Length && Convert.ToHexString(SHA256.HashData(stream)) == file.Sha256)
            {
                stream.Position = 0;
                return (stream, null);
            }

            stream.Dispose();
            return (null, Failed($"The model file {file.Path} does not match its checksum."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            return (null, Unavailable($"The model file {file.Path} could not be read: {exception.Message}"));
        }
    }

    private sealed record Tower(InferenceSession? Session, SentencePieceTokenizer? Tokenizer, EncoderFailure? Failure);
}
