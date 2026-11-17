using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Newtonsoft.Json.Linq;
using Font = GTA.UI.Font;

namespace QuantumWeatherMod
{
    /// <summary>
    /// Quantum Reality Shift (built on QuantumWeatherMod) — Main mod entry point.
    ///
    /// CONTROLS:
    ///   F6 -> Collapse reality manually via Moth Atlas
    ///   F7 -> Toggle Auto-Collapse (triggers on Wanted Level spikes)
    ///   F8 -> Stabilize / reset active quantum modifiers
    ///   F9 -> Toggle top-screen Quantum Telemetry Log HUD (shows API requests, responses, and meaning)
    /// </summary>
    public class QuantumWeatherMod : Script
    {
        // ── Controls & Constants ─────────────────────────────────────────────
        private const Keys CollapseKey = Keys.F6;
        private const Keys ToggleAutoKey = Keys.F7;
        private const Keys StabilizeKey = Keys.F8;
        private const Keys ToggleLogKey = Keys.F9;

        private const int GridSize = 4;                  // 4x4 = 16 cells (4 qubits)
        private const float GridHalfSpan = 40f;          // Covers 80m x 80m around player
        private const float PedSearchRadius = 80f;
        private const int EffectDurationMs = 30000;      // 30s active reality shift
        private const int AnomalyPulseIntervalMs = 4500; // Pulse hotspots every 4.5s
        private const int AutoCollapseCooldownMs = 20000;

        // ── Weather / Reality States (8 states = 2^3 = 3 qubits) ─────────────
        private readonly string[] _weatherTypes =
        {
            "EXTRASUNNY",   // 0
            "CLEAR",        // 1
            "CLOUDS",       // 2
            "OVERCAST",     // 3
            "FOGGY",        // 4
            "RAIN",         // 5
            "THUNDER",      // 6
            "BLIZZARD"      // 7
        };

        // ── Async / Shared State ─────────────────────────────────────────────
        private readonly AtlasService _atlas;
        private readonly ConcurrentQueue<string> _notifications = new ConcurrentQueue<string>();
        private readonly object _resultLock = new object();
        private volatile bool _isFetching = false;

        private PendingCollapse _pendingCollapse = null;

        // ── Active Gameplay State (Main GTA Thread only) ─────────────────────
        private bool _autoCollapseEnabled = true;
        private bool _showTelemetryHud = true;
        private int _lastWantedLevel = 0;
        private int _lastAutoTriggerTime = 0;

        private bool _effectActive = false;
        private int _activeWeatherIndex = 0;
        private bool _activeSurge = false;
        private int _effectEndTime = 0;
        private int _nextAnomalyPulseTime = 0;
        private int _slowMoEndTime = 0;
        private int _stealthEndTime = 0;
        private List<(float dx, float dy)> _hotspotOffsets = new List<(float dx, float dy)>();

        // ── Telemetry HUD State ──────────────────────────────────────────────
        private TelemetrySnapshot _lastTelemetry = null;
        private string _activeEffectLabel = "Idle (press F6 to collapse)";
        private string _lastFetchReason = "";

        private class ChaosSnapshot
        {
            public float Total;
            public int WantedStars;
            public int NearbyPeds;
            public int PlayerHp;
            public int NearbyVehicles;
        }

        private class TelemetrySnapshot
        {
            public string TriggerReason;
            public string SourceLabel;
            public double LatencySec;
            public ChaosSnapshot Chaos;
            public double BlurStrength;
            public double[] SentWeatherPriors;
            public double[] RecvWeatherProbs;
            public int ChosenWeatherIndex;
            public double GridDensitySum;
            public List<(float dx, float dy)> Hotspots;
            public int CoinHeads;
            public int CoinShots;
            public int FlashMs;
            public bool Surge;
            public string WeatherJobId;
            public string SpatialJobId;
            public string CoinJobId;
        }

        private class PendingCollapse
        {
            public int WeatherIndex;
            public float FlashIntensity;
            public bool Surge;
            public float ChaosLevel;
            public bool UsedFallback;
            public List<(float dx, float dy)> Hotspots;
            public TelemetrySnapshot Telemetry;
        }

