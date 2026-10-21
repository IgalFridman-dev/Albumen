using System.Text;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using static System.FormattableString;

namespace Albumen.Corpus;

// The corpus measured against its labels: the hash rules (Look-alike, Maybe similar), the
// embedding cosine for E4-S6's Maybe-similar threshold, and the E3-S3 blur evidence. Same
// labels and features, same text: nothing here depends on time, culture or machine.
internal static class CorpusReport
{
    private static readonly double[] BlurrierFactors = [2, 2.5, 3, 4, 5, 6];
    private const int ListedPairs = 10;

    private sealed record Pair(int A, int B, string? Kind, NearRelationshipKind Hash, double Cosine)
    {
        public bool Same => Kind is not null;
    }

    public static string Write(
        string version, string code, string model, IReadOnlyList<CorpusPhoto> photos, IReadOnlyList<CorpusFeatures> features)
    {
        var pairs = new List<Pair>();
        for (var a = 0; a < photos.Count; a++)
        for (var b = a + 1; b < photos.Count; b++)
        {
            var same = photos[a].Set.Length > 0 && photos[a].Set == photos[b].Set;
            pairs.Add(new(a, b, same ? photos[a].Kind : null,
                NearDuplicateThresholds.Classify(features[a].Signature, features[b].Signature).Kind,
                Cosine(features[a].Embedding, features[b].Embedding)));
        }

        var sets = photos.Select((photo, index) => (photo, index)).Where(entry => entry.photo.Set.Length > 0)
            .GroupBy(entry => entry.photo.Set, entry => entry.index).OrderBy(set => set.Key, StringComparer.Ordinal).ToList();
        var report = new StringBuilder();
        void Line(string text = "") => report.Append(text).Append('\n');

        Line(Invariant($"# Albumen corpus report {version}"));
        Line();
        Line(Invariant($"- Corpus `{version}` (the first 12 hex digits of the SHA-256 of `labels.csv`): {photos.Count} photos, {sets.Count} sets, {photos.Count(photo => photo.Set.Length == 0)} single photos."));
        Line($"- Code: Albumen.Corpus {code}. Model: {model}.");
        Line("- Pairs are scored one at a time. The app also needs every photo of a set to match every other one, so whole-set results can be lower.");
        Line();
        Line("| Kind | Sets | Photos | sharp | obvious-blur | ambiguous | not-applicable |");
        Line("|---|---|---|---|---|---|---|");
        foreach (var kind in CorpusLabels.Kinds.Append(CorpusLabels.SingleFolder))
        {
            var members = photos.Where(photo => (photo.Kind ?? CorpusLabels.SingleFolder) == kind).ToList();
            var setCount = kind == CorpusLabels.SingleFolder ? "–" : Invariant($"{sets.Count(set => photos[set.First()].Kind == kind)}");
            Line(Invariant($"| {kind} | {setCount} | {members.Count} | ")
                + string.Join(" | ", CorpusLabels.BlurSlices.Select(slice => Invariant($"{members.Count(photo => photo.Blur == slice)}"))) + " |");
        }

        Line();
        Line("## Hash rules (Look-alike and Maybe similar today)");
        Line();
        Line(Invariant($"{pairs.Count(pair => pair.Same)} same-set pairs, {pairs.Count(pair => !pair.Same)} other pairs. Recall per kind is over that kind's same-set pairs."));
        Line();
        Line(PairHeader("Rule"));
        Line(PairDivider());
        Line(PairRow("Look-alike (hash Near)", pairs, pair => pair.Hash == NearRelationshipKind.Near));
        Line(PairRow("Maybe similar (hash Uncertain)", pairs, pair => pair.Hash == NearRelationshipKind.Uncertain));
        Line(PairRow("Either", pairs, pair => pair.Hash != NearRelationshipKind.NotRelated));

        Line();
        Line("## Maybe similar with embeddings (E4-S6, decision D3)");
        Line();
        Line("Maybe similar = hash Uncertain, or cosine at or above the threshold; a pair the hash calls Look-alike stays Look-alike. "
            + "\"Either recall\" counts Look-alike and Maybe similar together.");
        Line();
        Line(PairHeader("Cosine ≥") + " Either recall |");
        Line(PairDivider() + "---|");
        for (var hundredths = 70; hundredths <= 99; hundredths++)
        {
            var threshold = hundredths / 100.0;
            bool Maybe(Pair pair) => pair.Hash == NearRelationshipKind.Uncertain
                || (pair.Hash == NearRelationshipKind.NotRelated && pair.Cosine >= threshold);
            Line(PairRow(Invariant($"{threshold:0.00}"), pairs, Maybe)
                + Invariant($" {Percent(pairs.Count(pair => pair.Same && (pair.Hash == NearRelationshipKind.Near || Maybe(pair))), pairs.Count(pair => pair.Same))} |"));
        }

        var sameCosines = pairs.Where(pair => pair.Same).Select(pair => pair.Cosine).Order().ToList();
        var otherCosines = pairs.Where(pair => !pair.Same).Select(pair => pair.Cosine).Order().ToList();
        Line();
        Line(Invariant($"Cosine of same-set pairs: lowest {At(sameCosines, 0)}, 10th percentile {At(sameCosines, 0.1)}, median {At(sameCosines, 0.5)}. ")
            + Invariant($"Other pairs: median {At(otherCosines, 0.5)}, 99th percentile {At(otherCosines, 0.99)}, highest {At(otherCosines, 1)}."));
        Line();
        Line("Closest pairs that are not one set:");
        Line();
        PairList(pairs.Where(pair => !pair.Same).OrderByDescending(pair => pair.Cosine).ThenBy(pair => pair.A).ThenBy(pair => pair.B));
        Line();
        Line("Farthest pairs of one set:");
        Line();
        PairList(pairs.Where(pair => pair.Same).OrderBy(pair => pair.Cosine).ThenBy(pair => pair.A).ThenBy(pair => pair.B));

        Line();
        Line("## Blur evidence (E3-S3)");
        Line();
        Line(Invariant($"Compared within each labelled set, as Review compares within a set. Shipped constants: no claim when the sharpest photo scores under {SharpnessComparison.MinimumDetail}, ")
            + Invariant($"\"blurrier\" at {SharpnessComparison.BlurrierFactor}× or more below the sharpest, \"sharpest\" at {SharpnessComparison.ClearMargin}× above the next. ")
            + Invariant($"{features.Count(feature => feature.Sharpness is null)} photos are smaller than the 1,024-pixel measuring size and get no score."));
        Line();
        var blurrier = sets.SelectMany(set => SharpnessComparison.Blurrier(
            set.Select(index => ((long)index, features[index].Sharpness)).ToList())).Select(index => (int)index).ToHashSet();
        var obvious = photos.Count(photo => photo.Set.Length > 0 && photo.Blur == CorpusLabels.ObviousBlur);
        Line(Invariant($"- Called blurrier: {blurrier.Count}; of those labelled obvious-blur {blurrier.Count(index => photos[index].Blur == CorpusLabels.ObviousBlur)}, ")
            + string.Join(", ", CorpusLabels.BlurSlices.Where(slice => slice != CorpusLabels.ObviousBlur)
                .Select(slice => Invariant($"{slice} {blurrier.Count(index => photos[index].Blur == slice)}"))) + ".");
        Line(Invariant($"- Obvious-blur relevance (share of \"blurrier\" calls that are obvious blur; target 90%): {Percent(blurrier.Count(index => photos[index].Blur == CorpusLabels.ObviousBlur), blurrier.Count)}."));
        Line(Invariant($"- Obvious-blur photos in sets called blurrier: {Percent(blurrier.Count(index => photos[index].Blur == CorpusLabels.ObviousBlur), obvious)} of {obvious}."));
        Line(Invariant($"- Ambiguous photos in sets called blurrier: {blurrier.Count(index => photos[index].Blur == CorpusLabels.Ambiguous)} of {photos.Count(photo => photo.Set.Length > 0 && photo.Blur == CorpusLabels.Ambiguous)}."));
        var sharpest = sets.Select(set => SharpnessComparison.Sharpest(
            set.Select(index => ((long)index, features[index].Sharpness)).ToList())).OfType<long>().Select(index => (int)index).ToList();
        Line(Invariant($"- Sets with a \"sharpest\" photo: {sharpest.Count} of {sets.Count}; that photo is labelled sharp in {sharpest.Count(index => photos[index].Blur == CorpusLabels.Sharp)}."));
        Line();
        Line("| Blurrier at | Called | Obvious-blur | Relevance | Obvious-blur recall |");
        Line("|---|---|---|---|---|");
        foreach (var factor in BlurrierFactors)
        {
            var called = sets.SelectMany(set => Blurrier(set, features, factor)).ToList();
            var hits = called.Count(index => photos[index].Blur == CorpusLabels.ObviousBlur);
            Line(Invariant($"| {factor}× | {called.Count} | {hits} | {Percent(hits, called.Count)} | {Percent(hits, obvious)} |"));
        }

        Line();
        Line("Sets with a photo not labelled sharp (score, and how many times below the set's sharpest):");
        Line();
        Line("| Photo | Label | Score | Below sharpest | Called blurrier |");
        Line("|---|---|---|---|---|");
        foreach (var set in sets.Where(set => set.Any(index => photos[index].Blur != CorpusLabels.Sharp)))
        {
            var best = set.Max(index => features[index].Sharpness) ?? 0;
            foreach (var index in set)
                Line(features[index].Sharpness is { } score
                    ? Invariant($"| {photos[index].Path} | {photos[index].Blur} | {score:0.0} | {(score > 0 ? best / score : double.PositiveInfinity):0.0}× | {(blurrier.Contains(index) ? "yes" : "no")} |")
                    : Invariant($"| {photos[index].Path} | {photos[index].Blur} | – | – | no |"));
        }

        return report.ToString();

        void PairList(IEnumerable<Pair> ordered)
        {
            Line("| Photo | Photo | Cosine | Hash |");
            Line("|---|---|---|---|");
            foreach (var pair in ordered.Take(ListedPairs))
                Line(Invariant($"| {photos[pair.A].Path} | {photos[pair.B].Path} | {pair.Cosine:0.000} | {pair.Hash} |"));
        }
    }

