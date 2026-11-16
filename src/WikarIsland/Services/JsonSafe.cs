using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WikarIsland.Services;

/// <summary>
/// JSON, в котором одно поле может прийти дважды (Яндекс Музыка иногда присылает «albums» два раза).
/// JsonNode на таком падает при первом обращении к объекту, поэтому повторы убираем заранее — остаётся первое значение.
/// </summary>
public static class JsonSafe
{
    public static JsonNode? Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(doc.RootElement, writer);
        return JsonNode.Parse(stream.ToArray());
    }

    private static void Write(JsonElement e, Utf8JsonWriter w)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                var seen = new HashSet<string>();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    if (!seen.Add(p.Name)) continue;
                    w.WritePropertyName(p.Name);
                    Write(p.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (JsonElement item in e.EnumerateArray()) Write(item, w);
                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }
}