        // ── Constructor ──────────────────────────────────────────────────────
        public QuantumWeatherMod()
        {
            string apiKey = LoadApiKey();
            _atlas = new AtlasService(apiKey);

            KeyDown += OnKeyDown;
            Tick += OnTick;
            Interval = 0;

            Notification.Show(
                "~p~Quantum Reality Shift ~w~loaded!\n" +
                "~y~F6~w~: Collapse | ~y~F7~w~: Auto (~g~ON~w~) | ~y~F8~w~: Reset | ~y~F9~w~: Log HUD"
            );
        }

        // ── Key Handler (Main GTA Thread) ────────────────────────────────────
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (Game.IsLoading) return;
            var ped = Game.Player.Character;
            if (ped == null || !ped.Exists()) return;

            if (e.KeyCode == CollapseKey)
            {
                TriggerCollapse("Manual (F6)");
            }
            else if (e.KeyCode == ToggleAutoKey)
            {
                _autoCollapseEnabled = !_autoCollapseEnabled;
                string state = _autoCollapseEnabled ? "~g~ON" : "~r~OFF";
                Notification.Show($"~p~Auto Quantum Collapse: {state}");
            }
            else if (e.KeyCode == StabilizeKey)
            {
                ClearActiveEffects(notify: true);
            }
            else if (e.KeyCode == ToggleLogKey)
            {
                _showTelemetryHud = !_showTelemetryHud;
                string state = _showTelemetryHud ? "~g~ON" : "~r~OFF";
                Notification.Show($"~p~Quantum Telemetry HUD: {state}");
            }
        }

