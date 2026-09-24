# Frame Time Doctor — V1 Product Plan

As of 2026-09-23

## Overview

**Frame Time Doctor** is a diagnostic product for PC gamers who suspect their system is underperforming but don't know why. Instead of another monitoring overlay that shows numbers without context, it runs a structured benchmark pass, compares the results against a rules-based knowledge base of known misconfigurations, and hands back a ranked, plain-language report: here's what's wrong, the expected gain from fixing it, and how to fix it.

**Core value prop:** measurement-driven diagnosis instead of generic tuning advice. The market is full of tools that display frame time and utilization graphs (MSI Afterburner, HWiNFO) and full of forum threads with generic tips. Nothing combines the two into "here is your specific problem, here is the specific fix, here is the proof it worked."

**Target user for v1:** enthusiast and semi-enthusiast PC gamers on AM5 + Nvidia builds who've already spent money on hardware and suspect they're leaving performance on the table, but don't have the depth of knowledge to diagnose it themselves. This is a narrower, more technical buyer than the mass market — someone who already knows what a 1% low is, or is willing to learn what it means.

**Why this over a broader "AI reimagines your PC" pitch:** narrow scope means a sellable v1 in weeks, not months, and it validates willingness to pay before any investment in a live agent or polished GUI.

## Product scope (v1)

**Input:** the user runs a benchmark pass through the tool while playing (or running a canned benchmark sequence in) one supported game.

**Capture layer:**
- Frame time data via PresentMon (per-frame, not sampled averages)
- CPU/GPU utilization, clock speeds, and temps during the same window
- RAM speed and timings (catches EXPO/XMP being off, a high hit-rate finding)
- OS-level state: power plan, background process load, GPU driver version

**Output metrics (primary):**
- 1% low and 0.1% low frame times — the stutter-sensitive numbers, not just average FPS
- Frame time variance/consistency, which is what a player actually feels as stutter

**Diagnosis:**
The captured data is checked against a rules table (detailed below) built from known misconfigurations. Each match produces a specific finding: what's wrong, why it hurts performance, and the expected impact of fixing it.

**Report (the actual product):**
- Baseline numbers from the initial pass
- Ranked list of findings, ranked by expected impact
- Plain-language fix instructions per finding (no jargon dump)
- A re-test step: user applies one fix, re-runs the benchmark, sees the before/after delta

**User does the fixing.** The tool diagnoses and recommends; it doesn't change system settings itself in v1. This keeps the trust and liability bar low while still delivering the core value: a specific, measured answer instead of generic advice.

## Out of scope for v1

- **Live/background monitoring or auto-tweaking.** A persistent agent that changes system settings on its own is a support and liability problem before the diagnostic value is even proven. Save this for v2+, once the rules engine and report format are validated.
- **Full hardware coverage.** v1 targets AM5 + one GPU vendor (Nvidia). Intel, older AM4 boards, and AMD GPUs come later.
- **Polished GUI.** A CLI or a simple local web dashboard is enough to prove the concept. Design investment comes after paying customers exist.
- **Every game.** Support 1–3 well-known, benchmark-friendly titles (ones with built-in benchmark modes or predictable repeatable sequences) rather than trying to generalize to anything a user might launch.

## Technical architecture

**1. Capture layer**
A thin wrapper around PresentMon (Intel's open-source frame time capture tool) to log per-frame data during a timed benchmark window. Runs alongside supplementary telemetry pulls: GPU/CPU stats via existing Windows performance counters or vendor APIs (NVML for Nvidia, similar for AMD later), plus RAM/BIOS state readable via WMI or a small driver-level read where needed.

**2. Rules engine**
A C#/.NET service that ingests the capture output and runs it against a rules table (see below). This is deliberately NOT machine learning for v1 — a hand-built, expert-authored rules table is more reliable, easier to explain to a user, and easier to validate than a model you can't fully account for. "AI" framing in marketing is fine; the internals should be boring and correct.

**3. Report generator**
Takes rule matches + capture data and produces the output report: ranked findings, plain-language explanations, before/after comparison view. Could be a simple local HTML report (easiest to build, easy to make shareable/screenshottable for word-of-mouth) or a PDF export.

**4. Benchmark harness**
Standardizes what "run a benchmark pass" means — either driving a game's built-in benchmark mode where available, or giving the user a scripted, repeatable in-game sequence to follow so pass-to-pass comparisons are valid. Without this, before/after comparisons are meaningless (different play sessions aren't comparable).

**Why C#/.NET + a C++/Rust ETW consumer:** .NET gives fast UI/service development; ETW (Event Tracing for Windows) is the actual data source PresentMon uses, and a native consumer avoids overhead that would contaminate the very measurements you're trying to take.

## Rules table — initial checks to build

This table is the actual product. It's where hardware expertise turns into something sellable, and it should grow over time.

