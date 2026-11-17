# Quantum Reality Shift V3

Adds two more Atlas engines on top of V2: `otoc-echo-v1` for timed shockwaves and `entanglement-shader-v1` for a live shader texture. Each trigger runs 5 jobs in parallel (6 credits).

## Hotkeys

- **F5**: Control menu (mode bias, Shader Vision overlay toggle, trigger, chemist)
- **F6**: Trigger a 30s quantum event
- **F7**: Toggle auto-trigger when your wanted level rises (20s cooldown)
- **F8**: Stop all effects and reset
- **F9**: Toggle the HUD (job IDs, 4×4 heatmap, 8-bar echo graph, shader thumbnail)
- **F10**: Summon the Quantum Chemist. Pay $10 for a 60s Shader Vision trip.

## Engines

| Engine | Input | Drives |
|--------|-------|--------|
| `blur-core-v1` (1D) | 8 weather weights, `shots=128`, `style="xy"` | Which mode activates |
| `blur-core-v1` (2D) | 4×4 car/ped grid | 3 pillar locations and the heatmap |
| `coin-toss-v1` | `mode="emu"`, `shots=16` | Flash length; 8+ heads links the Twin car |
| `otoc-echo-v1` | `depth=8`, `n_sites=4`, `lattice="chain"` | 8 shockwave beats, one every 3.6s. Positive beats heal and push; negative beats cause backlash (bigger blasts, lightning). |
| `entanglement-shader-v1` | `layers=2`, `resolution=32` | ZIP of `R_lut.hdr`/`T_lut.hdr`, decoded into the HUD texture and screen overlay |

Mode effects are the same as V2, scaled by the echo beats.

## Install

Story Mode only. Keep only one version's DLL in `GTA V\scripts\`.

1. Install ScriptHookV and ScriptHookVDotNet v3 (see the V4 README).
2. Copy `QuantumRealityShiftV3.dll`, `Newtonsoft.Json.dll`, and `LemonUI.SHVDN3.dll` into `GTA V\scripts\`.
3. Set `MOTH_API_KEY`, or copy `QuantumWeather.example.ini` to `GTA V\scripts\QuantumWeather.ini` and fill in `ApiKey`.

## Build

```powershell
dotnet build v3\QuantumRealityShiftV3.csproj -c Release
.\package_v3.ps1   # builds QuantumRealityShiftV3_Release.zip
```

## Troubleshooting

- F5/F10 menus don't open: `LemonUI.SHVDN3.dll` is missing from `scripts\`.
- Press F9 to confirm live calls. You should see `LIVE ATLAS (4 ENGINES)` and five job IDs.
- Load errors: check `GTA V\ScriptHookVDotNet.log`.
