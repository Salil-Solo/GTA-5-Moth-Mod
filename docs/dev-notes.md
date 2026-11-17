# Dev Notes

## From idea to mod

The rough idea was a street dealer selling a "quantum medicine" that gives you a trip, and then a quantum-generated maze to escape. The maze needed a custom map and teleporting in and out, so it was dropped. What survived became the mod:

| Rough idea | What it became |
|------------|----------------|
| The drink gives you a warped, powerful state | Four "reality modes" (Overclock, Time Dilation, Quantum Storm, Singularity), each with its own powers and hazards |
| The stranger on the corner | The F10 Quantum Chemist (V2), with three tiers in V4 ($10 / $100 / $500) |
| Waking up broke in an alley if you fail | The $500 Overdose ends in a 4-star "hangover" |
| Entangled NPCs that feel each other's hits | The Linked Twin car, which mirrors you and can be thrown |
| Quantum randomness decides what happens | Live game state is sent to Moth Atlas, and the engines' output picks the mode, the locations and the timing |

## How an Atlas call works

Base URL: `https://api.mothquantum.com/api/v1`, with the key sent as a Bearer token.

1. `POST /engines/{engine}/process` with a JSON body. This returns a job id.
2. Poll `GET /jobs/{id}/status` every 1s (max 30 tries) until `completed`, `failed` or `cancelled`.
3. `GET /jobs/{id}/result` returns the output.

`entanglement-shader-v1` returns a presigned S3 URL to a ZIP rather than JSON. That download must be sent without the Authorization header, so `AtlasService.cs` uses a second HttpClient for it.

Other details:
- TLS 1.2 is forced at startup (.NET Framework 4.8 doesn't default to it).
- Timeouts: 15s for API calls, 20s for the ZIP download.
- All calls are async. Nothing runs on the GTA script thread, so the game never freezes.

## Engine inputs and effects (V4)

| Engine | Input | Effect in game |
|--------|-------|----------------|
| `blur-core-v1` (1D) | 8 weather weights from chaos (wanted stars, nearby peds, low HP) plus the F5 bias. `reach=0.5`, `shots=128`, `style="xy"` | The 8 outputs are used as probabilities and one is sampled, which picks the mode. A calm city can still roll a storm. |
| `blur-core-v1` (2D) | 4×4 grid over an 80m area (car = +2, ped = +1). `reach=0.7`, `shots=128` | The top 3 cells are snapped to streets as anomaly rings, and also shown as the F9 heatmap |
| `coin-toss-v1` | `mode="emu"`, `shots=16` | heads/shots sets the flash strength. Half or more heads = Linked Twin car |
| `otoc-echo-v1` | `depth=8`, `n_sites=4`, `lattice="chain"`, `exact=true`, plus `kick`/`disorder` from chaos | 8 values become 8 beats over 30s. Positive beats pull things in and trigger slow-mo; negative beats are blasts |
| `entanglement-shader-v1` | `absorption`, `reflectance`, `interaction`, `layers=2`, `resolution=16`, plus a `style` chosen by the bias | `R_lut.hdr` + `T_lut.hdr` are decoded into a texture for the HUD and the chemist overlay |

Each trigger is 5 jobs, about 6 credits (coin toss is 2, the rest are 1 each). The shift starts once the 4 JSON jobs return (~4–6s). The shader texture appears when its ZIP arrives.

## Testing without credits

Set `OfflineTestMode=true` in `GTA V\scripts\QuantumWeather.ini` to replace every engine with a local simulation, with no network calls. Set it back to `false` for live runs.

In live runs, F9 (full telemetry) shows `LIVE ATLAS` and real job ids. `FALLBACK` means a call failed; check `QuantumRealityV4.log` in the scripts folder.

## Loading issues

- **BattlEye** blocks ScriptHookV. Disable it in the Rockstar launcher, or launch with `-nobattleye`.
- **On game builds 3258+**, SHVDN 3.6.0 stable throws a NativeMemory error. Use the 3.7.0 nightly.
- **Keep only one version's DLL in `scripts\`.** All versions use the F-keys.
- **After setting `MOTH_API_KEY`**, restart the launcher so the game sees it.

## Version history

- **V1**: weather and modes from blur 1D, 3 blast spots from blur 2D, SURGE from coin toss.
- **V2**: F5 menu, F10 chemist, 3D pillars, floating tags, 4×4 heatmap, Entangled Twin car.
- **V3**: `otoc-echo-v1` (8 timed shockwaves) and `entanglement-shader-v1` (live texture).
- **V4**:
  - Gameplay polish: a charge-up effect while waiting, rings fixed to the world, compact HUD by default.
  - Features: a throwable Twin car, three chemist tiers, and the Instability → Containment loop.
  - Quantum Blink (a teleport dash) was in briefly and was replaced with a speed boost.
