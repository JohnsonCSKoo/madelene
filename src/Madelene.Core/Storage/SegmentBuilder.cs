using System.Text;
using Madelene.Numerics;
using Madelene.Quantization;

namespace Madelene.Storage;

/// <summary>
/// Writes an immutable segment: rows are grouped by IVF cluster (one cluster for small segments),
/// encoded as 1-bit codes + int8 rescoring vectors, and given posting lists for every filter term.
/// The directory is written under a temporary name and renamed when complete.
/// </summary>
internal static class SegmentBuilder
{
    private const int ChunkRows = 16_384;

    public static string Build(string indexDirectory, long segmentId, long bucket, IReadOnlyList<BuildRow> rows, VectorIndexDefinition definition, RandomRotation rotation)
    {
        if (rows.Count == 0)
            throw new ArgumentException("Cannot build an empty segment.", nameof(rows));

        var finalDir = Path.Combine(indexDirectory, SegmentDirectoryName(segmentId));
        var tmpDir = finalDir + ".tmp";
        FileRetry.Run(() =>
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
            Directory.CreateDirectory(tmpDir);
        });

        var n = rows.Count;
        var dims = definition.Dimensions;
        var codeBytes = BinaryCodec.CodeBytes(dims);

        // Vectors are decoded per row into scratch buffers (rows carried over by compaction are int8), so a
        // merge holds ~1 KB per row instead of 3 KB of floats. Only the k-means training sample is materialized.

        // 1. Clusters (IVF lists) and the row order they imply.
        float[][] centroids;
        int[] labels;
        if (n >= definition.Options.IvfMinRows)
        {
            var lists = (int)System.Math.Clamp(System.Math.Round(definition.Options.IvfListsPerSqrtRow * System.Math.Sqrt(n)), 2, 65_536);
            var sample = (int)System.Math.Min(n, System.Math.Min(500_000L, lists * 128L));
            centroids = SphericalKMeans.Train(new DecodedVectors(rows, dims), lists, iterations: 10, maxSample: sample, seed: unchecked((int)segmentId * 7919 + 17));
            labels = SphericalKMeans.Assign(n, dims, (i, buffer) => rows[i].CopyVectorTo(buffer), centroids);
        }
        else
        {
            centroids = [Mean(rows, dims)];
            labels = new int[n];
        }

        var clusters = centroids.Length;
        var offsets = new int[clusters + 1];
        foreach (var l in labels)
            offsets[l + 1]++;
        for (var c = 0; c < clusters; c++)
            offsets[c + 1] += offsets[c];
        var cursor = (int[])offsets.Clone();
        var order = new int[n]; // new row -> source row
        for (var i = 0; i < n; i++)
            order[cursor[labels[i]]++] = i;

        // 2. Vector payloads, written in row order chunk by chunk.
        using (var codesFs = Create(tmpDir, SegmentFileNames.Codes))
        using (var factorsFs = Create(tmpDir, SegmentFileNames.Factors))
        using (var vecFs = Create(tmpDir, SegmentFileNames.Vectors))
        using (var scalesFs = Create(tmpDir, SegmentFileNames.Scales))
        {
            var codeChunk = new byte[ChunkRows * codeBytes];
            var factorChunk = new float[ChunkRows];
            var vecChunk = new sbyte[ChunkRows * dims];
            var scaleChunk = new float[ChunkRows];
            for (var start = 0; start < n; start += ChunkRows)
            {
                var count = System.Math.Min(ChunkRows, n - start);
                Parallel.For(0, count, () => (new float[dims], new float[dims], new float[dims]), (i, _, scratch) =>
                {
                    var newRow = start + i;
                    var src = order[newRow];
                    var cluster = labels[src];
                    var v = scratch.Item3;
                    rows[src].CopyVectorTo(v);
                    var residual = scratch.Item1;
                    var rotated = scratch.Item2;
                    var centre = centroids[cluster];
                    for (var d = 0; d < dims; d++)
                        residual[d] = v[d] - centre[d];
                    rotation.Apply(residual, rotated);
                    factorChunk[i] = BinaryCodec.Encode(rotated, codeChunk.AsSpan(i * codeBytes, codeBytes));
                    scaleChunk[i] = Int8Codec.Encode(v, vecChunk.AsSpan(i * dims, dims));
                    return scratch;
                }, _ => { });

                BinaryFiles.Append<byte>(codesFs, codeChunk.AsSpan(0, count * codeBytes));
                BinaryFiles.Append<float>(factorsFs, factorChunk.AsSpan(0, count));
                BinaryFiles.Append<sbyte>(vecFs, vecChunk.AsSpan(0, count * dims));
                BinaryFiles.Append<float>(scalesFs, scaleChunk.AsSpan(0, count));
            }
        }

