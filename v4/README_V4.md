# Quantum Reality Shift V4

This is the version I submitted to the Moth Quantum Mod Bounty. The game sends its live state to Moth's Atlas API, and what comes back drives the weather, vehicle physics, street traps and your abilities for the next 30 seconds.

V3 worked, but it was more of a tech demo: you pressed F6, watched something happen and that was it. For V4 I focused on making it fun to actually play, with traps you can use against cops, a car you can throw, and a reason not to spam the button.

## Hotkeys

- **F5**: Settings and bias menu
- **F6**: Trigger quantum shift (lasts 30s; chaining triggers Overdrive)
- **F7**: Toggle auto-trigger on wanted level
- **F8**: Reset everything (sets sunny noon, clears wanted stars, heals player, stops squads)
- **F9**: Switch HUD style (Compact, Full Telemetry with Heatmap, or Off)
- **F10**: Spawn the Quantum Chemist on the sidewalk to buy elixirs ($10, $100, $500)
- **X** or **Horn**: Launch the linked car like a missile
- **Space**: Super jump (active during any shift, along with infinite health)

## How It Works

### The 4 Reality Modes
Pressing F6 sends live game state to Moth Atlas, which collapses into one of four modes:
- **Overclock (Sunny)**: +35% sprint speed, heals health, -1 wanted star. Spawns 3 green rings on the street that repair your car, recharge armor, and give explosive ammo.
- **Time Dilation (Clouds)**: 1.5x sprint speed with repeating slow-mo pulses. Cops ignore you for 7 seconds.
- **Quantum Storm (Rain)**: Slippery road drifting. The 3 street circles become EMP traps where cars stall for 5 seconds and get tossed into the air.
- **Singularity (Snow)**: Alternating gravity pulses at the 3 street rings, pulling cars and pedestrians in before blasting them upward.

You get infinite health and super jump while any shift is active. The modes throw cars and explosions around right next to you, so without this you'd die before you saw half of it.

### Linked Twin Car
If at least 8 of the 16 coin tosses come up heads, a nearby car is tethered to you with a magenta laser line. It mirrors your moves, rams cops, and can be launched like a kinetic missile by pressing **X** on foot or your **Car Horn** while driving.

### Quantum Chemist (F10)
Spawns a street dealer on the sidewalk with three elixirs:
- **$10 Mild Dose (60s)**: Longer duration with a psychedelic optical shader overlay.
- **$100 Unstable Brew (60s)**: Guaranteed linked car with stronger physics waves.
- **$500 Singularity Overdose (45s)**: Instant overdrive, rapid shockwaves, and explosive ammo. Ends with a 4-star police hangover.

### Instability and Containment
Chaining shifts gives you Overdrive, but it also fills an Instability meter. At 100%, FIB/NOOSE containment teams show up in armored SUVs and hunt you for 45 seconds, or untill you press **F8**. Without this there was no reason not to just hold F6 forever.

### Telemetry HUD (F9)
- **Compact HUD**: Simple mode banner, timer countdown, and instability bar.
- **Full Telemetry**: Shows real-time job IDs, latency, a 4x4 car/pedestrian density heatmap, an 8-bar OTOC waveform, and the live optical shader texture.
- **Off**: Minimal slim timer.

Compact is the default. The full panel is great for checking that the calls are really live, but it covers half the screen.

## Engines Used

The mod calls the Moth Atlas API asynchronously in the background:
- `blur-core-v1` (1D): Determines weather and mode based on city chaos and player bias.
- `blur-core-v1` (2D): Blurs an 80m x 80m grid of nearby cars/peds to place the 3 street anomaly circles.
- `coin-toss-v1`: Controls collapse flash intensity and rolls for the linked car.
- `otoc-echo-v1`: Uses Out-of-Time-Order Correlator values for timed aftershocks, EMP bursts, and slow-mo pulses.
- `entanglement-shader-v1`: Streams Radiance HDR files into a live spectral texture for the HUD and screen overlay.

## Installation

For single-player Story Mode only. Never use mods in GTA Online.

> **Compatibility Note:** Tested and verified on **GTA V Legacy** (v1.0.3889.0 / v1.0.3258.0+). Not tested on GTA V Enhanced, but should work if using the corresponding Enhanced versions of ScriptHookV and ScriptHookVDotNet.

### Step 0: Turn off BattlEye
Rockstar's BattlEye anti-cheat blocks script mods from loading. You must disable it:
- **Rockstar Games Launcher**: Settings -> Grand Theft Auto V -> turn **BattlEye** to **Off**.
- **Steam / Epic Games**: Add `-nobattleye` to your game launch options, or create a text file named `commandline.txt` in your main GTA V folder containing `-nobattleye`.

### Step 1: Install Dependencies
1. **ScriptHookV**: Download from [AB Software Dev-C](http://www.dev-c.com/gtav/scripthookv/) and copy `ScriptHookV.dll` and `dinput8.dll` into your main GTA V folder (where `GTA5.exe` is).
2. **ScriptHookVDotNet**:
   - For GTA V version 3258 or 3889+ (recommended): Download **ScriptHookVDotNet Nightly (v3.7.0)** from [GitHub Nightly Releases](https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases). Place `ScriptHookVDotNet-Loader.asi` and the `.dll` files in your main GTA V folder. *(Avoid older SHVDN v3.6.0 stable as it throws a NativeMemory error on 3258+)*.
   - For older game versions (3179 or earlier): Download standard [ScriptHookVDotNet v3](https://github.com/scripthookvdotnet/scripthookvdotnet/releases).

### Step 2: Install Mod Files
Copy these files into your `GTA V\scripts\` folder:
- `QuantumRealityShiftV4.dll`
- `Newtonsoft.Json.dll`
- `LemonUI.SHVDN3.dll`

### Step 3: Moth Atlas Key or Offline Mode
- **Live Cloud Atlas**: Get a free key at `https://platform.mothquantum.com/` and set it in your Windows environment variables (`MOTH_API_KEY=moth_...`), or in `GTA V\scripts\QuantumWeather.ini` (`ApiKey=moth_...`).
- **Offline / Zero-Credit Mode**: To play completely offline or test without using any API credits, add `OfflineTestMode=true` to `GTA V\scripts\QuantumWeather.ini` (copy it from `QuantumWeather.example.ini`):
  ```ini
  [QuantumWeather]
  ApiKey=YOUR_ATLAS_API_KEY_HERE
  OfflineTestMode=true
  ```
  The mod will run local quantum simulations with zero network calls. Set it to `false` when you want live cloud processing.

## Troubleshooting

The mod writes a log to `GTA V\scripts\QuantumRealityV4.log` (the previous run is kept as `QuantumRealityV4.prev.log`). It records each Atlas request, latency, and the collapse result. API keys and download tokens are never logged. Check it first if the HUD shows `FALLBACK` instead of `LIVE ATLAS`.

## Building from Source

See "Building from source" in the `README.md` at the root of the source repository.
