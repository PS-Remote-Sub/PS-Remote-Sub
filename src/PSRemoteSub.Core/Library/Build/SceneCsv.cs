using System.Text;

namespace PSRemoteSub.Core.Library.Build;

/// <summary>One dialogue row of dialogue_by_scene.csv (only the columns the library build uses).</summary>
public sealed record SceneRow(string Part, string SceneId, string Speaker, string Text);

/// <summary>RFC 4180 reader for the extracted game dialogue CSV.</summary>
public static class SceneCsv
{
    public static IEnumerable<SceneRow> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        using var records = Records(reader).GetEnumerator();
        if (!records.MoveNext()) yield break;
        var header = records.Current;
        if (header.Count > 0) header[0] = header[0].TrimStart('﻿');
        int part = Column(header, "part"), scene = Column(header, "scene_id"), speaker = Column(header, "speaker"), text = Column(header, "text");
        var need = Math.Max(Math.Max(part, scene), Math.Max(speaker, text));
        while (records.MoveNext())
        {
            var r = records.Current;
            if (r.Count == 1 && r[0].Length == 0) continue; // blank line
            if (r.Count <= need) throw new InvalidDataException($"CSV record has {r.Count} fields, needs {need + 1}");
            yield return new SceneRow(r[part], r[scene], r[speaker], r[text]);
        }
    }

    private static int Column(List<string> header, string name)
    {
        var i = header.FindIndex(h => string.Equals(h.Trim(), name, StringComparison.Ordinal));
        return i >= 0 ? i : throw new InvalidDataException($"CSV header has no '{name}' column");
    }

    private static IEnumerable<List<string>> Records(TextReader reader)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            any = true;
            char ch = (char)c;
            if (quoted)
            {
                if (ch != '"') { sb.Append(ch); continue; }
                if (reader.Peek() == '"') { reader.Read(); sb.Append('"'); }
                else quoted = false;
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': fields.Add(sb.ToString()); sb.Clear(); break;
                case '\r': break;
                case '\n':
                    fields.Add(sb.ToString()); sb.Clear();
                    yield return fields;
                    fields = new List<string>();
                    any = false;
                    break;
                default: sb.Append(ch); break;
            }
        }
        if (any)
        {
            fields.Add(sb.ToString());
            yield return fields;
        }
    }
}
