using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepeatDesktop;

/// <summary>Your own playlists (Monkey picked "P2" on 2026-10-08,
/// mockups/playlist-options.html). Kept in %APPDATA%\RepeatDesktop\playlists.json
/// beside the counts. A playlist is a name and an ordered list of songs; each
/// song carries its title and channel so a list made from search results shows
/// names before the song has ever been played. Counts are NOT kept here: a
/// song's loops live in repeat-counts.json and are the same whichever list
/// played it.
///
/// Every change returns the whole set, like the counts routes do, so the page
/// never has to merge. Removing a song or a list is undone by putting back
/// exactly what the page kept (Insert / Restore).</summary>
sealed class Playlists(string file)
{
    public const int MaxLists = 100, MaxSongs = 500, MaxName = 60;
    readonly Lock gate = new();

    JsonArray Load()
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
        // like the counts file: a damaged file is an error, never silently emptied
        return JsonNode.Parse(text)?["playlists"] as JsonArray
               ?? throw new InvalidDataException($"{file} has no playlists list");
    }

    void Write(JsonArray lists)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, new JsonObject { ["playlists"] = lists.DeepClone() }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, overwrite: true);
    }

    public JsonObject All() { lock (gate) return Payload(Load()); }
    static JsonObject Payload(JsonArray lists) => new() { ["playlists"] = lists.DeepClone() };

    JsonObject Change(Action<JsonArray> act)
    {
        lock (gate)
        {
            var lists = Load();
            act(lists);
            Write(lists);
            return Payload(lists);
        }
    }

    static JsonObject Find(JsonArray lists, string pid) =>
        lists.OfType<JsonObject>().FirstOrDefault(p => S(p["id"]) == pid) ?? throw new ArgumentException("no such playlist");

    static JsonArray Songs(JsonObject p) => p["songs"] as JsonArray ?? (JsonArray)(p["songs"] = new JsonArray());

    static string Name(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > MaxName) throw new ArgumentException($"a name is 1 to {MaxName} characters");
        return name;
    }

    static JsonObject Song(string id, string title, string channel)
    {
        if (!RepeatApi.IsVideoId(id)) throw new ArgumentException("bad video id");
        var s = new JsonObject { ["id"] = id };
        if (!string.IsNullOrWhiteSpace(title)) s["title"] = Clip(title.Trim(), 200);
        if (!string.IsNullOrWhiteSpace(channel)) s["channel"] = Clip(channel.Trim(), 120);
        return s;
    }

    public JsonObject Create(string name) => Change(lists =>
    {
        if (lists.Count >= MaxLists) throw new ArgumentException($"at most {MaxLists} playlists");
        lists.Add(new JsonObject
        {
            ["id"] = "p" + Guid.NewGuid().ToString("N")[..10],
            ["name"] = Name(name),
            ["created"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00"),
            ["songs"] = new JsonArray(),
        });
    });

    public JsonObject Rename(string pid, string name) => Change(lists => Find(lists, pid)["name"] = Name(name));

    public JsonObject Delete(string pid) => Change(lists => lists.Remove(Find(lists, pid)));

    /// <summary>Undo of Delete: the list as the page kept it, at its old place.</summary>
    public JsonObject Restore(JsonObject p, int index) => Change(lists =>
    {
        var pid = S(p["id"]);
        if (pid.Length is 0 or > 40 || lists.OfType<JsonObject>().Any(x => S(x["id"]) == pid))
            throw new ArgumentException("can't restore that playlist");
        if (lists.Count >= MaxLists) throw new ArgumentException($"at most {MaxLists} playlists");
        var back = new JsonObject { ["id"] = pid, ["name"] = Name(S(p["name"])), ["created"] = S(p["created"]), ["songs"] = new JsonArray() };
        foreach (var s in (p["songs"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxSongs))
            Songs(back).Add(Song(S(s["id"]), S(s["title"]), S(s["channel"])));
        lists.Insert(Math.Clamp(index, 0, lists.Count), back);
    });

    /// <summary>Adds a song at the end. Adding a song already in the list does
    /// nothing (and says so in "already"), so a double click can't duplicate it.</summary>
    public JsonObject Add(string pid, string id, string title, string channel)
    {
        var already = false;
        var res = Change(lists =>
        {
            var songs = Songs(Find(lists, pid));
            if (songs.OfType<JsonObject>().Any(s => S(s["id"]) == id)) { already = true; return; }
            if (songs.Count >= MaxSongs) throw new ArgumentException($"at most {MaxSongs} songs in a playlist");
            songs.Add(Song(id, title, channel));
        });
        res["already"] = already;
        return res;
    }

    public JsonObject Remove(string pid, string id) => Change(lists =>
    {
        var songs = Songs(Find(lists, pid));
        var s = songs.OfType<JsonObject>().FirstOrDefault(x => S(x["id"]) == id) ?? throw new ArgumentException("not in this playlist");
        songs.Remove(s);
    });

    /// <summary>Undo of Remove: the song back at its old place.</summary>
    public JsonObject Insert(string pid, JsonObject song, int index) => Change(lists =>
    {
        var songs = Songs(Find(lists, pid));
        var s = Song(S(song["id"]), S(song["title"]), S(song["channel"]));
        if (songs.OfType<JsonObject>().Any(x => S(x["id"]) == S(s["id"]))) return;
        if (songs.Count >= MaxSongs) throw new ArgumentException($"at most {MaxSongs} songs in a playlist");
        songs.Insert(Math.Clamp(index, 0, songs.Count), s);
    });

    /// <summary>A drag-to-reorder: the new order must hold exactly the songs the
    /// list has now, so a stale page can't drop or duplicate anything.</summary>
    public JsonObject Order(string pid, IReadOnlyList<string> ids) => Change(lists =>
    {
        var songs = Songs(Find(lists, pid));
        var byId = songs.OfType<JsonObject>().ToDictionary(s => S(s["id"]), s => s);
        if (ids.Count != byId.Count || ids.Distinct().Count() != ids.Count || ids.Any(i => !byId.ContainsKey(i)))
            throw new ArgumentException("the order doesn't match the playlist; reload it");
        var ordered = ids.Select(i => byId[i].DeepClone()).ToList();
        songs.Clear();
        foreach (var s in ordered) songs.Add(s);
    });

    static string S(JsonNode? n) => n is not null && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : "";
    static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
