# Frame Time Doctor

Measurement-driven diagnostics for PC gaming performance. Run a benchmark pass, get a ranked,
plain-language report of what's misconfigured, how much fixing it should help, and how to fix it —
then re-test and see the before/after delta. See [docs/PROJECT_PLAN.md](docs/PROJECT_PLAN.md) for the product plan.

v1 targets **AM5 + NVIDIA** on Windows 10/11.

## Quick start

Requires the .NET 8 SDK. PresentMon 2.6.0 (MIT, Intel) is bundled in `tools/`.

```bash
dotnet build
```

```bash
src/FrameTimeDoctor.Cli/bin/Debug/net8.0-windows10.0.19041.0/ftd.exe probe
```

| Command | What it does | Admin? |
|---|---|---|
| `ftd probe` | Configuration checks that need no benchmark (memory profile, cores/SMT, drivers, power plan) | No |
| `ftd games` | Lists supported benchmark titles and their repeatable sequences | No |
| `ftd capture --game cyberpunk` | Waits for the game, starts on **Ctrl+Shift+F10** (or Enter), captures frames + telemetry, writes the report | **Yes** |
| `ftd capture --process X.exe --baseline latest --label expo-on` | Re-test against an earlier pass; report shows deltas and resolved findings | **Yes** |
| `ftd import file.csv [--baseline <id>]` | Diagnose an existing PresentMon CSV (no telemetry, so fewer checks) | No |
| `ftd analyze <id\|latest> [--baseline <id>]` | Re-run diagnosis on a stored session (e.g. after a knowledge-base update) | No |
| `ftd list` | Lists stored sessions | No |

PresentMon uses ETW, which needs an elevated terminal or membership in *Performance Log Users*.
Sessions are stored in `Documents\FrameTimeDoctor\sessions` (override with `FTD_SESSIONS`); each one is a
folder with `presentmon.csv`, `system.json`, `telemetry.json`, `session.json` and `report.html`.

## Architecture

```
src/FrameTimeDoctor.Core
  Capture/     PresentMon runner + CSV parser (column-name based; handles 1.x, 2.x and 2.4+ layouts)
  Telemetry/   SystemProbe (WMI, powercfg, nvidia-smi, registry) and TelemetrySampler
               (nvidia-smi @500 ms, per-core effective clock via perf counters, per-process CPU time)
  Analysis/    FrameStats: avg, 1% / 0.1% lows (P99 / P99.9 frame time), std dev, stutters/min, GPU-busy ratio
  Knowledge/   Loads knowledge/rules.json
  Rules/       RuleEngine + one class per check (detection logic only)
  Sessions/    Session storage, Diagnosis, before/after Comparison
  Report/      Self-contained HTML report (inline SVG chart, light/dark, no scripts)
  Benchmark/   Game profiles (knowledge/games.json)
src/FrameTimeDoctor.Cli   ftd.exe
tests/                    xUnit: parser, stats, every rule incl. false-positive cases
knowledge/                rules.json (the rules table) and games.json (benchmark harness)
```

**The rules table is split deliberately.** Detection logic (the telemetry signature) is C# in `Rules/`.
Everything an expert tunes — titles, explanations, fix steps, impact ranges, CPU specs, driver floors,
known background offenders — is in `knowledge/rules.json`, so it can change without a code release.

**Findings are measurement-gated to limit false positives.** Each rule scales its expected impact by what
actually limited the run (GPU-bound runs discount CPU/memory fixes), and rules defer to root causes:
thermal throttling suppresses the PBO advice, and a Balanced power plan is only flagged when the capture
shows CPU-bound clock dips (AMD recommends Balanced on Ryzen).

| Check | Signal | Needs capture |
|---|---|---|
| `memory-profile-off` | Configured MT/s below rating decoded from the kit part number | No |
| `cores-disabled` | Logical processors below the CPU spec (SMT/CCD/msconfig) | No |
| `power-plan` | Power saver, or Balanced + CPU-bound + max-core clock dips | Balanced only |
| `pbo-stock` | p90 max-core clock < 93% of rated boost on a CPU-limited run, no throttling | Yes |
| `background-processes` | Non-game processes ≥ 0.5 cores (or known offenders ≥ 0.15) during the run | Yes |
| `gpu-driver-outdated` | Driver older than KB floor, or older than 150 days | No |
| `chipset-driver` | AMD Chipset Software missing/old; weighted up on dual-CCD X3D | No |
| `thermal-throttling` | NVIDIA thermal-slowdown flags; CPU clock heat-soak over the run | Yes |
| `frame-cap-below-refresh` | Flat frame times well below refresh with GPU headroom | Yes |
| `vsync-judder` | V-Sync on, ≥ 3% of frames held for two refresh intervals | Yes |
| `composed-presentation` | Frames presented through DWM composition instead of independent flip | Yes |

## Status against the build plan

1. **Capture pipeline** — done. PresentMon wrapper, CSV parsing and telemetry are implemented. A live capture hasn't been run yet (needs an elevated terminal and a game).
2. **Rules table v0** — done. 11 checks. Impact ranges are **provisional** (`"validated": false`) until milestone 5.
3. **Report generator** — done. Baseline metrics, ranked findings, before/after deltas, resolved findings, noise band.
4. **Benchmark harness** — first cut. Three profiles (Cyberpunk 2077, Shadow of the Tomb Raider, CS2), hotkey start, fixed duration.
5. **Self-test on real rigs** — next.

## Known gaps / next steps

- **Run a real capture** in an elevated terminal and check the parser against live PresentMon 2.6 output.
- **Validate every rule** with before/after captures, then set `validated: true` and real impact ranges.
  Measure run-to-run noise (3+ identical passes) to replace the provisional 3% / 5% noise band.
- **CPU temperature**: Windows has no driver-free Ryzen temperature API, so CPU throttling is inferred from
  clock drift. Options: read an HWiNFO shared-memory/CSV log, or ship a signed sensor driver (v2).
- **RAM timings** (not just speed) need SMU/SPD access; the part-number decoder covers major vendors only
  and deliberately returns "unknown" rather than guessing.
- **Driver/chipset version floors** in `rules.json` are null; keep them current or fetch them from a hosted feed
  (relates to the plan's open "local tool vs service" question).
- Replace nvidia-smi with direct NVML calls, and consider the native ETW consumer from the plan once overhead is measured.
- Anti-cheat: verify each game in `games.json` before promoting it.

## Licensing

PresentMon is MIT-licensed (Copyright Intel Corporation); `tools/PresentMon-LICENSE.txt` must ship with it.
Bundling it in a commercial product is allowed as long as that notice ships too.