        var flatCentroids = new float[clusters * dims];
        for (var c = 0; c < clusters; c++)
            centroids[c].CopyTo(flatCentroids, c * dims);
        BinaryFiles.Write<float>(Path.Combine(tmpDir, SegmentFileNames.Centroids), flatCentroids);
        BinaryFiles.Write<int>(Path.Combine(tmpDir, SegmentFileNames.ClusterOffsets), offsets);

        // 3. Ids, id hash index and bloom filter.
        var idOffsets = new long[n + 1];
        var hashes = new ulong[n];
        using (var idFs = Create(tmpDir, SegmentFileNames.IdData))
        {
            long pos = 0;
            for (var r = 0; r < n; r++)
            {
                var id = rows[order[r]].Id;
                var bytes = Encoding.UTF8.GetBytes(id);
                idFs.Write(bytes);
                idOffsets[r] = pos;
                pos += bytes.Length;
                hashes[r] = Hashing.Hash64(id);
            }

            idOffsets[n] = pos;
        }

        BinaryFiles.Write<long>(Path.Combine(tmpDir, SegmentFileNames.IdOffsets), idOffsets);
        var hashRows = Enumerable.Range(0, n).ToArray();
        var sortedHashes = (ulong[])hashes.Clone();
        Array.Sort(sortedHashes, hashRows);
        BinaryFiles.Write<ulong>(Path.Combine(tmpDir, SegmentFileNames.IdHashes), sortedHashes);
        BinaryFiles.Write<int>(Path.Combine(tmpDir, SegmentFileNames.IdHashRows), hashRows);
        BinaryFiles.Write<ulong>(Path.Combine(tmpDir, SegmentFileNames.Bloom), Bloom.Build(hashes, n));

        // 4. Time column.
        long minTime = long.MaxValue, maxTime = long.MinValue;
        var hasTime = definition.TimePath != null;
        if (hasTime)
        {
            var times = new long[n];
            for (var r = 0; r < n; r++)
            {
                var t = rows[order[r]].Time;
                times[r] = t;
                if (t == long.MinValue)
                    continue;
                minTime = System.Math.Min(minTime, t);
                maxTime = System.Math.Max(maxTime, t);
            }

            BinaryFiles.Write<long>(Path.Combine(tmpDir, SegmentFileNames.Time), times);
        }

        if (minTime == long.MaxValue)
            minTime = maxTime = long.MinValue;

        // 5. Posting lists. Dense terms are stored as bitsets, sparse ones as sorted row arrays.
        WritePostings(tmpDir, rows, order, n);

        new SegmentMeta
        {
            Id = segmentId,
            Rows = n,
            Dimensions = dims,
            CodeBytes = codeBytes,
            Clusters = clusters,
            MinTime = minTime,
            MaxTime = maxTime,
            HasTime = hasTime,
            Bucket = bucket,
            CreatedUtc = DateTime.UtcNow,
        }.Save(Path.Combine(tmpDir, SegmentFileNames.Meta));