        // ── Main Tick (Main GTA Thread) ──────────────────────────────────────
        private void OnTick(object sender, EventArgs e)
        {
            while (_notifications.TryDequeue(out string msg))
            {
                Notification.Show(msg);
            }

            if (Game.IsLoading) return;

            int now = Game.GameTime;
            var player = Game.Player;
            var ped = player.Character;
            if (ped == null || !ped.Exists()) return;

            // 1. Auto-trigger when Wanted Level spikes
            int currentWanted = player.WantedLevel;
            if (_autoCollapseEnabled && !_isFetching && currentWanted > _lastWantedLevel)
            {
                if (now - _lastAutoTriggerTime >= AutoCollapseCooldownMs)
                {
                    _lastAutoTriggerTime = now;
                    TriggerCollapse($"Auto (Wanted {currentWanted}*)");
                }
            }
            _lastWantedLevel = currentWanted;

            // 2. Apply newly arrived Atlas collapse result
            PendingCollapse pending = null;
            lock (_resultLock)
            {
                if (_pendingCollapse != null)
                {
                    pending = _pendingCollapse;
                    _pendingCollapse = null;
                }
            }

            if (pending != null)
            {
                _lastTelemetry = pending.Telemetry;
                ApplyQuantumReality(pending, now);
            }

            // 3. Maintain active quantum gameplay effects
            if (_effectActive)
            {
                if (!ped.IsAlive || now >= _effectEndTime)
                {
                    ClearActiveEffects(notify: ped.IsAlive);
                }
                else
                {
                    if (_slowMoEndTime > 0 && now >= _slowMoEndTime)
                    {
                        Game.TimeScale = 1.0f;
                        _slowMoEndTime = 0;
                    }

                    if (_stealthEndTime > 0 && now >= _stealthEndTime)
                    {
                        Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player, false);
                        _stealthEndTime = 0;
                    }

                    if (ped.IsInVehicle() && ped.CurrentVehicle != null && ped.CurrentVehicle.Exists())
                    {
                        Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, ped.CurrentVehicle, false);
                    }

                    if (now >= _nextAnomalyPulseTime)
                    {
                        _nextAnomalyPulseTime = now + AnomalyPulseIntervalMs;
                        PulseSpatialAnomalies(ped);
                    }
                }
            }

            // 4. Draw top-screen Telemetry HUD if toggled ON (F9)
            if (_showTelemetryHud)
            {
                DrawTelemetryHud(now);
            }
        }

        // ── Snapshot Game State & Launch Async Atlas Jobs ────────────────────
        private void TriggerCollapse(string reason)
        {
            if (_isFetching) return;
            _isFetching = true;

            try
            {
                ChaosSnapshot chaos = CaptureChaosSnapshot();
                double[][] spatialGrid = BuildSpatialEntityGrid(out double densitySum, out int vehCount);
                chaos.NearbyVehicles = vehCount;
                _lastFetchReason = reason;

                Notification.Show($"~p~Quantum collapsing ({reason})...");
                Task.Run(() => FetchQuantumStateAsync(reason, chaos, spatialGrid, densitySum));
            }
            catch
            {
                _isFetching = false;
                throw;
            }
        }

        // ── Background Thread: Calls Moth Atlas (NO GTA natives here!) ───────
        private async Task FetchQuantumStateAsync(string reason, ChaosSnapshot chaos, double[][] spatialGrid, double densitySum)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                double[] weatherWeights = BuildInitialWeatherWeights(chaos.Total);
                double blurStrength = Math.Max(0.25, Math.Min(0.95, 0.25 + chaos.Total * 0.7));

                // ① 1D blur-core-v1: Collapses weather/gameplay mode (3 qubits)
                var weatherTask = _atlas.RunEngineJobAsync("blur-core-v1", new
                {
                    values = weatherWeights,
                    strength = blurStrength,
                    reach = 0.5,
                    shots = 128,
                    style = "xy"
                });

                // ② 2D blur-core-v1: Interferes 4x4 local entity grid to pick spatial hotspots (4 qubits)
                var spatialTask = _atlas.RunEngineJobAsync("blur-core-v1", new
                {
                    values = spatialGrid,
                    strength = Math.Max(0.35, blurStrength),
                    reach = 0.7,
                    shots = 128,
                    style = "xy"
                });

                // ③ coin-toss-v1: Quantum coin flips for flash duration & Superposition Surge
                var coinTask = _atlas.RunEngineJobAsync("coin-toss-v1", new
                {
                    mode = "emu",
                    shots = 16
                });

                await Task.WhenAll(weatherTask, spatialTask, coinTask);
                sw.Stop();

                var (weatherRes, weatherJobId, weatherErr) = weatherTask.Result;
                var (spatialRes, spatialJobId, spatialErr) = spatialTask.Result;
                var (coinRes, coinJobId, coinErr) = coinTask.Result;

                if (weatherErr != null)
                    _notifications.Enqueue($"~r~Atlas weather error: {weatherErr}");
                if (spatialErr != null)
                    _notifications.Enqueue($"~r~Atlas spatial error: {spatialErr}");
                if (coinErr != null)
                    _notifications.Enqueue($"~r~Atlas coin error: {coinErr}");

                var rng = new Random();
                JArray weatherArr = UnwrapOutputArray(weatherRes);
                bool weatherOk = weatherArr != null && weatherArr.Count == _weatherTypes.Length;
                double[] recvWeatherProbs;
                int chosenIndex;

                if (weatherOk)
                {
                    recvWeatherProbs = weatherArr.Select(t => Math.Max(0.0, t.ToObject<double>())).ToArray();
                    chosenIndex = SampleFromDistribution(recvWeatherProbs, rng);
                }
                else
                {
                    double weightSum = Math.Max(1e-9, weatherWeights.Sum());
                    recvWeatherProbs = weatherWeights.Select(w => w / weightSum).ToArray();
                    chosenIndex = SampleFromDistribution(recvWeatherProbs, rng);
                }

                JArray spatialArr = UnwrapOutputArray(spatialRes);
                bool spatialOk = spatialArr != null && spatialArr.Count == GridSize;
                List<(float dx, float dy)> hotspots = ExtractHotspots(spatialArr, rng);

                bool coinOk = coinRes is JObject coinObj && coinObj["heads"] != null && coinObj["shots"] != null;
                int heads = 6;
                int shots = 16;
                float flashIntensity = 0.75f;
                bool surge = false;
                if (coinOk)
                {
                    var coinJObj = (JObject)coinRes;
                    heads = coinJObj["heads"].ToObject<int>();
                    shots = Math.Max(1, coinJObj["shots"].ToObject<int>());
                    flashIntensity = Math.Max(0.3f, Math.Min(1.0f, (float)heads / shots));
                    surge = heads >= (shots / 2);
                }

                bool allOk = weatherOk && spatialOk && coinOk;
                bool anyOk = weatherOk || spatialOk || coinOk;
                bool fallback = !allOk;
                string sourceLabel = allOk
                    ? "LIVE ATLAS"
                    : (anyOk ? "PARTIAL ATLAS" : "FALLBACK (API offline/no key)");

                var telemetry = new TelemetrySnapshot
                {
                    TriggerReason = reason,
                    SourceLabel = sourceLabel,
                    LatencySec = sw.Elapsed.TotalSeconds,
                    Chaos = chaos,
                    BlurStrength = blurStrength,
                    SentWeatherPriors = weatherWeights,
                    RecvWeatherProbs = recvWeatherProbs,
                    ChosenWeatherIndex = chosenIndex,
                    GridDensitySum = densitySum,
                    Hotspots = hotspots,
                    CoinHeads = heads,
                    CoinShots = shots,
                    FlashMs = (int)(flashIntensity * 1800),
                    Surge = surge,
                    WeatherJobId = ShortId(weatherJobId),
                    SpatialJobId = ShortId(spatialJobId),
                    CoinJobId = ShortId(coinJobId)
                };

                lock (_resultLock)
                {
                    _pendingCollapse = new PendingCollapse
                    {
                        WeatherIndex = chosenIndex,
                        FlashIntensity = flashIntensity,
                        Surge = surge,
                        ChaosLevel = chaos.Total,
                        UsedFallback = fallback,
                        Hotspots = hotspots,
                        Telemetry = telemetry
                    };
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _notifications.Enqueue($"~r~Quantum decoherence! {ex.Message}");
                var rng = new Random();
                double[] fallbackWeights = BuildInitialWeatherWeights(chaos.Total);
                int idx = rng.Next(_weatherTypes.Length);
                var hotspots = ExtractHotspots(null, rng);

                lock (_resultLock)
                {
                    _pendingCollapse = new PendingCollapse
                    {
                        WeatherIndex = idx,
                        FlashIntensity = 0.75f,
                        Surge = false,
                        ChaosLevel = chaos.Total,
                        UsedFallback = true,
                        Hotspots = hotspots,
                        Telemetry = new TelemetrySnapshot
                        {
                            TriggerReason = reason,
                            SourceLabel = "FALLBACK (Exception)",
                            LatencySec = sw.Elapsed.TotalSeconds,
                            Chaos = chaos,
                            BlurStrength = 0.5,
                            SentWeatherPriors = fallbackWeights,
                            RecvWeatherProbs = fallbackWeights,
                            ChosenWeatherIndex = idx,
                            GridDensitySum = densitySum,
                            Hotspots = hotspots,
                            CoinHeads = 8,
                            CoinShots = 16,
                            FlashMs = 1350,
                            Surge = false,
                            WeatherJobId = "none",
                            SpatialJobId = "none",
                            CoinJobId = "none"
                        }
                    };
                }
            }
            finally
            {
                _isFetching = false;
            }
        }

        // ── Top-Screen Quantum Telemetry HUD (F9 Toggle) ─────────────────────
        private void DrawTelemetryHud(int now)
        {
            var box = new ContainerElement(
                new PointF(20f, 12f),
                new SizeF(1240f, 178f),
                Color.FromArgb(190, 12, 14, 28)
            );

            string autoTag = _autoCollapseEnabled ? "ON" : "OFF";
            string remainTag = _effectActive
                ? $"{Math.Max(0, (_effectEndTime - now) / 1000)}s left"
                : "Inactive";

            var lines = new List<(string text, Color color)>();

            if (_isFetching)
            {
                lines.Add((
                    $"MOTH ATLAS TELEMETRY [F9 Hide] | STATUS: SENDING LIVE REQUEST ({_lastFetchReason})...",
                    Color.FromArgb(255, 255, 210, 90)
                ));
                lines.Add((
                    "POST https://api.mothquantum.com/api/v1/engines/blur-core-v1/process (1D Weather + 2D 4x4 Grid)",
                    Color.FromArgb(255, 180, 220, 255)
                ));
                lines.Add((
                    "POST https://api.mothquantum.com/api/v1/engines/coin-toss-v1/process (16 quantum shots, mode=emu)",
                    Color.FromArgb(255, 180, 220, 255)
                ));
                lines.Add((
                    "Polling GET /jobs/{id}/status asynchronously on background thread (60 FPS unblocked)...",
                    Color.FromArgb(255, 200, 200, 215)
                ));
            }
            else if (_lastTelemetry == null)
            {
                lines.Add((
                    $"MOTH ATLAS TELEMETRY [F9 Hide] | Auto[F7]: {autoTag} | Waiting for first quantum collapse...",
                    Color.FromArgb(255, 200, 170, 255)
                ));
                lines.Add((
                    "Press F6 (or raise Wanted Level) to send city chaos + 4x4 entity grid to Moth Atlas API.",
                    Color.FromArgb(255, 220, 225, 235)
                ));
                lines.Add((
                    "API sends: blur-core-v1 (1D weather priors + 2D entity density) & coin-toss-v1 (16 shots, emu).",
                    Color.FromArgb(255, 170, 215, 255)
                ));
                lines.Add((
                    "Controls: F6=Collapse | F7=Auto Toggle | F8=Stabilize/Reset | F9=Toggle This Log HUD",
                    Color.FromArgb(255, 160, 240, 190)
                ));
            }
            else
            {
                var t = _lastTelemetry;
                Color statusColor = t.SourceLabel.StartsWith("LIVE")
                    ? Color.FromArgb(255, 110, 255, 160)
                    : Color.FromArgb(255, 255, 185, 90);

                lines.Add((
                    $"MOTH ATLAS LOG [F9] | {t.SourceLabel} ({t.LatencySec:F2}s) | Trigger: {t.TriggerReason} | {remainTag}",
                    statusColor
                ));
                lines.Add((
                    $"INPUT: Chaos={t.Chaos.Total:F2} (Stars:{t.Chaos.WantedStars} Peds:{t.Chaos.NearbyPeds} Cars:{t.Chaos.NearbyVehicles} HP:{t.Chaos.PlayerHp}) -> Strength={t.BlurStrength:F2}",
                    Color.FromArgb(255, 235, 235, 245)
                ));
                lines.Add((
                    $"SEND 1D blur-core-v1 [{t.WeatherJobId}]: [{FormatArray(t.SentWeatherPriors)}] (priors)",
                    Color.FromArgb(255, 150, 215, 255)
                ));
                lines.Add((
                    $"RECV 1D blur-core-v1: [{FormatArray(t.RecvWeatherProbs)}] -> #{t.ChosenWeatherIndex} {_weatherTypes[t.ChosenWeatherIndex]}",
                    Color.FromArgb(255, 210, 175, 255)
                ));
                lines.Add((
                    $"2D 4x4 GRID [{t.SpatialJobId}, sum={t.GridDensitySum:F1}] -> Hotspots: {FormatHotspots(t.Hotspots)}",
                    Color.FromArgb(255, 255, 220, 140)
                ));
                lines.Add((
                    $"COIN coin-toss-v1 [{t.CoinJobId}, emu]: {t.CoinHeads}/{t.CoinShots} heads -> Flash={t.FlashMs}ms Surge={(t.Surge ? "1.35x" : "1.0x")}",
                    Color.FromArgb(255, 170, 245, 220)
                ));
                lines.Add((
                    $"GAMEPLAY: {_activeEffectLabel} | F6:Collapse F7:Auto({autoTag}) F8:Reset F9:Log",
                    Color.FromArgb(255, 255, 255, 255)
                ));
            }

            float y = 8f;
            foreach (var (text, color) in lines)
            {
                string safeText = text.Length > 95 ? text.Substring(0, 92) + "..." : text;
                box.Items.Add(new TextElement(
                    safeText,
                    new PointF(14f, y),
                    0.34f,
                    color,
                    Font.ChaletLondon,
                    Alignment.Left,
                    false,
                    true
                ));
                y += 23f;
            }

            box.Size = new SizeF(1240f, y + 8f);
            box.ScaledDraw();
        }

        // ── Apply Collapsed Reality on Main GTA Thread ───────────────────────
        private void ApplyQuantumReality(PendingCollapse collapse, int now)
        {
            int idx = Math.Max(0, Math.Min(collapse.WeatherIndex, _weatherTypes.Length - 1));
            string weather = _weatherTypes[idx];

            _effectActive = true;
            _activeWeatherIndex = idx;
            _activeSurge = collapse.Surge;
            _effectEndTime = now + EffectDurationMs;
            _nextAnomalyPulseTime = now + 800;
            _hotspotOffsets = collapse.Hotspots;
            _slowMoEndTime = 0;
            _stealthEndTime = 0;
            Game.TimeScale = 1.0f;
            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, false);

            // 1. Weather + PostFX
            Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, weather);
            int flashDuration = (int)(collapse.FlashIntensity * 1800);
            Function.Call(Hash.ANIMPOSTFX_PLAY, "RaceTurbo", flashDuration, false);

            var player = Game.Player;
            var ped = player.Character;

            // 2. State-specific immediate gameplay changes
            string effectLabel;
            if (idx <= 1)
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.35f);
                ped.Health = Math.Min(ped.MaxHealth, ped.Health + 25);
                if (player.WantedLevel > 0)
                    player.WantedLevel = Math.Max(0, player.WantedLevel - 1);
                effectLabel = "Overclock (+Sprint, +HP, -1 Star)";
            }
            else if (idx <= 4)
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.15f);
                Game.TimeScale = 0.65f;
                _slowMoEndTime = now + 3500;
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player, true);
                _stealthEndTime = now + 7000;
                effectLabel = "Time Dilation & Stealth (7s)";
            }
            else if (idx <= 6)
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.0f);
                Function.Call(Hash.FORCE_LIGHTNING_FLASH);
                Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 700, false);
                effectLabel = "Quantum Storm (EMP Hotspots + Icy Grip)";
            }
            else
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.1f);
                Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 1000, false);
                effectLabel = "Singularity (Anti-Gravity Hotspots)";
            }

            _activeEffectLabel = effectLabel;

            string sourceTag = collapse.UsedFallback ? "~o~(Fallback)" : "~g~(Atlas)";
            string surgeTag = collapse.Surge ? " ~r~[SURGE]" : "";
            Notification.Show(
                $"~p~{weather}{surgeTag} {sourceTag}\n" +
                $"~w~{effectLabel}"
            );
        }

        // ── Spatial Anomaly Pulses Driven by 2D blur-core-v1 Hotspots ────────
        private void PulseSpatialAnomalies(Ped playerPed)
        {
            Vector3 playerPos = playerPed.Position;
            float forceScale = _activeSurge ? 1.35f : 1.0f;

            if (_activeWeatherIndex <= 1)
            {
                if (playerPed.IsAlive && playerPed.Health < playerPed.MaxHealth)
                    playerPed.Health = Math.Min(playerPed.MaxHealth, playerPed.Health + 5);
                return;
            }

            if (_activeWeatherIndex <= 4)
            {
                foreach (var (dx, dy) in _hotspotOffsets.Take(1))
                {
                    Vector3 spot = new Vector3(playerPos.X + dx, playerPos.Y + dy, playerPos.Z);
                    foreach (Vehicle veh in World.GetNearbyVehicles(spot, 14f))
                    {
                        if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle) continue;
                        veh.ApplyForce(new Vector3(0f, 0f, 2.5f * forceScale), Vector3.Zero, ForceType.MaxForceRot);
                    }
                }
                return;
            }

            foreach (Vehicle veh in World.GetNearbyVehicles(playerPos, 55f))
            {
                if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle) continue;
                Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, veh, true);
            }

            if (_activeWeatherIndex == 6)
                Function.Call(Hash.FORCE_LIGHTNING_FLASH);

            foreach (var (dx, dy) in _hotspotOffsets)
            {
                float sx = playerPos.X + dx;
                float sy = playerPos.Y + dy;
                float groundZ = World.GetGroundHeight(new Vector2(sx, sy));
                float sz = groundZ > -200f && Math.Abs(groundZ) > 0.01f ? groundZ : playerPos.Z;
                Vector3 spot = new Vector3(sx, sy, sz);
                if (spot.DistanceTo(playerPos) < 8f) continue;

                ExplosionType expType = _activeWeatherIndex >= 7
                    ? ExplosionType.RayGun
                    : ExplosionType.EmpLauncherEmp;
                World.AddExplosion(spot, expType, 3.0f, 0.25f, null, true, false);

                foreach (Vehicle veh in World.GetNearbyVehicles(spot, 16f))
                {
                    if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle) continue;
                    float up = (_activeWeatherIndex >= 7 ? 11f : 6.5f) * forceScale;
                    veh.ApplyForce(new Vector3(dx * 0.08f, dy * 0.08f, up), new Vector3(0.4f, 0.2f, 0f), ForceType.MaxForceRot);
                }

                foreach (Ped p in World.GetNearbyPeds(spot, 12f))
                {
                    if (p == null || !p.Exists() || p.IsPlayer) continue;
                    p.Ragdoll(2000, RagdollType.Relax);
                }
            }
        }

        // ── Cleanly Reset Active Gameplay Modifiers ──────────────────────────
        private void ClearActiveEffects(bool notify)
        {
            _effectActive = false;
            _slowMoEndTime = 0;
            _stealthEndTime = 0;
            _activeEffectLabel = "Stabilized (idle)";
            Game.TimeScale = 1.0f;

            var player = Game.Player;
            Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.0f);
            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player, false);
            Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);

            if (player.Character != null && player.Character.Exists())
            {
                foreach (Vehicle veh in World.GetNearbyVehicles(player.Character.Position, 75f))
                {
                    if (veh != null && veh.Exists())
                        Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, veh, false);
                }
            }

            if (notify)
                Notification.Show("~b~Quantum field stabilized.");
        }

        // ── Spatial Grid & Chaos Measurement (Main Thread) ───────────────────
        private double[][] BuildSpatialEntityGrid(out double densitySum, out int vehCount)
        {
            double[][] grid = new double[GridSize][];
            for (int r = 0; r < GridSize; r++)
            {
                grid[r] = new double[GridSize];
                for (int c = 0; c < GridSize; c++)
                    grid[r][c] = 0.05;
            }

            Vector3 origin = Game.Player.Character.Position;
            float cellSize = (GridHalfSpan * 2f) / GridSize;

            Vehicle[] vehicles = World.GetNearbyVehicles(origin, GridHalfSpan);
            vehCount = vehicles.Length;
            foreach (Vehicle v in vehicles)
            {
                if (v == null || !v.Exists()) continue;
                AddEntityToGrid(grid, v.Position - origin, cellSize, 2.0);
            }

            foreach (Ped p in World.GetNearbyPeds(origin, GridHalfSpan))
            {
                if (p == null || !p.Exists() || p.IsPlayer) continue;
                AddEntityToGrid(grid, p.Position - origin, cellSize, 1.0);
            }

            densitySum = grid.Sum(row => row.Sum());
            return grid;
        }

        private void AddEntityToGrid(double[][] grid, Vector3 offset, float cellSize, double weight)
        {
            int col = (int)((offset.X + GridHalfSpan) / cellSize);
            int row = (int)((offset.Y + GridHalfSpan) / cellSize);
            if (row >= 0 && row < GridSize && col >= 0 && col < GridSize)
                grid[row][col] += weight;
        }

        private List<(float dx, float dy)> ExtractHotspots(JArray blurredGrid, Random rng)
        {
            var cells = new List<(int r, int c, double score)>();
            float cellSize = (GridHalfSpan * 2f) / GridSize;

            if (blurredGrid != null && blurredGrid.Count == GridSize)
            {
                for (int r = 0; r < GridSize; r++)
                {
                    if (blurredGrid[r] is JArray rowArr && rowArr.Count == GridSize)
                    {
                        for (int c = 0; c < GridSize; c++)
                        {
                            double val = Math.Max(0.0, rowArr[c].ToObject<double>());
                            cells.Add((r, c, val + rng.NextDouble() * 0.001));
                        }
                    }
                }
            }

            if (cells.Count == 0)
            {
                for (int i = 0; i < 3; i++)
                    cells.Add((rng.Next(GridSize), rng.Next(GridSize), rng.NextDouble()));
            }

            return cells
                .OrderByDescending(x => x.score)
                .Take(3)
                .Select(x =>
                {
                    float dx = -GridHalfSpan + (x.c + 0.5f) * cellSize;
                    float dy = -GridHalfSpan + (x.r + 0.5f) * cellSize;
                    return (dx, dy);
                })
                .ToList();
        }

        private ChaosSnapshot CaptureChaosSnapshot()
        {
            var player = Game.Player;
            var character = player.Character;

            int stars = player.WantedLevel;
            int nearbyPeds = World.GetNearbyPeds(character, PedSearchRadius).Length;
            int hp = character.Health;

            float wantedContrib = (stars / 5f) * 0.5f;
            float pedContrib = Math.Min(nearbyPeds / 20f, 1f) * 0.3f;
            float healthContrib = hp < 50 ? 0.2f : 0f;

            return new ChaosSnapshot
            {
                Total = Math.Min(wantedContrib + pedContrib + healthContrib, 1.0f),
                WantedStars = stars,
                NearbyPeds = nearbyPeds,
                PlayerHp = hp
            };
        }

        private double[] BuildInitialWeatherWeights(float chaosLevel)
        {
            double center = chaosLevel * (_weatherTypes.Length - 1);
            double[] weights = new double[_weatherTypes.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                double dist = Math.Abs(i - center);
                weights[i] = Math.Exp(-(dist * dist) / 2.5);
            }
            return weights;
        }

        private int SampleFromDistribution(double[] weights, Random rng)
        {
            double sum = weights.Sum();
            if (sum <= 0) return rng.Next(weights.Length);

            double roll = rng.NextDouble() * sum;
            double cumulative = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                cumulative += weights[i];
                if (roll <= cumulative) return i;
            }
            return weights.Length - 1;
        }

        private string FormatArray(double[] values)
        {
            if (values == null || values.Length == 0) return "";
            return string.Join(",", values.Select(v => v.ToString("F2")));
        }

        private string FormatHotspots(List<(float dx, float dy)> hotspots)
        {
            if (hotspots == null || hotspots.Count == 0) return "none";
            return string.Join(" ", hotspots.Select(h => $"({h.dx:+0;-0},{h.dy:+0;-0})m"));
        }

        private JArray UnwrapOutputArray(JToken token)
        {
            if (token is JArray arr) return arr;
            if (token is JObject obj && obj["output"] is JArray outArr) return outArr;
            return null;
        }

        private string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "none";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        private string LoadApiKey()
        {
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                string envKey = Environment.GetEnvironmentVariable("MOTH_API_KEY", target);
                if (!string.IsNullOrWhiteSpace(envKey))
                    return envKey.Trim();
            }

            try
            {
                string scriptDir = !string.IsNullOrWhiteSpace(BaseDirectory) ? BaseDirectory : null;
                if (string.IsNullOrWhiteSpace(scriptDir))
                {
                    string asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrWhiteSpace(asmLoc))
                        scriptDir = System.IO.Path.GetDirectoryName(asmLoc);
                }
                if (string.IsNullOrWhiteSpace(scriptDir))
                    scriptDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");

                string iniPath = System.IO.Path.Combine(scriptDir, "QuantumWeather.ini");
                if (System.IO.File.Exists(iniPath))
                {
                    foreach (var line in System.IO.File.ReadAllLines(iniPath))
                    {
                        if (line.StartsWith("ApiKey="))
                        {
                            string key = line.Substring("ApiKey=".Length).Trim();
                            if (!string.IsNullOrWhiteSpace(key) &&
                                !string.Equals(key, "YOUR_ATLAS_API_KEY_HERE", StringComparison.OrdinalIgnoreCase))
                            {
                                return key;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
