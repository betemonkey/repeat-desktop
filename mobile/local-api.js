/* On repeat, phone version. There is no server: this answers the page's
   /api/* routes from this browser's storage, with the rules the desktop app
   keeps in RepeatApi.cs and Playlists.cs (same checks, same limits, same
   order). Only a search and a title lookup go out to YouTube, and the search
   key is the one the user saved on this phone. Counts and playlists stay on
   this phone; nothing syncs. */
"use strict";
const LocalApi = (() => {
  const COUNTS = "msRepeatCounts", LISTS = "msRepeatLists", KEY = "msRepeatYtKey";
  const MAX_STEP = 500, MAX_LISTS = 100, MAX_SONGS = 500, MAX_NAME = 60;
  const YT_ID = /^[A-Za-z0-9_-]{11}$/;
  const MAX_TOTAL = 10_000_000;

  class ApiError extends Error {
    constructor(status, message) { super(message); this.status = status; }
  }
  const bad = message => new ApiError(400, message);
  const now = () => new Date().toISOString().slice(0, 19) + "+00:00";
  const num = v => (typeof v === "number" && Number.isFinite(v) ? v : 0);
  const str = v => (typeof v === "string" ? v : "");
  const clip = (s, max) => s.slice(0, max);
  const byDesc = (x, y) => (x < y ? 1 : x > y ? -1 : 0);

  // ---------- storage: a damaged value throws and is never overwritten ----------

  function readCounts() {
    const raw = localStorage.getItem(COUNTS);
    if (raw === null) return {};
    const videos = JSON.parse(raw)?.videos;
    if (!videos || typeof videos !== "object" || Array.isArray(videos)) throw new Error("the saved counts are damaged");
    return videos;
  }
  function writeCounts(videos) { localStorage.setItem(COUNTS, JSON.stringify({videos})); }

  function readLists() {
    const raw = localStorage.getItem(LISTS);
    if (raw === null) return [];
    const lists = JSON.parse(raw)?.playlists;
    if (!Array.isArray(lists)) throw new Error("the saved playlists are damaged");
    return lists;
  }
  function writeLists(lists) { localStorage.setItem(LISTS, JSON.stringify({playlists: lists})); }

  const searchKey = () => (localStorage.getItem(KEY) || "").trim();

  // ---------- counts (RepeatApi.cs) ----------

  /* the all-videos total is always the sum, so it cannot drift */
  function payload(videos) {
    const rows = Object.entries(videos)
      .map(([id, v]) => ({...v, id}))
      .sort((a, b) => byDesc(num(a.seq), num(b.seq)) || byDesc(str(a.last), str(b.last)));
    const total = rows.reduce((sum, r) => sum + num(r.count), 0);
    return {videos: rows, total};
  }

  /* a loop adds n (never a total); a play adds 0 and only stamps last and seq */
  function count(id, n, title, channel) {
    if (!YT_ID.test(id)) throw bad("bad video id");
    if (!Number.isInteger(n) || n < 0 || n > MAX_STEP) throw bad("bad n");
    const videos = readCounts(), stamp = now();
    const v = videos[id] || (videos[id] = {count: 0, first: stamp});
    v.count = num(v.count) + n;
    v.last = stamp;
    v.seq = Math.max(...Object.values(videos).map(x => num(x.seq))) + 1;
    if (title.trim()) v.title = clip(title.trim(), 200);      // keep the newest non-empty title
    if (channel.trim()) v.channel = clip(channel.trim(), 120);
    writeCounts(videos);
    return payload(videos);
  }

  /* removes a video and its loops; the page keeps it for Undo */
  function remove(id) {
    if (!YT_ID.test(id)) throw bad("bad video id");
    const videos = readCounts();
    if (id in videos) { delete videos[id]; writeCounts(videos); }
    return payload(videos);
  }

  /* Undo of a delete. If the video was played again since, the two add together */
  function restore(entry) {
    const id = str(entry.id);
    if (!YT_ID.test(id)) throw bad("bad video id");
    const c = num(entry.count);
    if (c < 0 || c > MAX_TOTAL) throw bad("bad count");
    const videos = readCounts();
    const played = videos[id];
    const back = {
      count: c + (played ? num(played.count) : 0),
      first: str(entry.first) || now(),
      last: str(entry.last),
      seq: num(entry.seq),
    };
    if (str(entry.title)) back.title = clip(str(entry.title), 200);
    if (str(entry.channel)) back.channel = clip(str(entry.channel), 120);
    if (played) { back.last = played.last; back.seq = played.seq; }   // keep the newer order and stamp
    videos[id] = back;
    writeCounts(videos);
    return payload(videos);
  }

  // ---------- playlists (Playlists.cs) ----------

  function find(lists, pid) {
    const p = lists.find(x => str(x.id) === pid);
    if (!p) throw bad("no such playlist");
    return p;
  }
  const songsOf = p => (Array.isArray(p.songs) ? p.songs : (p.songs = []));

  function cleanName(name) {
    name = str(name).trim();
    if (name.length === 0 || name.length > MAX_NAME) throw bad(`a name is 1 to ${MAX_NAME} characters`);
    return name;
  }

  function song(id, title, channel) {
    if (!YT_ID.test(id)) throw bad("bad video id");
    const s = {id};
    if (str(title).trim()) s.title = clip(str(title).trim(), 200);
    if (str(channel).trim()) s.channel = clip(str(channel).trim(), 120);
    return s;
  }

  function newId() {
    return "p" + crypto.randomUUID().replace(/-/g, "").slice(0, 10);
  }

  function createList(name) {
    const lists = readLists();
    if (lists.length >= MAX_LISTS) throw bad(`at most ${MAX_LISTS} playlists`);
    lists.push({id: newId(), name: cleanName(name), created: now(), songs: []});
    writeLists(lists);
    return {playlists: lists};
  }

  function renameList(pid, name) {
    const lists = readLists();
    find(lists, pid).name = cleanName(name);
    writeLists(lists);
    return {playlists: lists};
  }

  function deleteList(pid) {
    const lists = readLists();
    const i = lists.indexOf(find(lists, pid));
    lists.splice(i, 1);
    writeLists(lists);
    return {playlists: lists};
  }

  /* Undo of a delete: the list as the page kept it, at its old place */
  function restoreList(p, index) {
    const lists = readLists();
    const pid = str(p.id);
    if (pid.length === 0 || pid.length > 40 || lists.some(x => str(x.id) === pid)) throw bad("can't restore that playlist");
    if (lists.length >= MAX_LISTS) throw bad(`at most ${MAX_LISTS} playlists`);
    const back = {id: pid, name: cleanName(str(p.name)), created: str(p.created), songs: []};
    for (const s of (Array.isArray(p.songs) ? p.songs : []).slice(0, MAX_SONGS))
      back.songs.push(song(str(s?.id), str(s?.title), str(s?.channel)));
    lists.splice(Math.min(Math.max(index, 0), lists.length), 0, back);
    writeLists(lists);
    return {playlists: lists};
  }

  /* adding a song already in the list does nothing, and says so in "already" */
  function addSong(pid, id, title, channel) {
    const lists = readLists();
    const songs = songsOf(find(lists, pid));
    if (songs.some(s => str(s.id) === id)) return {playlists: lists, already: true};
    if (songs.length >= MAX_SONGS) throw bad(`at most ${MAX_SONGS} songs in a playlist`);
    songs.push(song(id, title, channel));
    writeLists(lists);
    return {playlists: lists, already: false};
  }

  function removeSong(pid, id) {
    const lists = readLists();
    const songs = songsOf(find(lists, pid));
    const i = songs.findIndex(s => str(s.id) === id);
    if (i < 0) throw bad("not in this playlist");
    songs.splice(i, 1);
    writeLists(lists);
    return {playlists: lists};
  }

  /* Undo of a remove: the song back at its old place */
  function insertSong(pid, item, index) {
    const lists = readLists();
    const songs = songsOf(find(lists, pid));
    const s = song(str(item?.id), str(item?.title), str(item?.channel));
    if (songs.some(x => str(x.id) === s.id)) return {playlists: lists};
    if (songs.length >= MAX_SONGS) throw bad(`at most ${MAX_SONGS} songs in a playlist`);
    songs.splice(Math.min(Math.max(index, 0), songs.length), 0, s);
    writeLists(lists);
    return {playlists: lists};
  }

  /* drag to reorder: the new order must hold exactly the songs the list has now */
  function orderSongs(pid, ids) {
    const lists = readLists();
    const songs = songsOf(find(lists, pid));
    const byId = new Map(songs.map(s => [str(s.id), s]));
    if (ids.length !== byId.size || new Set(ids).size !== ids.length || ids.some(i => !byId.has(i)))
      throw bad("the order doesn't match the playlist; reload it");
    find(lists, pid).songs = ids.map(i => byId.get(i));
    writeLists(lists);
    return {playlists: lists};
  }

  // ---------- YouTube ----------

  async function youtube(path, params, key) {
    const r = await fetch(`https://www.googleapis.com/youtube/v3/${path}?` + new URLSearchParams({...params, key}));
    if (!r.ok) throw new ApiError(r.status, `YouTube API ${r.status}`);
    return r.json();
  }

  /* "PT1H2M10S" -> "1:02:10", "PT3M27S" -> "3:27"; "" when unparseable */
  function duration(iso) {
    const m = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$/.exec(iso || "");
    if (!m || iso === "P" || iso === "PT" || iso === "P0D") return "";
    const g = i => (m[i] ? parseInt(m[i], 10) : 0);
    const h = g(1) * 24 + g(2);
    const pad = x => String(x).padStart(2, "0");
    return h > 0 ? `${h}:${pad(g(3))}:${pad(g(4))}` : `${g(3)}:${pad(g(4))}`;
  }

  /* title and channel of a pasted link, from YouTube's keyless oEmbed */
  async function info(id) {
    if (!YT_ID.test(id)) throw bad("bad video id");
    const url = "https://www.youtube.com/oembed?format=json&url=" +
      encodeURIComponent(`https://www.youtube.com/watch?v=${id}`);
    let d;
    try { d = await (await fetch(url)).json(); }
    catch (e) { throw new ApiError(502, e.message); }
    return {id, title: str(d.title), channel: str(d.author_name)};
  }

  /* search.list costs 100 of the 10,000 free daily units; videos.list costs 1 more */
  async function search(term) {
    if (term.length < 2 || term.length > 100) throw bad("bad query");
    const key = searchKey();
    if (!key) throw new ApiError(503, "nokey");
    try {
      const found = await youtube("search", {part: "snippet", type: "video", maxResults: "10", q: term}, key);
      const ids = (found.items || []).map(it => str(it?.id?.videoId)).filter(x => YT_ID.test(x));
      if (ids.length === 0) return {results: []};
      const details = await youtube("videos", {part: "snippet,contentDetails,status", id: ids.join(",")}, key);
      const byId = new Map((details.items || []).filter(Boolean).map(it => [str(it.id), it]));
      const results = ids.map(id => {
        const it = byId.get(id);
        const sn = it?.snippet;
        const live = str(sn?.liveBroadcastContent) === "live";
        return {
          id, title: str(sn?.title), channel: str(sn?.channelTitle),
          duration: live ? "LIVE" : duration(str(it?.contentDetails?.duration)),
          embeddable: it?.status?.embeddable ?? true,
        };
      });
      return {results};
    } catch (e) {
      if (e.status === 403) throw new ApiError(502, "quota");   // a 403 is almost always the daily quota
      throw new ApiError(502, e.message);
    }
  }

  /* checks a pasted key with one 1-unit request before saving it; "" removes it */
  async function saveKey(raw) {
    const key = raw.trim();
    if (key.length > 0) {
      if (key.length > 100 || /\s/.test(key)) throw bad("That doesn't look like an API key.");
      try {
        await youtube("videos", {part: "id", id: "jNQXAC9IVRw"}, key);
      } catch (e) {
        if (!(e instanceof ApiError)) throw new ApiError(502, "Couldn't reach Google to check the key.");
        throw bad(e.status === 400 ? "Google says this key isn't valid. Check you copied all of it."
          : e.status === 403 ? "Google refused this key. Is the YouTube Data API v3 enabled for its project, and is the key allowed to use it?"
          : `Couldn't check the key: ${e.message}`);
      }
      localStorage.setItem(KEY, key);
    } else {
      localStorage.removeItem(KEY);
    }
    return {search: key.length > 0};
  }

  // ---------- routes ----------

  async function route(method, url, body) {
    const path = url.pathname;
    const q = url.searchParams;
    if (method === "GET") {
      switch (path) {
        case "/api/repeat": return {...payload(readCounts()), search: searchKey().length > 0};
        case "/api/playlists": return {playlists: readLists()};
        case "/api/ytinfo": return info(q.get("id") || "");
        case "/api/ytsearch": return search((q.get("q") || "").trim());
      }
    }
    if (method === "POST") {
      const nOf = () => {
        if (body.n === undefined) return 1;                       // no "n" means one loop
        if (!Number.isInteger(body.n)) throw bad("bad n");        // an explicit null or "3" is refused
        return body.n;
      };
      switch (path) {
        case "/api/repeat-count": {
          const n = nOf();
          if (n === 0) throw bad("bad n");
          return count(str(body.id), n, str(body.title), str(body.channel));
        }
        case "/api/repeat-played": return count(str(body.id), 0, str(body.title), str(body.channel));
        case "/api/repeat-delete": return remove(str(body.id));
        case "/api/repeat-restore": {
          if (!body.entry || typeof body.entry !== "object") throw bad("no entry");
          return restore(body.entry);
        }
        case "/api/settings": return saveKey(str(body.ytKey));
        case "/api/playlist-create": return createList(str(body.name));
        case "/api/playlist-rename": return renameList(str(body.pid), str(body.name));
        case "/api/playlist-delete": return deleteList(str(body.pid));
        case "/api/playlist-restore": {
          if (!body.playlist || typeof body.playlist !== "object") throw bad("no playlist");
          return restoreList(body.playlist, Number.isInteger(body.index) ? body.index : Infinity);
        }
        case "/api/playlist-add": return addSong(str(body.pid), str(body.id), str(body.title), str(body.channel));
        case "/api/playlist-remove": return removeSong(str(body.pid), str(body.id));
        case "/api/playlist-insert": {
          if (!body.song || typeof body.song !== "object") throw bad("no song");
          return insertSong(str(body.pid), body.song, Number.isInteger(body.index) ? body.index : Infinity);
        }
        case "/api/playlist-order": {
          if (!Array.isArray(body.ids)) throw bad("no ids");
          return orderSongs(str(body.pid), body.ids.map(str));
        }
      }
    }
    throw new ApiError(404, "no such route");
  }

  /* same shape as the app's answers: JSON, and {error, results} on failure */
  async function handle(path, opts = {}) {
    let data, status = 200;
    try {
      const url = new URL(path, location.href);
      const method = (opts.method || "GET").toUpperCase();
      let body = {};
      if (opts.body) {
        try { body = JSON.parse(opts.body); } catch (e) { throw bad("bad body"); }
        if (!body || typeof body !== "object") throw bad("bad body");
      }
      data = await route(method, url, body);
    } catch (e) {
      status = e instanceof ApiError ? e.status : 500;
      data = {error: e.message, results: []};
    }
    return new Response(JSON.stringify(data), {status, headers: {"Content-Type": "application/json"}});
  }

  return {handle};
})();
