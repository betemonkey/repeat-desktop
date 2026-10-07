# Testing checklist

How On repeat got tested (mostly by Monkey, by using it), what each pass found,
and the check that stops it coming back. Read before calling a change done.
Built from the 2026-10-07/08 sessions. Add a line each time a new defect
or preference miss turns up.

## Before saying "done"

- [ ] Run `node tests/check.js` and quote its result. If the app is open (and so
      locks `bin\`), build to `build-test\` and use `REPEAT_EXE`.
- [ ] Watch it run **in the real app window**, not only headless Chrome. Headless
      YouTube refuses to play ("owner doesn't allow"), so it proves nothing about playback.
- [ ] Watch it **in the browser or app Monkey actually uses** (Firefox, Orca's built-in
      browser, the app). Each one broke something the others didn't.
- [ ] **On both screens** (125% and 100%), and **with Windows Text size ≠ 100%**
      (Monkey's PC is at 117%).
- [ ] **Full → mini → folded → back**, and **mini dragged to the other screen → Esc**.
- [ ] With the **longest real title**, an **empty list**, and **one item deleted to empty**.
- [ ] **Resize the window to tiny.**
- [ ] For anything with sound, use **real audio** (at low volume). Muted means the bars
      stay flat whatever the code does.
- [ ] For a new check, **prove it can fail**: break the code it guards, run it, see it go red.

## Defects found, and the check now

| What Monkey saw | Cause | Check / rule now |
|---|---|---|
| Pop out "doesn't do anything" (Orca) | Orca's browser returns a 0×0 pop-out window and closes it 1 ms later | Detect an instant close and fall back; test in Orca, not only Chrome |
| Pop-out had Firefox's address bar | Firefox draws it; no page option hides it (MDN) | Say "can't" only with the doc line; ask before swapping features |
| Progress and counts not carried between tab and pop-out | Two players handing state back and forth | One window, one player (the app). Avoid hand-overs |
| Long title spilled out of the box, sideways scrollbar | grid `1fr` grows to fit content | `minmax(0,1fr)` + ellipsis + hover title; test the longest title |
| Mini window square corners | A frameless window must ask Windows 11 for rounded corners | `DwmSetWindowAttribute` corner preference |
| Counters cut off at the bottom of mini | Windows Text size 117% enlarges the page; the window was sized from DPI alone | The page reports its height in device pixels; test at Text size ≠ 100% |
| ✕ in mini looked like "quit" | The icon didn't say what it does | Icons say the action: "back to full" uses the expand arrows |
| Mini dragged to the other screen, Esc → full window mini-sized | Windows' DPI-change resize used the old (mini) size | Re-apply the full bounds after the DPI change; test both directions |
| Header looked off | Buttons and title on separate rows: an empty band | Review screenshots at the real window width |
| Window shrunk to a sliver | No minimum size | 640×480 minimum in full; none in mini |
| Bars flat while music played | Capturing the app's own process gets silence; the sound is in WebView2's process tree | Test with real sound; the capture status is sent to the page |
| Counts didn't save (first standalone run) | C# JSON number type (int vs long) | The first real end-to-end loop caught it; always run one |
| `"n": null` counted as 1 | Missing value vs explicit null | Validation matches the old server's; the checks send null, true, "3", 1.5 |
| Recent kept old rows after the last delete | The empty case returned early | Test delete-to-empty |
| Playlist rows squashed | New class `.grip` collided with an existing hidden `.grip` | Search for a class name before reusing it |
| Menu's "New playlist" box tiny | A broad rule (`.menu button{width:100%}`) leaked | Scope selectors (`.menu > button`) |
| A test failed on correct behaviour | It assumed 3 songs; the step before added a 4th | Derive expectations from state, never hard-code |
| API usable from any frame (review) | The app answered every request made in its window | Per-start secret on each call; a check proves calls without it are refused |
| A security check passed even with the protection off | That path was already closed by WebView2 | Mutation-test every new check |

## Monkey's preferences (each was missed once)

- **Mockup first.** Design changes go into `mockups/` as A/B/C options; the app changes
  only after a pick **and** an explicit "apply".
- **Call him Monkey**, never Simon.
- **Every reply opens with a TL;DR.** One 🟠 line per thing only he can do.
- **"Can't" needs evidence**: the doc line, the measurement, or a question instead.
- **Don't touch his running app.** Test on a separate build and throwaway data
  (`REPEAT_DESKTOP_DATA`). Never move, close or restart his windows.
- **Screenshots of the app's page only** (DevTools `Page.captureScreenshot`), never a
  screen grab: one grab once caught another window of his.
- **Undo for anything that deletes**, for 8 seconds.
- **Counters always visible**, in every mode.
- **Compact over chrome**: fewer bars and frames, more video.
- Before **sound tests**, say so and keep the volume low.