| Check | Signal detected | Typical fix |
|---|---|---|
| Memory profile off | RAM running at JEDEC default instead of rated speed | Enable EXPO (AMD) / XMP (Intel) in BIOS |
| Power plan not set to Performance | Windows set to Balanced, causing clock throttling under load | Switch to High Performance/Ultimate Performance plan |
| PBO/Curve Optimizer left at stock | CPU not reaching expected boost clocks | Suggest PBO Enhancement level + Curve Optimizer offset appropriate to cooling (80°C for air/240mm AIO, 90°C for 360mm) |
| Background process overhead | High CPU utilization from non-game processes during capture | Flag specific offending processes by name |
| Outdated GPU driver | Driver version behind current stable release | Recommend specific driver version to install |
| GPU power limit / frame cap conflicts | V-sync and in-game frame cap fighting each other, or a cap set below monitor refresh unnecessarily | Recommend consistent single source of frame limiting |
| Chipset drivers outdated | Affects PCIe/memory subsystem performance on AM5 boards | Point to current chipset driver package |
| Thermal throttling | Sustained clock drops correlating with high temps | Flag as a cooling issue, distinct from a settings issue |

Each check should map to: (1) the specific telemetry signature that triggers it, (2) an expected performance impact range based on real testing, and (3) plain-language fix steps.

## Business model and pricing (v1)

**Model: one-time paid diagnostic.** Not a subscription for v1 — a subscription implies ongoing value delivery (live monitoring, regular re-checks), which is v2+ territory. A one-time report is a simpler sale, has no churn to manage, and matches what the product actually does right now.

**Suggested price point:** $10–25 one-time. Cheap enough to be an impulse buy, expensive enough to filter for people who take it seriously and to cover diagnostic compute (cheap since it runs locally).

**Upsell path (not v1, but worth designing toward):** once the rules table and report format are proven, a subscription tier could offer periodic re-checks or a live-agent auto-fix version. v1's job is to prove people will pay for the diagnosis at all before building that.

**Distribution cost is near zero** if this ships as a downloadable local tool rather than a hosted service — no server costs to run someone else's benchmark pass, which keeps margins high even at a low price point.

## Go-to-market: initial customer acquisition

**Primary channel: r/pcmasterrace, r/buildapc, r/nvidia, r/Amd, and similar communities.** These are exactly where the target buyer already hangs out asking "why is my new PC stuttering." Sharing before/after report screenshots as proof of value is more persuasive than any ad copy.

**Content as acquisition:** writing genuinely useful diagnostic content ("why your 1% lows are bad even though average FPS looks fine," etc.) builds credibility and search traffic before the product exists — this can start now, independent of the build timeline.

**Direct proof over claims:** the report format itself (baseline → fix → re-test delta) is the marketing asset. A handful of real before/after examples is worth more than any feature list.

**Avoid:** paid ads before the product is validated. This audience is ad-skeptical and technical; word-of-mouth from a genuinely useful free or cheap first version will outperform spend at this stage.

## Build plan and milestones

1. **Capture pipeline working end to end on your own rig.** PresentMon logging + parsing script producing clean frame time data. This is the foundation.
2. **Rules table v0 (5–8 checks).** Start with the checks above. Write detection logic and plain-language fix text for each.
3. **Report generator.** Turn capture data + rule matches into the before/after report format. Simple HTML output is fine to start.
4. **Benchmark harness.** Pick 1–2 games with reliable built-in benchmark modes or scriptable sequences.
5. **Self-test on your own AM5 rig and a second rig if available.** Validate that findings are accurate and fixes actually produce the claimed improvement before anyone pays for this.
6. **Wrap in a minimal interface** (CLI or simple local web dashboard) once the pipeline works.
7. **Beta with a small group** before public launch — catches false-positive rule matches and report-clarity issues cheaply.
8. **Public launch** with real before/after proof from the beta.

## Risks and open questions

- **False positives in the rules table are the biggest trust risk.** A wrong diagnosis kills word-of-mouth fast in a technical community. Every rule needs to be validated against real before/after data before shipping.
- **PresentMon licensing/distribution.** It's open source (Intel), but confirm the license terms allow bundling it inside a commercial product before building around it.
- **Anti-cheat conflicts.** Some games with kernel-level anti-cheat (Valorant, Fortnite) may flag or block frame-capture tools running alongside them. Test per-title before committing to which games v1 supports.
- **Narrow addressable market at first.** AM5 + Nvidia + a couple of specific games is a small slice of PC gamers. Intentional for v1 validation, but plan the expansion path so the rules table architecture doesn't need a rewrite to scale.
- **Open question: packaged software vs. a report-as-a-service.** Does the user download and run a tool locally (simpler, no server costs, but harder to update the rules table without a new release), or upload their capture data to a service that runs the diagnosis (easier to iterate the rules table, but now you're hosting infrastructure and handling user data)? This affects the whole architecture, so it's worth deciding early.
