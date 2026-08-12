using System.Text.Json;

namespace Nama.Providers;

/// <summary>
/// Forgiving JSON accessors. Providers change their response shapes without warning, and
/// a missing or unexpectedly-typed field should cost one property, not the whole search.
/// </summary>
internal static class JsonExtensions
{
    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : null;

    public static string? String(this JsonElement element, string name)
    {
        var value = element.Prop(name);
        return value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
    }

    public static int? Int(this JsonElement element, string name)
    {
        var value = element.Prop(name);
        if (value is null) return null;

        return value.Value.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetInt32(out var number) => number,
            // Steam returns some numeric fields as strings ("metascore": "94").
            JsonValueKind.String when int.TryParse(value.Value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    public static double? Double(this JsonElement element, string name)
    {
        var value = element.Prop(name);
        if (value is null) return null;

        return value.Value.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.Value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    public static bool Bool(this JsonElement element, string name) =>
        element.Prop(name)?.ValueKind == JsonValueKind.True;

    /// <summary>Enumerates an array property, yielding nothing when it is absent or not an array.</summary>
    public static IEnumerable<JsonElement> Array(this JsonElement element, string name)
    {
        var value = element.Prop(name);
        if (value?.ValueKind != JsonValueKind.Array) return [];

        return value.Value.EnumerateArray();
    }

    /// <summary>Collects the named string field from each object in an array property.</summary>
    public static List<string> StringsFrom(this JsonElement element, string arrayName, string field)
    {
        var values = new List<string>();
        foreach (var item in element.Array(arrayName))
        {
            if (item.String(field) is { Length: > 0 } value) values.Add(value);
        }

        return values;
    }

    /// <summary>Collects a string array property directly (e.g. Steam's "developers": [...]).</summary>
    public static List<string> Strings(this JsonElement element, string arrayName)
    {
        var values = new List<string>();
        foreach (var item in element.Array(arrayName))
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value) values.Add(value);
        }

        return values;
    }

    /// <summary>
    /// Parses the release-date spellings Nama actually encounters: ISO from VNDB
    /// ("2010-03-26", sometimes partial like "2010" or "2010-03") and Steam's localized
    /// long form ("Feb 24, 2022").
    /// </summary>
    public static DateOnly? ParseReleaseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();

        if (DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var exact)) return exact;

        // VNDB uses "9999" and "TBA" for unreleased titles; those carry no date.
        if (text.StartsWith("9999", StringComparison.Ordinal)) return null;

        var parts = text.Split('-');
        if (parts.Length >= 1 && int.TryParse(parts[0], out var year) && year is > 1900 and < 2200)
        {
            var month = parts.Length > 1 && int.TryParse(parts[1], out var m) && m is >= 1 and <= 12 ? m : 1;
            return new DateOnly(year, month, 1);
        }

        return null;
    }
}
