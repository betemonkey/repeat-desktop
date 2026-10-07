using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepeatDesktop;

/// <summary>The page's backend, in-process. The page was first built for the
/// monkey-status server (its /api/repeat* routes); Monkey then asked for an app
/// that needs no NAS (2026-10-07), so the same routes are answered here and the
/// page code stays the same. Counts live in %APPDATA%\RepeatDesktop\repeat-counts.json,
/// in the NAS file's shape, so they could be merged into it one day.
///
/// The rules carried over from the server, each one there for a reason:
/// - a loop adds n (1, or a flush of loops queued while this couldn't be
///   reached), never a total, capped at MaxStep;
/// - a play (/api/repeat-played) adds 0 and only stamps last + seq, so the
///   page reopens on the last video played;
/// - seq is the play order: timestamps are whole seconds and two plays in one
///   second tied;
/// - the all-videos total is always the sum, so it cannot drift.</summary>
sealed partial class RepeatApi(Settings settings, string countsFile)
{
    public const int MaxStep = 500;
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")] private static partial Regex YtId();
    public static bool IsVideoId(string s) => YtId().IsMatch(s ?? "");
    readonly Playlists lists = new(Path.Combine(Path.GetDirectoryName(countsFile)!, "playlists.json"));
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly Lock gate = new();

    public record Reply(int Status, string Json);
    static Reply Ok(object o) => new(200, JsonSerializer.Serialize(o));
    static Reply Fail(int status, string error) => new(status, JsonSerializer.Serialize(new { error, results = Array.Empty<object>() }));

    public async Task<Reply> Handle(string method, string path, IReadOnlyDictionary<string, string> query, string body)
    {
        try
        {
            switch (method, path)
            {
                case ("GET", "/api/repeat"):
                    var p = Payload(Load());
                    p["search"] = !string.IsNullOrWhiteSpace(settings.YoutubeApiKey);
                    return new(200, p.ToJsonString());
                case ("POST", "/api/repeat-count"):
                case ("POST", "/api/repeat-played"):
                    var b = JsonNode.Parse(body) as JsonObject ?? throw new ArgumentException("bad body");
                    // a loop adds at least 1; a play adds nothing, it only stamps "last"
                    // no "n" at all means 1; an explicit null is refused, as the server did
                    var n = path == "/api/repeat-played" ? 0 : b.ContainsKey("n") ? ReadStep(b["n"]) : 1;
                    if (path == "/api/repeat-count" && n == 0) throw new ArgumentException("bad n");
                    return new(200, Count(Str(b["id"]), n, Str(b["title"]), Str(b["channel"])).ToJsonString());
                // ---- playlists (P2): every call returns the whole set ----
                case ("GET", "/api/playlists"):
                    return new(200, lists.All().ToJsonString());
                case ("POST", _) when path.StartsWith("/api/playlist-"):
                {
                    var pl = JsonNode.Parse(body) as JsonObject ?? throw new ArgumentException("bad body");
                    string pid = Str(pl["pid"]), id = Str(pl["id"]);
                    int index = pl["index"] is JsonNode ix && ix.GetValueKind() == JsonValueKind.Number ? ix.GetValue<int>() : int.MaxValue;
                    var res = path switch
                    {
                        "/api/playlist-create" => lists.Create(Str(pl["name"])),
                        "/api/playlist-rename" => lists.Rename(pid, Str(pl["name"])),
                        "/api/playlist-delete" => lists.Delete(pid),
                        "/api/playlist-restore" => lists.Restore(pl["playlist"] as JsonObject ?? throw new ArgumentException("no playlist"), index),
                        "/api/playlist-add" => lists.Add(pid, id, Str(pl["title"]), Str(pl["channel"])),
                        "/api/playlist-remove" => lists.Remove(pid, id),
                        "/api/playlist-insert" => lists.Insert(pid, pl["song"] as JsonObject ?? throw new ArgumentException("no song"), index),
                        "/api/playlist-order" => lists.Order(pid, (pl["ids"] as JsonArray ?? throw new ArgumentException("no ids"))
                            .Select(Str).ToList()),
                        _ => throw new ArgumentException("no such route"),
                    };
                    return new(200, res.ToJsonString());
                }
                case ("POST", "/api/repeat-delete"):
                    return new(200, Delete(Str((JsonNode.Parse(body) as JsonObject)?["id"])).ToJsonString());
                case ("POST", "/api/repeat-restore"):
                    return new(200, Restore(JsonNode.Parse(body)?["entry"] as JsonObject
                                            ?? throw new ArgumentException("no entry")).ToJsonString());
                case ("GET", "/api/ytinfo"):
                    return await Info(query.GetValueOrDefault("id", ""));
                case ("GET", "/api/ytsearch"):
                    return await Search(query.GetValueOrDefault("q", "").Trim());
                case ("POST", "/api/settings"):
                    return await SaveKey(JsonNode.Parse(body)?["ytKey"]?.GetValue<string>() ?? "");
                default:
                    return Fail(404, "no such route");
            }
        }
        catch (Exception e) when (e is ArgumentException or JsonException or FormatException or InvalidOperationException)
        {
            return Fail(400, e.Message);
        }
    }

