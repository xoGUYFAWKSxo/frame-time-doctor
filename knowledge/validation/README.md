# Validation records

Each file here records one real before/after test of one rule. The impact ranges in `../rules.json` should
only change when a record here backs the change, and a rule's `evidence` list should name the records behind it.

A rule can be marked `"validated": true` once its range holds across several different systems and at least
two games. Until then it keeps its "estimate" label in reports.

## How to run a test

1. Use a game with a built-in benchmark and write down every graphics setting.
2. Do a warm-up pass (shader compilation), then **2–3 passes before** the change.
3. Make exactly **one** change. Confirm nothing else moved (`system.json` in each session, or the game's settings log).
4. Do **2–3 passes after**.
5. Compare averages across runs, not one pass against another. Use one measurement source for all runs:
   PresentMon captures and game logs report different absolute numbers.

For Cyberpunk 2077, the game already logs every benchmark run, so step 2 and step 4 can come straight from
`ftd import "Documents\CD Projekt Red\Cyberpunk 2077\benchmarkResults"`.

## Record format

JSON with comments. Required fields: `rule`, `date`, `change`, `otherChanges`, `system`, `game`, the per-run
numbers, `result` (measured deltas, what the tool predicted, verdict), `noise`, and `knowledgeBaseChange`.