        FileRetry.Run(() =>
        {
            if (Directory.Exists(finalDir))
                Directory.Delete(finalDir, recursive: true);
            Directory.Move(tmpDir, finalDir);
        });
        return finalDir;
    }

    public static string SegmentDirectoryName(long segmentId) => $"seg-{segmentId:D8}";

    private static void WritePostings(string dir, IReadOnlyList<BuildRow> rows, int[] order, int n)
    {
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var r = 0; r < n; r++)
        {
            foreach (var term in rows[order[r]].Terms)
            {
                if (!postings.TryGetValue(term, out var list))
                    postings[term] = list = [];
                if (list.Count == 0 || list[^1] != r)
                    list.Add(r);
            }
        }

        var terms = postings.Keys.ToArray();
        Array.Sort(terms, StringComparer.Ordinal);
        var termOffsets = new long[terms.Length + 1];
        var postOffsets = new long[terms.Length];
        var postCounts = new int[terms.Length];
        var postKinds = new byte[terms.Length];
        var words = (n + 63) >> 6;

        using (var termFs = Create(dir, SegmentFileNames.TermData))
        using (var postFs = Create(dir, SegmentFileNames.Postings))
        {
            long termPos = 0, postPos = 0;
            var bitset = new ulong[words];
            for (var t = 0; t < terms.Length; t++)
            {
                var bytes = Encoding.UTF8.GetBytes(terms[t]);
                termFs.Write(bytes);
                termOffsets[t] = termPos;
                termPos += bytes.Length;

                var list = postings[terms[t]];
                postOffsets[t] = postPos;
                postCounts[t] = list.Count;
                if ((long)list.Count * 32 > n)
                {
                    postKinds[t] = (byte)PostingKind.Bitset;
                    Array.Clear(bitset);
                    foreach (var row in list)
                        bitset[row >> 6] |= 1UL << row;
                    BinaryFiles.Append<ulong>(postFs, bitset);
                    postPos += words * 8L;
                }
                else
                {
                    postKinds[t] = (byte)PostingKind.Array;
                    BinaryFiles.Append<int>(postFs, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
                    postPos += list.Count * 4L;
                }
            }

            termOffsets[terms.Length] = termPos;
        }

        BinaryFiles.Write<long>(Path.Combine(dir, SegmentFileNames.TermOffsets), termOffsets);
        BinaryFiles.Write<long>(Path.Combine(dir, SegmentFileNames.PostingOffsets), postOffsets);
        BinaryFiles.Write<int>(Path.Combine(dir, SegmentFileNames.PostingCounts), postCounts);
        BinaryFiles.Write<byte>(Path.Combine(dir, SegmentFileNames.PostingKinds), postKinds);
    }

    private static float[] Mean(IReadOnlyList<BuildRow> rows, int dims)
    {
        var sum = new double[dims];
        var v = new float[dims];
        foreach (var row in rows)
        {
            row.CopyVectorTo(v);
            for (var d = 0; d < dims; d++)
                sum[d] += v[d];
        }

        var mean = new float[dims];
        for (var d = 0; d < dims; d++)
            mean[d] = (float)(sum[d] / rows.Count);
        return mean;
    }

    /// <summary>Rows' vectors as a list; each access to an int8 row decodes a fresh array (used for the k-means sample only).</summary>
    private sealed class DecodedVectors(IReadOnlyList<BuildRow> rows, int dims) : IReadOnlyList<float[]>
    {
        public int Count => rows.Count;

        public float[] this[int index]
        {
            get
            {
                if (rows[index].Vector is { } full)
                    return full;
                var v = new float[dims];
                rows[index].CopyVectorTo(v);
                return v;
            }
        }

        public IEnumerator<float[]> GetEnumerator()
        {
            for (var i = 0; i < rows.Count; i++)
                yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static FileStream Create(string dir, string name) => BinaryFiles.CreateDurable(Path.Combine(dir, name));
}

internal enum PostingKind : byte
{
    Array = 1,
    Bitset = 2,
}
