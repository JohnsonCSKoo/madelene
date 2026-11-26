namespace Madelene.Filtering;

/// <summary>
/// Thrown when a filter uses an operator, value type or field outside the supported subset.
/// Hosts should treat it as "route this query elsewhere" (e.g. fall back to MongoDB's own $vectorSearch).
/// </summary>
public sealed class FilterNotSupportedException(string message) : Exception(message);

/// <summary>Thrown for malformed $vectorSearch requests (the equivalent of a MongoDB query error).</summary>
public sealed class VectorSearchRequestException(string message) : Exception(message);
