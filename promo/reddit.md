# Reddit (EN)

> Rule checks: I could not load subreddit rules automatically (Reddit blocks unauthenticated API requests), so check each sidebar right before posting. Typical rules for these subs are listed as a checklist — confirm them, don't assume.

## Checklist per subreddit

| Sub | Fit | Check before posting |
|---|---|---|
| r/ForzaHorizon (+ the FH6 sub if separate) | ★★★ main audience | Flair (often "Discussion" / "PC" / "Tools"); some Forza subs only allow self-promo on certain days or in a megathread; video posts may need to be native uploads. |
| r/ForzaHorizon5 | ★★★ | Same as above; FH5 players on PC still ask about DualSense often. |
| r/DualSense | ★★★ | Usually welcomes PC tools; use the "PC" / "Software" flair if present. |
| r/pcgaming | ★★ big but strict | Self-promotion rules are strict (account history, 10% guideline, sometimes "no tools/self-promo" outside designated threads). Skip if the account is new or rarely active there. |
| r/simracing | ★ | It's a sim-racing sub; Forza Horizon is arcade. Post only if "tools I made" posts are allowed and frame it around the trigger/pedal model. |
| r/PS5 | ✗ skip | It's about the console; PC tools are generally off-topic there. |

---

## Post 1 — r/ForzaHorizon / r/ForzaHorizon5

**Title:** I made a free app that gives the DualSense real adaptive triggers and haptics in Forza Horizon on PC

**Body:**

Forza Horizon on PC only gives the PS5 controller basic rumble — no adaptive triggers, no proper haptics. I got tired of that, so I wrote a small tool that reads Forza's built-in *Data Out* telemetry and drives the controller itself.

What you feel:
- **L2** behaves like a real brake pedal: free play, bite point, firmer towards the end — and when the wheels are about to lock, a "wall" rises behind your finger so you can threshold-brake by feel. ABS pulses, lock-up shudder.
- **R2**: wheelspin, rev limiter bounce, a kick on every gear change, optional kickdown detent.
- **Haptics** (the voice-coil motors, not rumble): engine pulse with a different character per engine (V8 burble vs V10/V12 vs rotary vs EV), weight shifting left/right in corners, hits from the side they come from, landings after jumps.

Two modes: **direct USB** (free ViGEmBus + HidHide drivers, no DSX needed, ~10 ms haptics) or **through DSX** if you already have it (works over Bluetooth too).

No injection or memory reading — it only listens to the UDP telemetry the game sends anyway. Single ~100 KB exe, open source (MIT), English/Russian UI, Steam and Game Pass versions both work.

Download + setup: https://github.com/GefryV3/ForzaTriggers-DualSense-Forza-Horizon

It's tuned on FH6, should work on FH5 as-is. Feedback very welcome — especially on the brake feel and which effects are too strong/weak.

*(Attach: 20–40 s clip from video-script.md, or the screenshot.)*

---

## Post 2 — r/DualSense

**Title:** Adaptive triggers + HD haptics for Forza Horizon 5/6 on PC — free tool, no DSX required

**Body:**

Sharing a tool I built for the DualSense on PC. Forza Horizon has no native DualSense support on PC, so this app reads the game's telemetry and writes trigger effects and audio-rate haptics straight to the controller.

Technical bits people here may care about:
- Direct USB mode writes raw trigger effects over HID and streams **16-bit / 48 kHz haptics** to the controller's motor channels (~10 ms), while a ViGEmBus virtual Xbox pad feeds the game. HidHide keeps the game from seeing the real pad twice.
- DSX mode sends the triggers over DSX's UDP API and plays haptics through DSX's audio path (Bluetooth haptics need DSX+).
- Left and right motors are driven independently: weight transfer, side impacts, engine character per cylinder layout.
- Works with DualSense Edge too.

Free / MIT / ~100 KB: https://github.com/GefryV3/ForzaTriggers-DualSense-Forza-Horizon

Happy to answer questions about the haptics side — getting voice-coil feel right without it turning into a buzzy mess was the fun part.

---

## Post 3 — r/pcgaming (only if allowed)

**Title:** Forza Horizon 6/5 on PC ignores DualSense adaptive triggers, so I made an open-source tool that adds them

**Body:** Use Post 1 body, shortened to the first two paragraphs + mode line + link. Add: "No ads, no telemetry of its own, MIT licence."

---

## Post 4 — r/simracing (only if allowed)

**Title:** Modelled a brake pedal on a gamepad trigger (DualSense, Forza telemetry) — feedback from real-pedal people welcome

**Body:** Focus on the pedal model: 10-zone travel curve, bite point, progression tied to measured deceleration, a "wall" at the slip ratio where the tyres are about to lock, ABS modes. Ask how it compares to load-cell feel. Link at the end.

---

## Comment snippets (for replies)

- *"Does it work with Bluetooth?"* — Triggers yes (DSX mode). Full haptics without DSX need USB, because that's the only way Windows exposes the controller's haptic audio channel.
- *"Ban risk?"* — None that I know of: no injection, no memory reading, it only reads the official Data Out UDP stream that SimHub and motion rigs use.
- *"SmartScreen warning?"* — The exe isn't code-signed (hobby project). Source is in the repo, you can build it yourself with build.cmd; SHA256 is in the release notes.
