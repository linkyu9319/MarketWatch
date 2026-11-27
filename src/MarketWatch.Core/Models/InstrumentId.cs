using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketWatch.Core.Models;

/// <summary>
/// Canonical instrument identifier combining Market + AssetType + Code
/// to prevent collisions across markets and between indices and stocks.
/// Format: "{Market}:{AssetType}:{Code}", e.g. "HK:INDEX:HSTECH", "CN:INDEX:SH000001".
/// </summary>
[JsonConverter(typeof(InstrumentIdJsonConverter))]
public readonly record struct InstrumentId : IComparable<InstrumentId>
{
    public Market Market { get; }
    public AssetType AssetType { get; }
    public string Code { get; }

    public InstrumentId(Market market, AssetType assetType, string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Market = market;
        AssetType = assetType;
        Code = code.Trim().ToUpperInvariant();
    }

    public string CanonicalKey => $"{Market}:{AssetType.ToString().ToUpperInvariant()}:{Code}";

    public override string ToString() => CanonicalKey;

    public int CompareTo(InstrumentId other) =>
        string.Compare(CanonicalKey, other.CanonicalKey, StringComparison.Ordinal);

    public static InstrumentId Parse(string value)
    {
        if (!TryParse(value, out var result))
        {
            throw new FormatException($"Invalid InstrumentId format: '{value}'. Expected 'MARKET:ASSETTYPE:CODE'.");
        }
        return result;
    }

    public static bool TryParse(string? value, out InstrumentId result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return false;

        if (!Enum.TryParse<Market>(parts[0], true, out var market))
            return false;
        if (!Enum.TryParse<AssetType>(parts[1], true, out var assetType))
            return false;
        if (string.IsNullOrWhiteSpace(parts[2]))
            return false;

        result = new InstrumentId(market, assetType, parts[2]);
        return true;
    }
}

public sealed class InstrumentIdJsonConverter : JsonConverter<InstrumentId>
{
    public override InstrumentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (InstrumentId.TryParse(raw, out var id))
            return id;
        throw new JsonException($"Invalid InstrumentId JSON value: '{raw}'.");
    }

    public override void Write(Utf8JsonWriter writer, InstrumentId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.CanonicalKey);
    }
}
