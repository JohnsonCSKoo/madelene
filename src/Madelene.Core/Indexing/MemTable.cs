using Madelene.Filtering;
using Madelene.Model;
using Madelene.Numerics;
using Madelene.Storage;

namespace Madelene.Indexing;

/// <summary>
/// Mutable in-memory buffer of recent writes, searched exactly with float32 vectors.
/// Sealed into an immutable segment when full or at a checkpoint.
/// Not persisted: after a crash the host replays its change feed from the last checkpoint.
/// </summary>
internal sealed class MemTable
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly List<MemRow> _rows = [];
    private readonly Dictionary<string, int> _byId = new(StringComparer.Ordinal);
    private int _live;

    public int LiveCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _live; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void Upsert(MemRow row)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_byId.TryGetValue(row.Id, out var existing) && !_rows[existing].Deleted)
            {
                _rows[existing].Deleted = true;
                _live--;
            }

            _byId[row.Id] = _rows.Count;
            _rows.Add(row);
            _live++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Delete(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_byId.Remove(id, out var slot) || _rows[slot].Deleted)
                return false;
            _rows[slot].Deleted = true;
            _live--;
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool Contains(string id)
    {
        _lock.EnterReadLock();
        try { return _byId.TryGetValue(id, out var slot) && !_rows[slot].Deleted; }
        finally { _lock.ExitReadLock(); }
    }

    public int DeleteWhere(Func<MemRow, bool> predicate)
    {
        _lock.EnterWriteLock();
        try
        {
            var removed = 0;
            foreach (var row in _rows)
            {
                if (row.Deleted || !predicate(row))
                    continue;
                row.Deleted = true;
                _byId.Remove(row.Id);
                _live--;
                removed++;
            }

            return removed;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Live rows (snapshot), used when sealing.</summary>
    public List<MemRow> LiveRows()
    {
        _lock.EnterReadLock();
        try { return _rows.Where(r => !r.Deleted).ToList(); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Exact search over live rows. Scores are raw inner products.</summary>
    public void Search(ReadOnlySpan<float> query, FilterNode filter, string? timePath, TopK results, ref int matched)
    {
        var q = query.ToArray();
        _lock.EnterReadLock();
        try
        {
            if (_rows.Count == 0)
                return;
            var local = 0;
            var sync = new object();
            Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(0, _rows.Count, 4096),
                () => new TopK(results.Capacity),
                (range, _, top) =>
                {
                    var count = 0;
                    for (var i = range.Item1; i < range.Item2; i++)
                    {
                        var row = _rows[i];
                        if (row.Deleted || !DocumentFilterEvaluator.Matches(filter, row.Fields, timePath, row.TimeOrNull))
                            continue;
                        count++;
                        top.Add(VectorMath.Dot(q, row.Vector), new Candidate(row));
                    }

                    Interlocked.Add(ref local, count);
                    return top;
                },
                top =>
                {
                    lock (sync)
                        results.Merge(top);
                });
            matched += local;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}

internal sealed class MemRow
{
    public required string Id { get; init; }
    public required float[] Vector { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<FieldValue>> Fields { get; init; }
    public required IReadOnlyList<string> Terms { get; init; }
    public long Time { get; init; } = long.MinValue;
    public long? TimeOrNull => Time == long.MinValue ? null : Time;
    public bool Deleted { get; set; }

    public BuildRow ToBuildRow() => new() { Id = Id, Vector = Vector, Terms = Terms, Time = Time };
}
