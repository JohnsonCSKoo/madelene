using System.Buffers.Binary;
using Madelene.Model;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Madelene.Mongo;

/// <summary>Lossless mapping between BSON <c>_id</c> values and Madelene's opaque string ids.</summary>
public static class IdCodec
{
    public static string Encode(BsonValue id) => id.BsonType switch
    {
        BsonType.ObjectId => "o:" + id.AsObjectId.ToString(),
        BsonType.String => "s:" + id.AsString,
        BsonType.Int32 => "i:" + id.AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BsonType.Int64 => "l:" + id.AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "j:" + new BsonDocument("v", id).ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson }),
    };

    public static BsonValue Decode(string id) => id[..2] switch
    {
        "o:" => ObjectId.Parse(id[2..]),
        "s:" => new BsonString(id[2..]),
        "i:" => new BsonInt32(int.Parse(id[2..], System.Globalization.CultureInfo.InvariantCulture)),
        "l:" => new BsonInt64(long.Parse(id[2..], System.Globalization.CultureInfo.InvariantCulture)),
        "j:" => BsonDocument.Parse(id[2..])["v"],
        _ => throw new FormatException($"'{id}' is not a Madelene-encoded id."),
    };
}

/// <summary>Extracts vectors and filter values from BSON documents using MongoDB dotted-path semantics.</summary>
public static class BsonFields
{
    /// <summary>All leaf values at a dotted path. Arrays are traversed (multikey semantics) and flattened.</summary>
    public static List<BsonValue> Values(BsonDocument document, string path)
    {
        var result = new List<BsonValue>();
        Collect(document, path.Split('.'), 0, result);
        return result;
    }

    private static void Collect(BsonValue current, string[] parts, int index, List<BsonValue> result)
    {
        if (index == parts.Length)
        {
            if (current is BsonArray leafArray)
                result.AddRange(leafArray.Where(v => !v.IsBsonArray));
            else
                result.Add(current);
            return;
        }

        switch (current)
        {
            case BsonDocument doc when doc.TryGetValue(parts[index], out var next):
                Collect(next, parts, index + 1, result);
                break;
            case BsonArray array:
                foreach (var element in array)
                    if (element is BsonDocument)
                        Collect(element, parts, index, result);
                break;
        }
    }

    /// <summary>Maps a BSON scalar to a filter value; returns null for types that are not filterable.</summary>
    public static FieldValue? ToFieldValue(BsonValue value) => value.BsonType switch
    {
        BsonType.String => FieldValue.String(value.AsString),
        BsonType.Int32 => FieldValue.Num(value.AsInt32),
        BsonType.Int64 => FieldValue.Num(value.AsInt64),
        BsonType.Double => FieldValue.Num(value.AsDouble),
        BsonType.Decimal128 => Decimal128.ToDouble(value.AsDecimal128) is var d && double.IsFinite(d) ? FieldValue.Num(d) : null,
        BsonType.Boolean => FieldValue.Bool(value.AsBoolean),
        BsonType.ObjectId => FieldValue.ObjectId(value.AsObjectId.ToString()),
        BsonType.DateTime => FieldValue.DateMs(value.AsBsonDateTime.MillisecondsSinceEpoch), // no DateTime range limits
        BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard =>
            FieldValue.Uuid(value.AsBsonBinaryData.ToGuid(GuidRepresentation.Standard)),
        _ => null,
    };

    /// <summary>
    /// Reads a vector stored as an array of numbers or as BSON BinData subtype 9 (float32 or int8).
    /// Returns null if the path holds no vector.
    /// </summary>
    public static float[]? Vector(BsonDocument document, string path)
    {
        var current = (BsonValue)document;
        foreach (var part in path.Split('.'))
        {
            if (current is not BsonDocument doc || !doc.TryGetValue(part, out current!))
                return null;
        }

        switch (current)
        {
            case BsonArray array when array.Count > 0 && array.All(v => v.IsNumeric):
                return array.Select(v => (float)v.ToDouble()).ToArray();
            case BsonBinaryData { SubType: BsonBinarySubType.Vector } binary:
                return DecodeBinaryVector(binary.Bytes);
            default:
                return null;
        }
    }

    internal static float[] DecodeBinaryVector(byte[] bytes)
    {
        if (bytes.Length < 2)
            throw new FormatException("BSON vector is too short.");
        var data = bytes.AsSpan(2);
        switch (bytes[0])
        {
            case 0x27: // float32
                var floats = new float[data.Length / 4];
                for (var i = 0; i < floats.Length; i++)
                    floats[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4));
                return floats;
            case 0x03: // int8
                return data.ToArray().Select(b => (float)(sbyte)b).ToArray();
            default:
                throw new NotSupportedException($"BSON vector dtype 0x{bytes[0]:X2} is not supported (float32 and int8 are).");
        }
    }
}