    // ---------- counts ----------

    JsonObject Load()
    {
        string text;
        try { text = File.ReadAllText(countsFile); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
        // a damaged file throws here instead of being replaced by an empty one:
        // losing every count to a bad write is worse than an error on screen
        return JsonNode.Parse(text)?["videos"] as JsonObject
               ?? throw new InvalidDataException($"{countsFile} has no videos map");
    }

    static JsonObject Payload(JsonObject videos)
    {
        var rows = videos.Select(kv =>
            {
                var row = (JsonObject)kv.Value!.DeepClone();
                row["id"] = kv.Key;
                return row;
            })
            .OrderByDescending(r => Num(r["seq"]))
            .ThenByDescending(r => r["last"]?.GetValue<string>() ?? "")
            .ToList();
        var total = rows.Sum(r => Num(r["count"]));
        return new JsonObject { ["videos"] = new JsonArray([.. rows]), ["total"] = total };
    }

    /// <summary>Removes a video and its loops (Monkey asked for a delete,
    /// 2026-10-07). Gone from Recent and from the all-videos total. The page
    /// keeps the removed entry for a few seconds and offers Undo, which sends
    /// it back through Restore.</summary>
    public JsonObject Delete(string id)
    {
        if (!YtId().IsMatch(id)) throw new ArgumentException("bad video id");
        lock (gate)
        {
            var videos = Load();
            if (videos.Remove(id)) Write(videos);
            return Payload(videos);
        }
    }

    /// <summary>Undo of a delete: puts the entry back as it was. If the video
    /// was played again in the meantime, the two are added together rather
    /// than one replacing the other.</summary>
    public JsonObject Restore(JsonObject entry)
    {
        var id = Str(entry["id"]);
        if (!YtId().IsMatch(id)) throw new ArgumentException("bad video id");
        var count = Num(entry["count"]);
        if (count is < 0 or > 10_000_000) throw new ArgumentException("bad count");
        lock (gate)
        {
            var videos = Load();
            var back = new JsonObject
            {
                ["count"] = count + (videos[id] is JsonObject now ? Num(now["count"]) : 0),
                ["first"] = Str(entry["first"]) is { Length: > 0 } f ? f : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00"),
                ["last"] = Str(entry["last"]),
                ["seq"] = Num(entry["seq"]),
            };
            if (Str(entry["title"]) is { Length: > 0 } t) back["title"] = Clip(t, 200);
            if (Str(entry["channel"]) is { Length: > 0 } c) back["channel"] = Clip(c, 120);
            if (videos[id] is JsonObject played)        // played again since: keep its newer order and stamp
            {
                back["last"] = played["last"]?.DeepClone();
                back["seq"] = played["seq"]?.DeepClone();
            }
            videos[id] = back;
            Write(videos);
            return Payload(videos);
        }
    }

    void Write(JsonObject videos)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(countsFile)!);
        var tmp = countsFile + ".tmp";
        File.WriteAllText(tmp, new JsonObject { ["videos"] = videos.DeepClone() }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, countsFile, overwrite: true);
    }

    public JsonObject Count(string id, int n, string title = "", string channel = "")
    {
        if (!YtId().IsMatch(id)) throw new ArgumentException("bad video id");
        if (n is < 0 or > MaxStep) throw new ArgumentException("bad n");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00");
        lock (gate)
        {
            var videos = Load();
            if (videos[id] is not JsonObject v)
                videos[id] = v = new JsonObject { ["count"] = 0L, ["first"] = now };
            v["count"] = Num(v["count"]) + n;
            v["last"] = now;
            v["seq"] = videos.Max(kv => Num(kv.Value?["seq"])) + 1;
            // keep the newest non-empty title; a flush from an old session may lack one
            if (!string.IsNullOrWhiteSpace(title)) v["title"] = Clip(title.Trim(), 200);
            if (!string.IsNullOrWhiteSpace(channel)) v["channel"] = Clip(channel.Trim(), 120);
            Write(videos);
            return Payload(videos);
        }
    }

    static int ReadStep(JsonNode? node)
    {
        if (node is null) throw new ArgumentException("bad n");
        // the step must be a real integer: true, "3" and 1.5 are refused, not coerced
        if (node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<int>(out var n))
            throw new ArgumentException("bad n");
        return n;
    }

    static string Str(JsonNode? node) =>
        node is not null && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>A stored number, however it was boxed: a node built from an int
    /// refuses GetValue<long>(), and a file written by Python reads back as
    /// long. Missing or not a number counts as 0.</summary>
    static long Num(JsonNode? n) =>
        n is not null && n.GetValueKind() == JsonValueKind.Number && long.TryParse(n.ToJsonString(), out var x) ? x : 0;

    // ---------- YouTube ----------

    /// <summary>Title + channel of a pasted link, from YouTube's keyless oEmbed.</summary>
    static async Task<Reply> Info(string id)
    {
        if (!YtId().IsMatch(id)) return Fail(400, "bad video id");
        try
        {
            var url = "https://www.youtube.com/oembed?format=json&url=" +
                      Uri.EscapeDataString($"https://www.youtube.com/watch?v={id}");
            var d = JsonNode.Parse(await Http.GetStringAsync(url))!;
            return Ok(new { id, title = Str(d["title"]), channel = Str(d["author_name"]) });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Fail(502, e.Message);
        }
    }

    /// <summary>search.list costs 100 of the 10,000 free daily units; videos.list
    /// (durations, embeddable flag) costs 1 more.</summary>
    async Task<Reply> Search(string term)
    {
        if (term.Length is < 2 or > 100) return Fail(400, "bad query");
        var key = settings.YoutubeApiKey?.Trim() ?? "";
        if (key.Length == 0) return Fail(503, "nokey");
        try
        {
            var found = await Api("search", new() { ["part"] = "snippet", ["type"] = "video", ["maxResults"] = "10", ["q"] = term }, key);
            var ids = (found["items"]?.AsArray() ?? [])
                .Select(it => Str(it?["id"]?["videoId"])).Where(x => YtId().IsMatch(x)).ToList();
            if (ids.Count == 0) return Ok(new { results = Array.Empty<object>() });
            var info = (await Api("videos", new() { ["part"] = "snippet,contentDetails,status", ["id"] = string.Join(",", ids) }, key))["items"]?
                .AsArray().Where(it => it is not null).ToDictionary(it => Str(it!["id"]), it => it!) ?? [];
            var results = ids.Select(id =>
            {
                var it = info.GetValueOrDefault(id);
                var sn = it?["snippet"];
                var live = Str(sn?["liveBroadcastContent"]) == "live";
                return new
                {
                    id, title = Str(sn?["title"]), channel = Str(sn?["channelTitle"]),
                    duration = live ? "LIVE" : Duration(Str(it?["contentDetails"]?["duration"])),
                    embeddable = it?["status"]?["embeddable"]?.GetValue<bool>() ?? true,
                };
            });
            return Ok(new { results });
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Forbidden)
        {
            return Fail(502, "quota");      // a 403 from the Data API is almost always the daily quota
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Fail(502, e.Message);
        }
    }

    /// <summary>Saves a pasted API key only after one 1-unit request proves it
    /// works, so a typo is caught here and not at the first search. "" removes it.</summary>
    async Task<Reply> SaveKey(string key)
    {
        key = key.Trim();
        if (key.Length > 0)
        {
            if (key.Length > 100 || key.Any(char.IsWhiteSpace)) return Fail(400, "That doesn't look like an API key.");
            try
            {
                await Api("videos", new() { ["part"] = "id", ["id"] = "jNQXAC9IVRw" }, key);
            }
            catch (HttpRequestException e)
            {
                return Fail(400, e.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "Google says this key isn't valid. Check you copied all of it.",
                    HttpStatusCode.Forbidden => "Google refused this key. Is the YouTube Data API v3 enabled for its project, and is the key allowed to use it?",
                    _ => $"Couldn't check the key: {e.Message}",
                });
            }
            catch (TaskCanceledException) { return Fail(502, "Couldn't reach Google to check the key."); }
        }
        settings.YoutubeApiKey = key;
        settings.Save();
        return Ok(new { search = key.Length > 0 });
    }

    static async Task<JsonNode> Api(string path, Dictionary<string, string> args, string key)
    {
        args["key"] = key;
        var q = string.Join("&", args.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        using var r = await Http.GetAsync($"https://www.googleapis.com/youtube/v3/{path}?{q}");
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"YouTube API {(int)r.StatusCode}", null, r.StatusCode);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    }

    /// <summary>"PT1H2M10S" -> "1:02:10", "PT3M27S" -> "3:27"; "" when unparseable.</summary>
    public static string Duration(string iso)
    {
        var m = Regex.Match(iso ?? "", @"^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success || iso is "P" or "PT" or "P0D") return "";
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        var h = G(1) * 24 + G(2);
        return h > 0 ? $"{h}:{G(3):00}:{G(4):00}" : $"{G(3)}:{G(4):00}";
    }
}
