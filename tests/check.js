// Checks for the app's in-process backend (RepeatApi) and the page it serves.
// Starts the built app on a throwaway data folder (REPEAT_DESKTOP_DATA), so it
// never touches the real counts, drives it over WebView2's DevTools port, and
// closes it.
//
//   dotnet build -c Release
//   node tests/check.js
//
// Most cases are rules that shipped as bugs or were decided on purpose while
// this was the monkey-status /repeat page (its Python suite, test_repeat_page.py):
// loops move the count by a small positive step, a play moves it by nothing,
// two plays in one second keep their order, the total is the sum, a damaged
// counts file is reported and never overwritten.
const {spawn} = require("child_process");
const fs = require("fs"), os = require("os"), path = require("path");

// REPEAT_EXE tests another build, e.g. `dotnet build -c Release -o build-test`
// while the real app is open and locks bin\
const exe = process.env.REPEAT_EXE || path.join(__dirname, "..", "bin", "Release", "net9.0-windows", "RepeatDesktop.exe");
const port = 9455;
const sleep = ms => new Promise(r => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, got) => { console.log(`${ok ? "PASS" : "FAIL"}  ${name}${ok ? "" : "  -> " + JSON.stringify(got)}`); if (!ok) failed++; };

async function start(dataDir) {
  const app = spawn(exe, [], {env: {...process.env, REPEAT_DESKTOP_DATA: dataDir,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port} --autoplay-policy=no-user-gesture-required --mute-audio`}});
  let page;
  for (let i = 0; i < 60 && !page; i++) {
    try { page = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.url.includes("on-repeat.example")); } catch (e) {}
    if (!page) await sleep(500);
  }
  if (!page) throw new Error("the app's page never appeared");
  const ws = new WebSocket(page.webSocketDebuggerUrl); let id = 0; const wait = {};
  ws.onmessage = m => { const d = JSON.parse(m.data); if (d.id && wait[d.id]) { wait[d.id](d); delete wait[d.id]; } };
  await new Promise(r => ws.onopen = r);
  const ev = expr => new Promise(r => { const i = ++id; wait[i] = r;
    ws.send(JSON.stringify({id: i, method: "Runtime.evaluate", params: {expression: expr, returnByValue: true, awaitPromise: true}})); })
    .then(d => d.result.result.value ?? {threw: d.result.exceptionDetails?.exception?.description});
  await sleep(1500);
  return {app, ev, stop: async () => { app.kill(); await sleep(800); }};
}

// fetch from inside the page: same origin, same path the page itself uses
const call = (ev, method, url, body) => ev(`fetch(${JSON.stringify(url)}, {method: ${JSON.stringify(method)},
  headers: {"Content-Type": "application/json", "X-App-Token": APP_TOKEN}, body: ${body === undefined ? "undefined" : JSON.stringify(JSON.stringify(body))}})
  .then(async r => ({status: r.status, body: await r.json().catch(() => null)}))`);

(async () => {
  const VID = "dQw4w9WgXcQ", OTHER = "abcdefghijk";
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "repeat-check-"));
  let s = await start(dir);
  try {
    let r = await s.ev(`document.title + " | " + !!document.querySelector('[data-page="repeat"]') + " | " + document.body.classList.contains("app")`);
    check("page is served from inside the app, in app mode", /On repeat|↻/.test(r) && r.endsWith("true | true"), r);

    r = await call(s.ev, "GET", "/api/repeat");
    check("starts empty, search off without a key", r.status === 200 && r.body.total === 0 && r.body.search === false, r);

    await call(s.ev, "POST", "/api/repeat-count", {id: VID, n: 1, title: "Song", channel: "Chan"});
    await call(s.ev, "POST", "/api/repeat-count", {id: VID, n: 3});
    r = await call(s.ev, "POST", "/api/repeat-count", {id: OTHER, n: 2, title: "Other"});
    const byId = Object.fromEntries(r.body.videos.map(v => [v.id, v]));
    check("loops add up and the total is the sum", byId[VID].count === 4 && byId[OTHER].count === 2 && r.body.total === 6, r.body);
    check("a flush without a title keeps the stored one", byId[VID].title === "Song" && byId[VID].channel === "Chan", byId[VID]);

    r = await call(s.ev, "POST", "/api/repeat-played", {id: VID, n: 400, title: "Song"});
    check("a play counts nothing and cannot smuggle a count in", r.body.total === 6, r.body);
    check("...and moves the video to the top (two plays in one second keep their order)", r.body.videos[0].id === VID, r.body.videos.map(v => v.id));

    const bad = [];
    for (const n of [0, -1, 501, true, "3", 1.5, null]) {
      const x = await call(s.ev, "POST", "/api/repeat-count", {id: VID, n});
      if (x.status !== 400) bad.push(["n", n, x.status]);
    }
    for (const id of ["short", VID + "x", "../../etc/pa", null, 12345678901]) {
      const x = await call(s.ev, "POST", "/api/repeat-count", {id, n: 1});
      if (x.status !== 400) bad.push(["id", id, x.status]);
    }
    r = await call(s.ev, "GET", "/api/repeat");
    check("refuses anything but a small positive step on a real video id", bad.length === 0 && r.body.total === 6, {bad, total: r.body.total});

    r = await s.ev(`Promise.all(Array.from({length: 8}, () => (async () => { for (let i = 0; i < 25; i++)
      await fetch("/api/repeat-count", {method: "POST", headers: {"X-App-Token": APP_TOKEN}, body: JSON.stringify({id: "${OTHER}", n: 1})}); })()))
      .then(() => fetch("/api/repeat", {headers: {"X-App-Token": APP_TOKEN}})).then(r => r.json()).then(d => d.total)`);
    check("8 tabs x 25 loops lose nothing", r === 206, r);

    r = await call(s.ev, "GET", "/api/ytsearch?q=lofi");
    check("search without a key says so", r.status === 503 && r.body.error === "nokey", r);

    r = await call(s.ev, "POST", "/api/settings", {ytKey: "AIza-not-a-real-key-0000000000000000000"});
    check("a wrong API key is refused and not saved", r.status === 400 && !(fs.existsSync(path.join(dir, "settings.json")) && fs.readFileSync(path.join(dir, "settings.json"), "utf8").includes("not-a-real")), r);

    // the page side of the key: searching without one offers the guide, and a
    // wrong key comes back as Google's refusal in the panel, not saved
    r = await s.ev(`(async () => {
      const $ = id => document.getElementById(id), wait = ms => new Promise(r => setTimeout(r, ms));
      const hint = $("rp-q").placeholder;
      $("rp-q").value = "lofi"; $("rp-form").requestSubmit(); await wait(800);
      const offered = !!$("rp-addkey"); if (offered) $("rp-addkey").click(); await wait(200);
      const opened = !$("rp-keys").hidden;
      $("rp-key").value = "AIza-not-a-real-key-0000000000000000000"; $("rp-keyform").requestSubmit(); await wait(4000);
      return {hint, offered, opened, verdict: $("rp-keystate").textContent, steps: $("rp-keys").querySelectorAll("ol li").length};
    })()`);
    check("no key: search offers the guide, the panel opens with its 6 steps",
      r.offered && r.opened && r.steps === 6 && /add a search key/.test(r.hint), r);
    check("a wrong key shows Google's refusal in the panel", /isn't valid|refused|Couldn't check/.test(r.verdict), r);

    // delete + undo (2026-10-07): a removed video leaves Recent and the total;
    // undo restores it exactly; a refused id changes nothing
    const before = (await call(s.ev, "GET", "/api/repeat")).body;
    const entry = before.videos.find(v => v.id === OTHER);
    r = await call(s.ev, "POST", "/api/repeat-delete", {id: OTHER});
    check("delete removes the video and its loops from the total",
      r.status === 200 && !r.body.videos.some(v => v.id === OTHER) && r.body.total === before.total - entry.count, r.body);
    r = await call(s.ev, "POST", "/api/repeat-restore", {entry});
    const back = r.body.videos.find(v => v.id === OTHER);
    check("undo puts it back exactly", r.body.total === before.total && back && back.count === entry.count
      && back.first === entry.first && back.title === entry.title, {back, entry});
    r = await call(s.ev, "POST", "/api/repeat-delete", {id: "../../x"});
    check("delete refuses a bad id", r.status === 400, r);
    r = await s.ev(`(async () => {
      const $ = id => document.getElementById(id), wait = ms => new Promise(r => setTimeout(r, ms));
      await loadCounts(); await wait(200);
      const rows = () => [...document.querySelectorAll("#rp-recent li[data-id]")].map(li => li.dataset.id);
      const had = rows();
      document.querySelector('#rp-recent [data-del="${OTHER}"]').click(); await wait(700);
      const after = rows(), msg = $("rp-msg").textContent;
      $("rp-undo").click(); await wait(700);
      return {had, after, msg, undone: rows()};
    })()`);
    check("the × in Recent removes the row and offers Undo, which brings it back",
      r.had.includes(OTHER) && !r.after.includes(OTHER) && /Removed .*Undo/.test(r.msg) && r.undone.includes(OTHER), r);

    r = await s.ev(`(() => { const $ = id => document.getElementById(id), out = {};
      for (const v of ["https://youtu.be/jNQXAC9IVRw", "lofi"]) { $("rp-q").value = v; renderGo(); out[v] = $("rp-go").textContent; }
      server.search = true; $("rp-q").value = "lofi"; renderGo(); out.withKey = $("rp-go").textContent;
      server.search = false; $("rp-q").value = ""; renderGo(); return out; })()`);
    check("the button says Play for a link, Search for words once a key is saved",
      r["https://youtu.be/jNQXAC9IVRw"] === "Play" && r.lofi === "Play" && r.withKey === "Search", r);

    // ---- playlists (P2, 2026-10-08) ----
    const A = "jNQXAC9IVRw", B = "dQw4w9WgXcQ", C = "3TNpOD6bov8";
    r = await call(s.ev, "POST", "/api/playlist-create", {name: "Focus"});
    const pid = r.body.playlists[0].id;
    for (const [id, title] of [[A, "Me at the zoo"], [B, "Song B"], [C, "Song C"]])
      r = await call(s.ev, "POST", "/api/playlist-add", {pid, id, title});
    const dup = await call(s.ev, "POST", "/api/playlist-add", {pid, id: B, title: "Song B"});
    check("a playlist keeps songs in order, and adding one twice doesn't duplicate it",
      r.body.playlists[0].songs.map(x => x.id).join() === [A, B, C].join() && dup.body.already === true
      && dup.body.playlists[0].songs.length === 3, {r: r.body, dup: dup.body});
    r = await call(s.ev, "POST", "/api/playlist-order", {pid, ids: [C, A, B]});
    const stale = await call(s.ev, "POST", "/api/playlist-order", {pid, ids: [C, A]});
    const now1 = (await call(s.ev, "GET", "/api/playlists")).body.playlists[0].songs.map(x => x.id).join();
    check("reorder works, and an order that doesn't match the list (a stale page) is refused",
      r.status === 200 && stale.status === 400 && now1 === [C, A, B].join(), {stale, now1});
    const songA = (await call(s.ev, "GET", "/api/playlists")).body.playlists[0].songs[1];
    await call(s.ev, "POST", "/api/playlist-remove", {pid, id: A});
    r = await call(s.ev, "POST", "/api/playlist-insert", {pid, song: songA, index: 1});
    check("removing a song and undoing puts it back in its place",
      r.body.playlists[0].songs.map(x => x.id).join() === [C, A, B].join(), r.body);
    const copy = (await call(s.ev, "GET", "/api/playlists")).body.playlists[0];
    await call(s.ev, "POST", "/api/playlist-create", {name: "Gym"});
    await call(s.ev, "POST", "/api/playlist-delete", {pid});
    r = await call(s.ev, "POST", "/api/playlist-restore", {playlist: copy, index: 0});
    check("deleting a playlist and undoing brings it back, songs and place",
      r.body.playlists[0].id === pid && r.body.playlists[0].songs.length === 3 && r.body.playlists[1].name === "Gym", r.body);
    const badP = [];
    for (const [path, body] of [["/api/playlist-add", {pid, id: "nope"}], ["/api/playlist-create", {name: ""}],
                                ["/api/playlist-create", {name: "x".repeat(61)}], ["/api/playlist-add", {pid: "pzzz", id: A}],
                                ["/api/playlist-rename", {pid, name: "  "}]]) {
      const x = await call(s.ev, "POST", path, body); if (x.status !== 400) badP.push([path, body, x.status]);
    }
    check("bad ids, names and unknown playlists are refused", badP.length === 0, badP);

    // the page: Add to…, play a list, a finished song counts and moves on, skipping doesn't count
    r = await s.ev(`(async () => {
      const $ = id => document.getElementById(id), wait = ms => new Promise(r => setTimeout(r, ms));
      await loadCounts(); await loadLists(); showTab("recent"); await wait(200);
      const plus = document.querySelector('#rp-recent [data-add]'); plus.click(); await wait(200);
      const menuShows = !$("rp-menu").hidden && [...$("rp-menu").querySelectorAll("[data-to]")].map(b => b.textContent).join();
      $("rp-menu").querySelector('[data-to="${pid}"]').click(); await wait(500);
      const added = $("rp-msg").textContent;
      const life = id => (server.videos.find(v => v.id === id) || {count: 0}).count + pendN(id);
      playList("${pid}", null, false); await wait(800);
      const start = {pos: $("rp-pos").textContent, cur: cur.id, prevShown: !$("rp-prev").hidden, mode: $("rp-list").classList.contains("on")};
      const firstBefore = life(cur.id), first = cur.id;
      onState({data: YT.PlayerState.ENDED}); await wait(1500);
      const afterEnd = {cur: cur.id, pos: $("rp-pos").textContent, counted: life(first) - firstBefore};
      const skipped = cur.id, skippedBefore = life(skipped);
      $("rp-next").click(); await wait(800);
      const afterSkip = {cur: cur.id, pos: $("rp-pos").textContent, counted: life(skipped) - skippedBefore};
      playIdx(q.order.length - 1, false); await wait(500);
      onState({data: YT.PlayerState.ENDED}); await wait(1200);
      const wrapped = {pos: $("rp-pos").textContent, cur: cur.id, firstAgain: cur.id === q.order[0]};
      play({id: "${A}"}, 0, false); await wait(500);
      const single = {listMode: listOn(), prevShown: !$("rp-prev").hidden, posShown: !$("rp-pos").hidden};
      return {menuShows, added, start, afterEnd, afterSkip, wrapped, single, order: q.order};
    })()`);
    check("+ on a Recent row offers your playlists and adds to the one picked",
      typeof r.menuShows === "string" && r.menuShows.includes("Focus") && /Added to Focus|Already in Focus/.test(r.added), r);
    check("playing a playlist: Playlist mode on, 1 / N, previous/next shown",
      r.start.mode && r.start.pos === `1 / ${r.order.length}` && r.start.prevShown && r.start.cur === r.order[0], r.start);
    check("a song that plays to its end counts once and the next one starts",
      r.afterEnd.counted === 1 && r.afterEnd.cur === r.order[1] && r.afterEnd.pos === `2 / ${r.order.length}`, r.afterEnd);
    check("skipping with ⏭ moves on without counting", r.afterSkip.counted === 0 && r.afterSkip.pos === `3 / ${r.order.length}`, r.afterSkip);
    check("after the last song it starts again from the first", r.wrapped.firstAgain && r.wrapped.pos === `1 / ${r.order.length}`, r.wrapped);
    check("playing one video yourself leaves Playlist mode", !r.single.listMode && !r.single.prevShown && !r.single.posShown, r.single);

    // ---- security review (2026-10-08): only the app's own page may use the API ----
    const before2 = (await call(s.ev, "GET", "/api/repeat")).body.total;
    r = await s.ev(`Promise.all([
      fetch("/api/repeat-count", {method: "POST", body: JSON.stringify({id: "${VID}", n: 5})}).then(r => r.status),
      fetch("/api/repeat-count", {method: "POST", headers: {"X-App-Token": "0".repeat(32)}, body: JSON.stringify({id: "${VID}", n: 5})}).then(r => r.status),
      fetch("/api/settings", {method: "POST", body: JSON.stringify({ytKey: ""})}).then(r => r.status)])`);
    const after2 = (await call(s.ev, "GET", "/api/repeat")).body.total;
    check("an API call without the app's secret, or with a wrong one, is refused and changes nothing",
      r.every(x => x === 403) && after2 === before2, {statuses: r, before2, after2});

    // from inside YouTube's frame, as an ad or a hijacked player would try. Measured
    // 2026-10-08: WebView2 doesn't route that frame's requests to the app at all (they
    // fail as network errors even with the secret check off), so this guards that
    // path staying closed; the secret check above is what a caller that does get
    // through runs into.
    const yt = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => /youtube\.com\/embed/.test(t.url));
    if (yt) {
      const fws = new WebSocket(yt.webSocketDebuggerUrl); await new Promise(res => fws.onopen = res);
      const fr = await new Promise(res => { fws.onmessage = m => res(JSON.parse(m.data)); fws.send(JSON.stringify({id: 1, method: "Runtime.evaluate", params: {awaitPromise: true, returnByValue: true, expression:
        `Promise.all([
          fetch("https://on-repeat.example/api/repeat-delete", {method: "POST", mode: "no-cors", body: JSON.stringify({id: "${VID}"})}).then(() => "sent", e => "blocked"),
          fetch("https://on-repeat.example/api/repeat", {mode: "cors"}).then(r => r.status, e => "blocked"),
          fetch("https://on-repeat.example/repeat").then(r => r.text().then(t => t.length), e => "blocked")]).then(JSON.stringify)`}})); });
      fws.close();
      const still = (await call(s.ev, "GET", "/api/repeat")).body.videos.some(v => v.id === VID);
      if (process.env.SHOW_FRAME) console.log("frame saw:", fr.result.result.value, "| still there:", still);
      check("YouTube's frame can't reach the app: no delete, no API read, no page (and so no secret)",
        still && !/\d{3,}\]$/.test(fr.result.result.value || ""), {fromFrame: fr.result.result.value, stillThere: still});
    } else check("YouTube's frame can't delete anything (needs the player's frame to exist)", false, "no youtube.com/embed frame found");

    r = await s.ev(`(async () => { const before = location.href; location.href = "https://example.com/"; await new Promise(r => setTimeout(r, 1500)); return {before, after: location.href}; })()`);
    check("the window won't navigate away from the app's page", r.after && r.after.startsWith("https://on-repeat.example/"), r);

    r = await call(s.ev, "GET", `/api/ytinfo?id=jNQXAC9IVRw`);
    check("pasted link gets its title (oEmbed)", r.status === 200 && r.body.title === "Me at the zoo", r);
  } finally { await s.stop(); }

  // a damaged counts file is reported, never replaced by an empty one
  const counts = path.join(dir, "repeat-counts.json");
  fs.writeFileSync(counts, "{ this is not json");
  s = await start(dir);
  try {
    const r = await call(s.ev, "POST", "/api/repeat-count", {id: VID, n: 1});
    check("a damaged counts file is an error, and left as it was",
      r.status >= 400 && fs.readFileSync(counts, "utf8") === "{ this is not json", r);
    const plFile = path.join(dir, "playlists.json");
    fs.writeFileSync(plFile, "[ broken");
    const p = await call(s.ev, "POST", "/api/playlist-create", {name: "x"});
    check("a damaged playlists file is an error, and left as it was",
      p.status >= 400 && fs.readFileSync(plFile, "utf8") === "[ broken", p);
  } finally { await s.stop(); }

  fs.rmSync(dir, {recursive: true, force: true});
  console.log(failed ? `\n${failed} FAILED` : "\nall passed");
  process.exit(failed ? 1 : 0);
})().catch(e => { console.error(e); process.exit(2); });
