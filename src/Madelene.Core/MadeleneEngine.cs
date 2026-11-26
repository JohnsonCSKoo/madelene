using System.Collections.Concurrent;
using System.Text;
using Madelene.Search;
using Madelene.Storage;

namespace Madelene;

/// <summary>
/// A directory of vector indexes. Embed one per process; it is thread-safe.
/// <para>
/// Each index lives in its own directory <c>&lt;escaped name&gt;~&lt;id&gt;</c>, so re-creating an index never
/// collides with the files of a dropped one that are still being deleted (Windows can't delete or rename
/// files that an in-flight query or compaction still has memory mapped).
/// </para>
/// </summary>
public sealed class MadeleneEngine : IDisposable
{
    private const char InstanceSeparator = '~'; // '~' is %-escaped in encoded names, so it can't occur in them
    private const string DroppedMarker = "DROPPED";

    private readonly ConcurrentDictionary<string, VectorIndex> _indexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Directory, Exception Error)> _failed = new(StringComparer.Ordinal);
    private readonly object _lifecycle = new();

    public MadeleneEngine(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
        {
            if (File.Exists(Path.Combine(dir, DroppedMarker)) || dir.EndsWith("~dropped", StringComparison.Ordinal))
            {
                TryDeleteDirectory(dir); // finish a drop that could not delete everything
                continue;
            }

            var leaf = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, VectorIndex.DefinitionFile)))
            {
                if (leaf.Contains(InstanceSeparator))
                    TryDeleteDirectory(dir); // an instance directory whose creation never finished
                continue;
            }

            var separator = leaf.IndexOf(InstanceSeparator);
            var name = DecodeName(separator >= 0 ? leaf[..separator] : leaf);
            try
            {
                var index = VectorIndex.Open(dir, name);
                if (!_indexes.TryAdd(name, index))
                {
                    index.Dispose();
                    _failed[name] = (dir, new InvalidDataException($"More than one directory holds index '{name}'."));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
            {
                // One corrupt index must not take the others down. MongoDB is the source of truth:
                // drop it (DropIndex) and rebuild it from there.
                _failed[name] = (dir, ex);
            }
        }
    }

    public string RootDirectory { get; }

    public IReadOnlyCollection<string> IndexNames => _indexes.Keys.ToList();

    /// <summary>Indexes that exist on disk but could not be opened (corrupt or unreadable), with the reason.</summary>
    public IReadOnlyDictionary<string, Exception> FailedIndexes => _failed.ToDictionary(kv => kv.Key, kv => kv.Value.Error);

    public bool TryGetIndex(string name, out VectorIndex index) => _indexes.TryGetValue(name, out index!);

    public VectorIndex GetIndex(string name) =>
        _indexes.TryGetValue(name, out var index) ? index : throw new KeyNotFoundException($"Vector index '{name}' does not exist.");

    /// <summary>Creates the index, or returns the existing one if its definition is identical.</summary>
    public VectorIndex CreateIndex(VectorIndexDefinition definition)
    {
        lock (_lifecycle)
        {
            if (_indexes.TryGetValue(definition.Name, out var existing))
            {
                if (existing.Definition.ToJson() != definition.ToJson())
                    throw new InvalidOperationException($"Index '{definition.Name}' exists with a different definition. Drop it first.");
                return existing;
            }

            if (_failed.ContainsKey(definition.Name))
                throw new InvalidOperationException($"Index '{definition.Name}' failed to open; drop it before re-creating it.");

            var dir = Path.Combine(RootDirectory, EncodeName(definition.Name) + InstanceSeparator + Guid.NewGuid().ToString("N")[..8]);
            var index = VectorIndex.Create(dir, definition);
            _indexes[definition.Name] = index;
            return index;
        }
    }

    /// <summary>
    /// Drops an index (also one listed in <see cref="FailedIndexes"/>). The directory is marked dropped
    /// first, so even if some files are still locked it is gone for good; leftovers are deleted at next start.
    /// </summary>
    public bool DropIndex(string name)
    {
        lock (_lifecycle)
        {
            string dir;
            if (_indexes.TryRemove(name, out var index))
            {
                dir = index.Directory;
                index.Dispose();
            }
            else if (_failed.TryRemove(name, out var failed))
            {
                dir = failed.Directory;
            }
            else
            {
                return false;
            }

            BinaryFiles.WriteDurable(Path.Combine(dir, DroppedMarker), "dropped"u8);
            TryDeleteDirectory(dir);
            return true;
        }
    }

    /// <summary>
    /// Deletes everything except the DROPPED marker first and the marker last, so a directory that can't be
    /// fully deleted yet (files still memory mapped) stays recognisable and is finished at next start.
    /// </summary>
    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            FileRetry.Run(() =>
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    Directory.Delete(sub, recursive: true);
                foreach (var file in Directory.EnumerateFiles(dir))
                    if (!string.Equals(Path.GetFileName(file), DroppedMarker, StringComparison.Ordinal))
                        File.Delete(file);
                Directory.Delete(dir, recursive: true);
            }, attempts: 10);
        }
        catch (IOException)
        {
            // still memory mapped by an in-flight query or compaction; finished at next start
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Routes a <c>$vectorSearch</c> to its index by name.</summary>
    public VectorSearchResult Search(VectorSearchRequest request) => GetIndex(request.Index).Search(request);

    public VectorSearchResult Search(string vectorSearchJson) => Search(VectorSearchRequest.FromJson(vectorSearchJson));

    public void Dispose()
    {
        foreach (var index in _indexes.Values)
            index.Dispose();
        _indexes.Clear();
    }

    /// <summary>Index names become directory names; anything outside [A-Za-z0-9._-] is %-escaped.</summary>
    internal static string EncodeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }

        return sb.ToString();
    }

    internal static string DecodeName(string encoded)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length)
            {
                bytes.Add(Convert.ToByte(encoded.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)encoded[i]);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
