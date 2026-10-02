using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.EdemRefresh;

public static class Pipeline
{
    static readonly Dictionary<string,string> Categories = new(StringComparer.OrdinalIgnoreCase) { ["фильм"]="movie", ["film"]="movie", ["мультфильм"]="kids", ["animation"]="kids", ["детский"]="kids", ["новости"]="news", ["nachrichten"]="news", ["спорт"]="sports", ["sport"]="sports" };
    static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase) { "Программа не найдена", "no programme information", "no program information" };
    static string Key(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();
    static XmlReader Reader(string path) => XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
    static Uri Url(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : throw new InvalidOperationException("Source must be an HTTP or HTTPS URL.");
    public static async Task DownloadGuide(HttpClient client, string url, string output, CancellationToken token)
    {
        using var response = await client.GetAsync(Url(url), HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new IOException($"EPG download returned HTTP {(int)response.StatusCode}.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var prefix = new byte[2];
        await input.ReadExactlyAsync(prefix, token);
        await using var raw = File.Create(output + ".download");
        await raw.WriteAsync(prefix, token);
        await CopyLimited(input, raw, 150_000_000, token);
        await raw.DisposeAsync();
        await using var file = File.OpenRead(output + ".download");
        await using var target = File.Create(output);
        if (prefix[0] == 31 && prefix[1] == 139) { await using var gzip = new GZipStream(file, CompressionMode.Decompress); await CopyLimited(gzip, target, 1_000_000_000, token); }
        else await CopyLimited(file, target, 1_000_000_000, token);
        File.Delete(output + ".download");
    }
    static async Task CopyLimited(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[65536]; long total = 0; int n;
        while ((n = await input.ReadAsync(buffer, token)) > 0) { total += n; if (total > limit) throw new IOException("Source exceeds size limit."); await output.WriteAsync(buffer.AsMemory(0,n), token); }
    }
    public static async Task<string> DownloadPlaylist(HttpClient client, string url, CancellationToken token)
    {
        using var response = await client.GetAsync(Url(url), HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new IOException($"Playlist download returned HTTP {(int)response.StatusCode}.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); await CopyLimited(input, output, 20_000_000, token);
        return Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
    }
    public static string Convert(string playlist, string guide, string output, DateTimeOffset now, CancellationToken token = default)
    {
        var names = new Dictionary<string,HashSet<string>>();
        var validIds = new HashSet<string>();
        using (var reader = Reader(guide))
            while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.Name == "channel")
            {
                token.ThrowIfCancellationRequested();
                using var sub = reader.ReadSubtree(); var c = XElement.Load(sub);
                var id = (string?)c.Attribute("id") ?? throw new InvalidDataException("EPG channel missing ID."); validIds.Add(id);
                foreach (var name in c.Elements("display-name")) { var key = Key(name.Value); if (!names.TryGetValue(key, out var ids)) names[key] = ids = []; ids.Add(id); }
            }
        var lines = playlist.Replace("\r", "").Split('\n').ToList();
        if (!lines[0].StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new InvalidDataException("Not an M3U playlist.");
        var used = new HashSet<string>(); int matched = 0, entries = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!lines[i].StartsWith("#EXTINF:")) continue;
            entries++;
            var comma = Regex.Match(lines[i], ",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)");
            if (!comma.Success) throw new InvalidDataException("Malformed M3U entry.");
            var metadata = lines[i][..comma.Index]; var name = lines[i][(comma.Index+1)..];
            var existing = Regex.Match(metadata, "tvg-id=\"([^\"]*)\"").Groups[1].Value;
            var group = Regex.Match(metadata, "group-title=\"([^\"]*)\"").Groups[1].Value;
            int j = i + 1;
            for (; j < lines.Count && !lines[j].StartsWith("#EXTINF:"); j++)
            {
                if (lines[j].StartsWith("#EXTGRP:")) group = lines[j][8..].Trim();
                else if (lines[j].Length > 0 && !lines[j].StartsWith('#')) break;
            }
            if (j == lines.Count || lines[j].StartsWith("#EXTINF:")) throw new InvalidDataException("M3U entry has no stream URL.");
            var id = validIds.Contains(existing) ? existing : names.TryGetValue(Key(name), out var candidates) && candidates.Count == 1 ? candidates.Single() : "";
            metadata = Regex.Replace(metadata, "\\s+(?:tvg-id|group-title)=\"[^\"]*\"", "");
            if (id.Length > 0) { metadata += $" tvg-id=\"{id.Replace('"','\'')}\""; used.Add(id); matched++; }
            if (group.Length > 0) metadata += $" group-title=\"{group.Replace('"','\'')}\"";
            lines[i] = metadata + "," + name;
        }
        if (entries == 0 || matched == 0) throw new InvalidDataException("No channels matched the EPG; refusing to publish.");
        lines[0] = "#EXTM3U"; // Jellyfin uses the configured local XMLTV provider.
        File.WriteAllLines(Path.Combine(output,"playlist.m3u"), lines.Where(l => !l.StartsWith("#EXTGRP:")), new UTF8Encoding(false));
        int programmes = 0, invalid = 0;
        using (var writer = XmlWriter.Create(Path.Combine(output,"guide.xml"), new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        using (var reader = Reader(guide))
        {
            writer.WriteStartDocument(); writer.WriteStartElement("tv");
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.Name is not ("channel" or "programme")) continue;
                using var sub = reader.ReadSubtree(); var item = XElement.Load(sub);
                if (item.Name == "channel") { if (used.Contains((string?)item.Attribute("id") ?? "")) item.WriteTo(writer); continue; }
                if (!used.Contains((string?)item.Attribute("channel") ?? "")) continue;
                var title = ((string?)item.Element("title") ?? "").Trim().TrimEnd('.', '!');
                if (Placeholders.Contains(title)) continue;
                DateTimeOffset start, stop;
                try { start = Timestamp((string?)item.Attribute("start")); stop = Timestamp((string?)item.Attribute("stop")); }
                catch (InvalidDataException) { invalid++; continue; }
                if (stop <= start) { invalid++; continue; }
                if (stop <= now || start >= now.AddDays(7)) continue;
                foreach (var cat in item.Elements("category").ToArray())
                    if (Categories.TryGetValue(cat.Value.Trim(), out var normalized) && !item.Elements("category").Any(c => c.Value == normalized)) item.Add(new XElement("category", normalized));
                item.WriteTo(writer); programmes++;
            }
            writer.WriteEndElement(); writer.WriteEndDocument();
        }
        if (programmes == 0) throw new InvalidDataException("No current programmes; refusing to publish.");
        return $"{entries} channels, {matched} matched, {programmes} programmes, {invalid} invalid schedules skipped";
    }
    static DateTimeOffset Timestamp(string? value)
    {
        if (value is null) throw new InvalidDataException("Missing programme timestamp.");
        var s = Regex.Replace(value.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2");
        if (!DateTimeOffset.TryParseExact(s, "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new InvalidDataException("Invalid XMLTV timestamp.");
        return date;
    }
}
