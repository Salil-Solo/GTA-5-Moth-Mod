# Quantum Reality Shift

A single-player GTA V mod I built for the Moth Quantum × GTA V Mod Bounty. Pressing F6 sends a snapshot of what's going on around you (wanted level, traffic, crowds) to Moth's Atlas API, and the result decides which of four "reality modes" the city flips into for the next 30 seconds.

Start with the [V4 README](v4/README_V4.md) for hotkeys, gameplay and install steps.

## Repo layout

| Folder | What it is |
|--------|------------|
| `v4/` | Current version, and the one I submitted |
| `v3/` | Added the OTOC echo and entanglement shader engines |
| `v2/` | Added the F5 menu, HUD, 3D markers and the street chemist |
| `src/` | V1, the first working prototype (blur + coin toss only) |
| `docs/` | `rough-idea.txt` (the original concept and how it changed) and `dev-notes.md` (AI-generated technical notes based on that idea: Atlas API, testing, version history) |

I kept the older versions as seperate projects instead of branches so I could load any of them in-game and compare quickly.

## Design notes

Some decisions that weren't obvious at the start:

- **Nothing touches the network on the game thread.** Atlas jobs are submit-then-poll and can take a few seconds. Everything runs async, and the game only applies the result once it's back. Blocking the script thread would freeze GTA.
- **The wait is hidden with a charge-up effect.** In V3, pressing F6 gave you 1–3 seconds of nothing. In V4 the charge-up (camera shake, glow, sound) starts immediately, so the latency feels intentional.
- **The shader doesn't hold up the shift.** `entanglement-shader-v1` returns a ZIP and is the slowest call. The mode is applied as soon as the four JSON jobs finish, and the shader texture is patched into the HUD whenever it arrives.
- **Hotspots are fixed to the world.** In V3 they were stored as offsets from the player, so they slid down the road as you moved. V4 snaps them to street positions once, at collapse time, so you can lure cops into a storm ring or drive around one.
- **Compact HUD by default.** The full telemetry panel is useful for proving the calls are live, but it covered too much of the screen for actual play. F9 cycles between the two.
- **Live vs. fallback is always visible.** If there's no key or the network fails, the mod still runs a local simulation, but the HUD says `FALLBACK` instead of `LIVE ATLAS`. That way I never accidentally recorded fake output.
- **I wrote my own HDR decoder.** The shader engine outputs Radiance `.hdr` lookup tables. I didn't want to pull in an imaging library for one format, so `ShaderTextureBuilder.cs` parses the RGBE data directly (both flat and RLE scanlines).

## Building from source

You need the .NET SDK (the projects target .NET Framework 4.8).

1. Put `ScriptHookVDotNet3.dll` and `LemonUI.SHVDN3.dll` in `src/lib/`. They're third-party, so they aren't committed.
2. Run:
   ```powershell
   .\package_v4.ps1
   ```
   This builds the release DLL and writes `QuantumRealityShiftV4_Release.zip` (mod DLL, dependencies, example ini, README).

If `GtaScriptsPath` in `v4/QuantumRealityShiftV4.csproj` points to your `GTA V\scripts\` folder, a normal build copies the mod there too.

## API key

The key is read from the `MOTH_API_KEY` environment variable first, then from a local `QuantumWeather.ini`, which is gitignored. It's never written to the log.

Story Mode only, don't use this in GTA Online.