    // SharpnessComparison.Blurrier with the factor as a parameter, for the sweep.
    private static IEnumerable<int> Blurrier(IEnumerable<int> set, IReadOnlyList<CorpusFeatures> features, double factor)
    {
        var measured = set.Where(index => features[index].Sharpness is not null).ToList();
        var best = measured.Count < 2 ? 0 : measured.Max(index => features[index].Sharpness!.Value);
        return best < SharpnessComparison.MinimumDetail
            ? Enumerable.Empty<int>()
            : measured.Where(index => features[index].Sharpness!.Value * factor <= best);
    }

    private static string PairHeader(string first) =>
        $"| {first} | Pairs called | Correct | Precision | Recall | " + string.Join(" | ", CorpusLabels.Kinds) + " |";

    private static string PairDivider() => "|---|---|---|---|---|" + string.Concat(CorpusLabels.Kinds.Select(_ => "---|"));

    private static string PairRow(string name, List<Pair> pairs, Func<Pair, bool> called)
    {
        var count = pairs.Count(called);
        var correct = pairs.Count(pair => pair.Same && called(pair));
        return Invariant($"| {name} | {count} | {correct} | {Percent(correct, count)} | {Percent(correct, pairs.Count(pair => pair.Same))} | ")
            + string.Join(" | ", CorpusLabels.Kinds.Select(kind =>
                Percent(pairs.Count(pair => pair.Kind == kind && called(pair)), pairs.Count(pair => pair.Kind == kind)))) + " |";
    }

    private static string Percent(int part, int whole) => whole == 0 ? "–" : Invariant($"{100.0 * part / whole:0.0}%");

    private static string At(List<double> sorted, double quantile) =>
        sorted.Count == 0 ? "–" : Invariant($"{sorted[(int)Math.Round(quantile * (sorted.Count - 1))]:0.000}");

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0;
        for (var i = 0; i < left.Length; i++)
            dot += (double)left[i] * right[i];
        return dot;
    }
}
