using System.Text;
using System.Text.Json;

namespace PSRemoteSub.Core.Translation;

public static class GoogleResponseParsers
{
    /// <summary>Parses the gtx response <c>[[["th","src",...],...],...]</c>, joining every segment.</summary>
    public static string ParseGtx(string json)
    {
        using var doc = Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0
            || root[0].ValueKind != JsonValueKind.Array || root[0].GetArrayLength() == 0)
            throw new FormatException("Unexpected gtx response shape.");

        var sb = new StringBuilder();
        foreach (var seg in root[0].EnumerateArray())
        {
            if (seg.ValueKind != JsonValueKind.Array || seg.GetArrayLength() == 0
                || seg[0].ValueKind != JsonValueKind.String)
                throw new FormatException("Unexpected gtx segment shape.");
            sb.Append(seg[0].GetString());
        }
        return sb.ToString();
    }

    /// <summary>Parses the clients5 response, either <c>["th"]</c> or <c>[["th","en"]]</c>.</summary>
    public static string ParseClients5(string json)
    {
        using var doc = Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new FormatException("Unexpected clients5 response shape.");

        var sb = new StringBuilder();
        foreach (var item in root.EnumerateArray())
        {
            var text = item;
            if (text.ValueKind == JsonValueKind.Array && text.GetArrayLength() > 0)
                text = text[0];
            if (text.ValueKind != JsonValueKind.String)
                throw new FormatException("Unexpected clients5 response shape.");
            sb.Append(text.GetString());
        }
        return sb.ToString();
    }

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException e) { throw new FormatException("Response is not JSON.", e); }
    }
}
