# Native chat performance

Measurements of how the native chat performs in real use, and the rule for the values that depend on them.

## Rule: limits change only with a measurement

The resumed-transcript limits (`ResumedTranscriptMaxMessages`, `ResumedTranscriptMaxToolCards` in
`Controls/ClaudeCodeControl.NativeChat.cs`) and any other value chosen for performance (throttle intervals,
virtualization cache, caps on rendered content) may only change together with a measurement recorded in this
document: what was measured and how, on which machine, and the numbers before and after the change.
Twice before, such a value was set by feel and kept long after the reason behind it (every row rendered at
once) had stopped being true. `ReplayLimitsMatchTheMeasuredValues` (`Tests/ResumedTranscriptReplayTests.cs`)
pins the current limits, so a change cannot slip through review unnoticed; update it together with this file.

Add new measurements as a new dated section; keep the old ones, so the history of a value stays readable.

## Machine and method

All measurements below were taken by @Gh61 on one machine:

- **CPU:** AMD Ryzen AI 9 HX 370 (12 cores / 24 threads), **RAM:** 32 GB LPDDR5-7500 (about 79 % in use while
  measuring)
- **OS / IDE:** Windows 11 Pro, Visual Studio 2026 Community (18.x), Exp instance, Release build unless noted
- **Simulated slow machine:** the same Exp instance limited to 2 of the 24 logical cores
  (`ProcessorAffinity = 3`) with Windows power-saving mode on, about 2–2.5x slower (section 1c)

The numbers come from temporary probe code (not merged) that wrote `REPLAY-PROBE` / `REPLAY-SWEEP` /
`STREAM-PROBE` lines to `%LocalAppData%\ClaudeCodeExtension\terminal-launch.log`. Visual Studio's own work
shows in the numbers: a large solution open in the same instance made the streaming lag and stalls clearly
worse (section 2), so measure with a small solution open.

---

## 1. Resumed transcript replay (2026-10-26, v272.0 + virtualized transcript from #219)

What the probe measures:
- **parse**: reading and parsing the session JSONL off the UI thread.
- **addRows**: adding the replay rows to the transcript on the UI thread.
- **load total**: start of the replay until the dispatcher is idle after the first layout and render.
- **maxUiStall**: longest wait of an input-priority dispatcher operation, sampled every 15 ms
  (how long a click or keystroke would have waited).
- **scroll to top**: the probe pages from the bottom to the top (`PageUp`, waits for idle after each page),
  which realizes every row once, like a reader scrolling up through the history.

