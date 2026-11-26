namespace Madelene.Model;

/// <summary>
/// One record as Madelene sees it: an opaque id, the vector, the filter field values and the optional
/// time value. Hosts (e.g. the MongoDB connector) map their documents into this shape.
/// </summary>
public sealed class IndexDocument
{
    public IndexDocument(string id, float[] vector, IReadOnlyDictionary<string, IReadOnlyList<FieldValue>>? fields = null, long? timeUnixMs = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(vector);
        Id = id;
        Vector = vector;
        Fields = fields ?? new Dictionary<string, IReadOnlyList<FieldValue>>();
        TimeUnixMs = timeUnixMs;
    }

    /// <summary>Opaque, unique document id. The MongoDB connector encodes BSON <c>_id</c> values into it.</summary>
    public string Id { get; }

    public float[] Vector { get; }

    /// <summary>Filter field path to its values. Multi-valued (array) fields have more than one value.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FieldValue>> Fields { get; }

    /// <summary>
    /// The configured time field as Unix milliseconds (see <see cref="FieldValue.ToUnixMs"/>), if present.
    /// Used for segment bucketing, range pruning and retention.
    /// </summary>
    public long? TimeUnixMs { get; }
}
