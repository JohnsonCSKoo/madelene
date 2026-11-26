using Madelene.Storage;

namespace Madelene.Indexing;

/// <summary>A search hit before it is resolved to an id: a segment row or a memtable row.</summary>
internal readonly record struct Candidate(Segment? Segment, int Row, MemRow? Mem)
{
    public Candidate(MemRow mem) : this(null, -1, mem)
    {
    }
}

/// <summary>Bounded "keep the k largest scores" collection.</summary>
internal sealed class TopK
{
    private readonly PriorityQueue<Candidate, float> _heap;

    public TopK(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _heap = new PriorityQueue<Candidate, float>(System.Math.Min(capacity, 1 << 16));
    }

    public int Capacity { get; }

    public int Count => _heap.Count;

    public float MinScore => _heap.Count < Capacity ? float.NegativeInfinity : PeekScore();

    public void Add(float score, Candidate candidate)
    {
        if (float.IsNaN(score))
            return;
        if (_heap.Count < Capacity)
            _heap.Enqueue(candidate, score);
        else if (score > PeekScore())
            _heap.DequeueEnqueue(candidate, score);
    }

    public void Merge(TopK other)
    {
        foreach (var (candidate, score) in other._heap.UnorderedItems)
            Add(score, candidate);
    }

    public List<(Candidate Candidate, float Score)> ToDescending()
    {
        var list = _heap.UnorderedItems.Select(x => (x.Element, x.Priority)).ToList();
        list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return list;
    }

    private float PeekScore()
    {
        _heap.TryPeek(out _, out var score);
        return score;
    }
}
