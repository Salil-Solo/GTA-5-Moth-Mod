# Quantum Reality Shift V1

The first prototype. Press F6 (or pick up a wanted star with auto-trigger on) and the mod sends a snapshot of the area to Moth Atlas, which picks one of four modes and three blast spots for the next 30 seconds. Without an API key or network it falls back to a local simulation.

## Hotkeys

- **F6**: Trigger a 30s quantum event
- **F7**: Toggle auto-trigger when your wanted level rises (20s cooldown)
- **F8**: Stop the event and restore weather, speed, and police
- **F9**: Toggle the status HUD (Atlas status, job IDs, odds)

## How It Works

1. A chaos score (0–1) is built from wanted stars, nearby peds, and low health (below 50 HP).
2. An 80m × 80m area around the player is split into a 4×4 grid, counting cars (+2) and peds (+1).
3. Three Atlas calls run in the background (4 credits per trigger):
   - `blur-core-v1` on 8 weather weights picks the mode.
   - `blur-core-v1` on the 4×4 grid picks 3 blast spots (at least 8m from the player).
   - `coin-toss-v1` (16 shots) sets the flash length. 8+ heads triggers SURGE, which makes blasts 1.35x stronger.

| Mode | Weather | Effect |
|------|---------|--------|
| Overclock | Clear | +35% sprint, +25 HP, -1 wanted star, slow regen |
| Time Dilation | Clouds/Fog | 3.5s slow-mo, cops ignore you for 7s |
| Quantum Storm | Rain/Thunder | Icy traffic, lightning, EMP blasts at the 3 spots every 4.5s |
| Singularity | Blizzard | Anti-gravity blasts every 4.5s that launch cars and ragdoll peds |

## Install

Story Mode only. Keep only one version's DLL in `GTA V\scripts\`.

1. Install ScriptHookV and ScriptHookVDotNet v3 in the GTA V folder (see the V4 README for links and the BattlEye note).
2. Build and copy `QuantumWeatherMod.dll` and `Newtonsoft.Json.dll` from `src\bin\Release\net48\` into `GTA V\scripts\`:
   ```powershell
   dotnet build src\QuantumWeatherMod.csproj -c Release
   ```
3. Set the `MOTH_API_KEY` environment variable, or copy `QuantumWeather.example.ini` to `GTA V\scripts\QuantumWeather.ini` and fill in `ApiKey`.

## API

All calls go to `https://api.mothquantum.com/api/v1`: `POST /engines/{id}/process`, then poll `GET /jobs/{id}/status` and fetch `GET /jobs/{id}/result`.

## Troubleshooting

- Nothing happens on F6: check `GTA V\ScriptHookVDotNet.log`.
- HUD shows `FALLBACK`: check your connection and API key. If you just set the environment variable, restart the game launcher.