Limits: A = shipped (200 messages, 40 full tool cards), B = 200 messages / 500 cards (the #197 value),
C = 1000 / 1000 (no effective limit). Older tool calls beyond the card limit become one-line summaries.

The user's sessions hold almost no thinking text (current Claude Code stores it empty), so the thinking
replay that `a394bd4` turned off (267k of 315k characters in MartinWooChina's session) is not covered here.

| Session | Run | Shown msgs / total | Full cards / summarized | Rows | Parse | Add rows | Load total | Max UI stall (load) | Scroll to top | Page avg / p95 / max | Max UI stall (scroll) | Extent estimate → real |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `82cef722` | A | 108/108 | 40 / 340 | 203 | 79 ms | 6 ms | 1.01 s ¹ | 604 ms ¹ | 43 pages, 5.1 s | 119 / 502 / 819 ms | 690 ms | 6 351 → 27 564 px (changed on 28 of 43 pages) |
| `82cef722` | B | 108/108 | 380 / 0 | 488 | 107 ms | 60 ms | 0.60 s | 360 ms | 48 pages, 7.6 s | 157 / 310 / 767 ms | 474 ms | 20 740 → 31 366 px (40 of 48) |
| `3cff8799` | A | 141/141 | 40 / 569 | 275 | 161 ms | 1 ms | 0.91 s | 361 ms | 61 pages, 7.2 s | 117 / 219 / 986 ms | 819 ms | 22 417 → 39 534 px (45 of 61) |
| `3cff8799` | B ² | 141/141 | 500 / 109 | 646 | 240 ms | 14 ms | 1.15 s | 611 ms | 64 pages, 19.6 s | 306 / 857 / 4 800 ms | 1 346 ms | 34 368 → 41 940 px (48 of 64) |
| `3cff8799` | B | 141/141 | 500 / 109 | 646 | 75 ms | 8 ms | 0.57 s | 236 ms | 67 pages, 7.3 s | 108 / 221 / 549 ms | 372 ms | 44 460 → 41 940 px (48 of 67) |
| `c089b949` | B | 24/24 | 61 / 0 | 85 | 47 ms | 1 ms | 0.46 s | 282 ms | 12 pages, 0.9 s | 78 / 115 / 115 ms | 80 ms | 4 017 → 7 717 px (9 of 12) |
| `c07cb5fe` | C | 289/289 | 493 / 0 | 782 | 114 ms | 5 ms | 0.62 s | 218 ms | 83 pages, 9.6 s | 115 / 232 / 868 ms | 729 ms | 40 925 → 53 847 px (71 of 83) |
| `ab8a5228` | C | 218/218 | 622 / 0 | 840 | 91 ms | 9 ms | 0.45 s | 223 ms | 91 pages, 10.3 s | 113 / 221 / 1 245 ms | 1 065 ms | 71 558 → 59 209 px (76 of 91) |

¹ First replay after Visual Studio started (includes JIT).
² Restored automatically while Visual Studio was still starting up; not representative.

Session sizes: `c07cb5fe` 15.6 MB (289 msgs, 493 tool calls), `ab8a5228` 7.6 MB (218 msgs, 622 tool calls),
`3cff8799` 5.8 MB (141 msgs, 609 tool calls), `82cef722` (108 msgs, 380 tool calls).

Findings:
- With virtualization the load cost does not depend on the limits: a whole conversation (840 rows, 622 full
  cards) restores in 0.45–0.6 s with at most ~0.2 s UI stall. The 30 s stall that motivated the 40-card
  limit (`a394bd4`, before virtualization) is gone, at least without large historical thinking.
- Paging through history costs the same per page (~110–120 ms avg, ~220 ms p95) in every run; more content
  only means more pages.
- Occasional 0.7–1.2 s stalls while scrolling appear in every configuration, current limits included.
- The scrollbar jumps because the virtualized panel's extent estimate is far off (up to 4x too small) and is
  corrected on 65–85 % of the pages while scrolling up.

### 1b. Limits, virtualization cache, thinking and memory (2026-10-27, v274.0, `ClaudeCodeExtension.sln` open)

Automated sweep (probe `sweep=<session>`): the same session replayed into the same chat once per configuration,
twice over, the chat cleared between runs. Memory = managed heap after a full GC (`GC.GetTotalMemory(true)`),
before the replay, after the load, and after paging to the top (every row realized once).
Session `e28c5efc` (2026-10-14): 154 messages, 327 tool calls; without limits 481 rows, 722 K characters.
"C" = no limits. Thinking = synthetic 3 000 characters before each reply (the session has none recorded),
replayed as collapsed thinking rows.

| Config | Rows | Load pass 1 / 2 | Scroll to top 1 / 2 | Page avg/p95 pass 1 · pass 2 | Max UI stall scrolling 1 / 2 |
|---|---|---|---|---|---|
| A (200/40), cache 2 | 282 | 519 / 569 ms | 6.4 / 4.3 s | 114/329 · 76/130 ms | 751 / 265 ms |
| C, cache 1 | 481 | 363 / 357 ms | 6.4 / 7.0 s | 109/310 · 120/327 ms | 421 / 557 ms |
| C, cache 2 | 481 | 357 / 363 ms | 6.1 / 6.8 s | 105/177 · 117/299 ms | 1 047 / 836 ms |
| C, cache 4 | 481 | 457 / 289 ms | 8.4 / 6.0 s | 146/343 · 105/308 ms | 748 / 553 ms |
| C, cache 8 | 481 | 248 / 219 ms | 6.2 / 6.0 s | 108/279 · 104/171 ms | 358 / 888 ms |
| C, cache 2 + thinking (364 K chars) | 608 | 390 / 334 ms | 6.1 / 6.3 s | 96/169 · 99/177 ms | 691 / 629 ms |

Load UI stall stayed at 160–280 ms in every run. The extent estimate was corrected on 43–54 pages in every
config (cache does not change the scrollbar jumping).

Memory: the deltas swung between −38 and +43 MB with no relation to the configuration, Visual Studio's own
allocations dominate the managed heap. The replay data itself is small: 722 K characters ≈ 1.4 MB (UTF-16),
the thinking 364 K characters ≈ 0.7 MB. The difference between the configurations is far below the noise.

(A first attempt at 17:31 is invalid: the sweep started in two automatically reopened tabs at once.)

Findings:
- No limit, cache or thinking setting has a measurable effect on load time, scrolling or memory on this machine.
- A whole conversation without limits loads as fast as the shipped limits (~0.3 s vs ~0.55 s).
- Replaying thinking as collapsed rows costs nothing measurable, even at 364 K characters (more than the
  267 K that stalled MartinWooChina's resume before virtualization).
- A bigger virtualization cache does not reduce the *number* of extent corrections (43–54 pages in every
  config), but it makes the first estimate far better, so each correction is much smaller. Extent right
  after the load vs the real 37 105 px: cache 1 → 19 461 px (−48 %), cache 2 → 20 530 px (−45 %),
  cache 4 → 34 133 px (−8 %), cache 8 → 34 489 px (−7 %). With the shipped cache 2 the scrollbar starts
  about half off and the extent nearly doubles while scrolling up, which is the visible jumping; cache 4
  should make it much calmer. Cache 4 did not lengthen the load (289–457 ms) or the page cost
  (105–146 ms avg); cache 8 adds almost nothing over 4.
- Checked by eye (2026-10-27): the user scrolled `e28c5efc` up by hand with cache 8 and with cache 2 and saw
  no difference. The better estimate does not translate into a noticeable change, so the cache stays at 2.
- Suggested: raise the limits (or keep only a safety cap), replay thinking collapsed (always, or one checkbox),
  keep the cache at 2 pages without a setting. Before raising the defaults, check a slower machine (or simulate
  one, e.g. Visual Studio limited to 2 cores via processor affinity).

### 1c. Simulated slow machine (2026-10-27, v274.0)

Exp instance limited to 2 of 24 logical cores (`ProcessorAffinity = 3`) and Windows power-saving mode;
`ClaudeCodeExtension.sln` open, cache 2, same session `e28c5efc`. About 2–2.5x slower than the full machine
(parse 179 vs 89 ms, the first normal replay with the shipped limits 1.69 vs 0.67 s, max UI stall 844 vs 281 ms).

| Config | Rows | Load pass 1 / 2 | Load UI stall 1 / 2 | Scroll to top 1 / 2 | Page avg/p95 pass 1 · pass 2 | Max UI stall scrolling 1 / 2 |
|---|---|---|---|---|---|---|
| A shipped (200/40) | 282 | 1.68 / 1.21 s | 987 / 692 ms | 8.5 / 9.2 s | 137/220 · 148/198 ms | 1 258 / 1 122 ms |
| C (1000/1000) | 481 | 0.77 / 0.80 s | 529 / 409 ms | 13.3 / 13.2 s | 211/526 · 209/718 ms | 1 110 / 1 075 ms |
| C + thinking (364 K chars) | 608 | 0.81 / 0.64 s | 442 / 407 ms | 14.3 / 12.5 s | 207/486 · 180/336 ms | 1 658 / 1 139 ms |

The user also resumed a long conversation by hand in this mode: `b17fb691` with the shipped limits loaded in
2.9 s with a 1.35 s stall (first resume after start, JIT included).

Findings:
- Even on the slow machine the higher limits restore *faster* than the shipped ones (0.6–0.8 s vs 1.2–1.7 s)
  with shorter stalls: with the shipped limits the last rows include the large merged summary rows of older
  tool calls, which cost more to render than collapsed full cards.
- Paging through the history with full cards costs about 50 % more per page on the slow machine (~210 vs
  ~140 ms avg, p95 0.3–0.7 s); on the full machine there was no difference. Paging through all 481 rows takes
  13 s instead of 9 s, but each page stays around 0.2 s.
- Collapsed thinking costs nothing measurable here either.
- The ~1.1 s scrolling stalls appear in every config, the shipped limits included.
- Conclusion: raise the limits (1000 messages / 1000 cards) and replay thinking collapsed. A lower card limit
  (e.g. 200) would only spare slow machines some cost when paging deep into the history.

---

## 2. Streaming a long reply

Earlier numbers from #198 (2026-10-14, before virtualization), replies of about 13–17 K characters:

| | Before #198 | Throttling | Throttling + incremental markdown |
|---|---|---|---|
| UI updates per reply | 2 214 (one per chunk) | ~1 000 | ~900 |
| Lag: avg / p95 / max | 244 / 610 / 1 016 ms | 29 / 83 / 208 ms | 23 / 65 / 317 ms |
| Visible: avg / p95 | 274 / 645 ms | 62 / 112 ms | 55 / 96 ms |
| One update at 0–4 K → 12–16 K chars | grows with length | 22 → 40 ms | flat, 22–25 ms |
| Markdown while streaming | no | no | yes, avg 2.2 ms per update |

- Lag: chunk arrival on the reader thread → applied on the UI thread.
- Visible: chunk arrival → next layout pass.
- One update: Text change notification → next layout pass.

### 2026-10-27, v274.0 (virtualized transcript #219, Release build, chat tab, following the end)

Prompt: a long how-to without tools (headings, lists, tables, C# code blocks).

Runs 1 and 2 had the large `Reservanto.sln` open in the Exp instance; later runs use the small
`ClaudeCodeExtension.sln` to rule that out.

| | #198 throttling + incremental markdown | v274.0 run 1 (Reservanto) | v274.0 run 2 (Reservanto) | v274.0 run 3 (small sln) | v274.0 run 4 (small sln) |
|---|---|---|---|---|---|
| Reply length | 13–17 K chars | 25.7 K chars, 2 113 chunks, 106 s | 13.1 K chars, 2 341 chunks, 78 s | 10.3 K chars, 1 830 chunks, 61 s | 9.8 K chars, 1 816 chunks, 61 s |
| UI updates per reply | ~900 | 1 470 | 1 181 | 983 | 962 |
| Lag: avg / p95 / max | 23 / 65 / 317 ms | 59 / 166 / 697 ms | 41 / 126 / 257 ms | 33 / 85 / 187 ms | 30 / 81 / 222 ms |
| Visible: avg / p95 / max | 55 / 96 ms / – | 66 / 180 / 700 ms | 43 / 128 / 258 ms | 35 / 85 / 189 ms | 33 / 82 / 223 ms |
| One update: avg (p95) by length | 22–25 ms, flat | 0–4 K 4.7 (9.6) · 4–8 K 7.8 (14.3) · 8–12 K 8.4 (13.8) · 12–16 K 8.0 (19.2) · 16–20 K 8.9 (22.7) · 20–24 K 9.2 (22.1) · 24–28 K 6.6 (15.4) ms | 0–4 K 1.6 (2.8) · 4–8 K 1.7 (2.4) · 8–12 K 1.8 (2.9) · 12–16 K 2.0 (3.0) ms | 0–4 K 1.8 (3.2) · 4–8 K 1.8 (2.9) · 8–12 K 1.9 (2.7) ms | 0–4 K 1.9 (3.3) · 4–8 K 2.0 (3.4) · 8–12 K 2.3 (3.1) ms |
| Markdown per update: avg / p95 / max | avg 2.2 ms | 1.9 / 4.4 / 27.3 ms | 0.6 / 1.1 / 37.2 ms | 0.7 / 1.4 / 4.1 ms | 0.7 / 1.5 / 2.9 ms |
| Final whole rebuild | ~83 ms | 118 ms | 45 ms | 40 ms | 46 ms |
| Max UI stall during the reply | – | 661 ms | 93 ms | 100 ms | 111 ms |

Findings:
- Rendering is far cheaper than in #198: one update (change notification → layout) costs ~2 ms instead of
  22–25 ms and does not grow with the reply length; markdown ~0.7 ms per update; the final whole rebuild
  40–46 ms instead of ~83 ms. The ~20 ms per-update layout cost #198 left open came from the
  non-virtualized list, and #219 removed it.
- Text becomes visible sooner than in #198: 33–35 ms avg (p95 82–85 ms) from chunk arrival to layout,
  vs 55 ms (p95 96 ms).
- Lag (arrival → applied on the UI thread) on the small solution is close to #198: 30–33 ms vs 23 ms avg.
  What remains is waiting for the UI thread while the events are handed over (hypothesis: the priority
  they are marshalled with, queuing behind layout and render; unverified). No stall above ~110 ms, so it
  is not the rendering. Unknown whether #198 was measured on a Release build.
- A large solution open in the same Visual Studio skews the numbers: with `Reservanto.sln` lag was 41–59 ms
  and stalls reached 661 ms. That is Visual Studio's own work on the big solution, not the chat.
- Conclusion: streaming is in better shape than after #198; nothing here needs optimizing. The only
  headroom is the ~30 ms hand-over wait, which is not noticeable in use.
