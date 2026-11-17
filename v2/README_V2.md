# Quantum Reality Shift V2

Builds on V1 with a control menu, world markers, a street chemist, and the Entangled Twin car. Same three Atlas calls per trigger (4 credits).

## Hotkeys

- **F5**: Control menu (mode bias, trigger, summon chemist, HUD/auto toggles)
- **F6**: Trigger a 30s quantum event
- **F7**: Toggle auto-trigger when your wanted level rises (20s cooldown)
- **F8**: Stop the event and reset
- **F9**: Toggle the HUD (Atlas status, job IDs, 4×4 heatmap)
- **F10**: Summon the Quantum Chemist. Pay $10 for a 60s event.

## What's New

- **3D pillars** mark the 3 spots chosen by the 2D blur call.
- **Floating status cards** over affected cars and peds (e.g. `TRACTION: 0%`).
- **Entangled Twin**: if the coin toss gets 8+ heads out of 16, the nearest car is linked with a magenta line and mirrors your movement at 1.35x force.
- **Mode bias**: the F5 menu tilts the weather weights sent to Atlas.
- **Overclock pillars**: standing in a green pillar repairs your armor and car.

Modes and their effects are the same as V1.

## Install

Story Mode only. Keep only one version's DLL in `GTA V\scripts\`.

1. Install ScriptHookV and ScriptHookVDotNet v3 (see the V4 README).
2. Copy `QuantumRealityShiftV2.dll`, `Newtonsoft.Json.dll`, and `LemonUI.SHVDN3.dll` into `GTA V\scripts\`.
3. Set `MOTH_API_KEY`, or copy `QuantumWeather.example.ini` to `GTA V\scripts\QuantumWeather.ini` and fill in `ApiKey`.

## Build

```powershell
dotnet build v2\QuantumRealityShiftV2.csproj -c Release
.\package_v2.ps1   # builds QuantumRealityShiftV2_Release.zip
```

## Troubleshooting

- F5/F10 menus don't open: `LemonUI.SHVDN3.dll` is missing from `scripts\`.
- Nothing happens on F6: check `GTA V\ScriptHookVDotNet.log`.
- HUD shows `FALLBACK`: check your connection and API key.
