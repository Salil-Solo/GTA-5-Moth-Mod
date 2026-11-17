using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Newtonsoft.Json.Linq;

namespace QuantumRealityShiftV2
{
    /// <summary>
    /// Quantum Reality Shift V2 — Phase 2 Edition.
    ///
    /// CONTROLS:
    ///   F5  -> Open Quantum Control Menu (Bias Mode, Triggers, Toggles)
    ///   F6  -> Collapse reality manually via Moth Atlas (30s)
    ///   F7  -> Toggle Auto-Collapse (triggers on Wanted Level spikes)
    ///   F8  -> Stabilize / reset all active quantum modifiers
    ///   F9  -> Toggle top-screen Atlas Telemetry HUD + Live 4x4 Heatmap
    ///   F10 -> Summon the "Quantum Elixir" Chemist Vendor (60s trip)
    /// </summary>
    public class QuantumRealityModV2 : Script
    {
        private const Keys MenuKey = Keys.F5;
        private const Keys CollapseKey = Keys.F6;
        private const Keys ToggleAutoKey = Keys.F7;
        private const Keys StabilizeKey = Keys.F8;
        private const Keys ToggleLogKey = Keys.F9;
        private const Keys VendorKey = Keys.F10;

        private const int GridSize = 4;
        private const float GridHalfSpan = 40f;
        private const float PedSearchRadius = 80f;
        private const int DefaultDurationMs = 30000;
        private const int AnomalyPulseIntervalMs = 4500;
        private const int AutoCollapseCooldownMs = 20000;

        private readonly string[] _weatherTypes =
        {
            "EXTRASUNNY", "CLEAR", "CLOUDS", "OVERCAST",
            "FOGGY", "RAIN", "THUNDER", "BLIZZARD"
        };

        private readonly AtlasService _atlas;
        private readonly QuantumHudRenderer _hud;
        private readonly ElixirVendor _vendor;
        private readonly QuantumControlMenu _controlMenu;
        private readonly ConcurrentQueue<string> _notifications = new ConcurrentQueue<string>();
        private readonly object _resultLock = new object();

        private volatile bool _isFetching = false;
        private PendingCollapse _pendingCollapse = null;

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
        private int _nextTwinMirrorTime = 0;

        private List<HotspotTarget> _activeHotspots = new List<HotspotTarget>();
        private Vehicle _entangledTwin = null;

        private TelemetrySnapshot _lastTelemetry = null;
        private string _activeEffectLabel = "Idle (F6 Collapse / F10 Elixir)";
        private string _lastFetchReason = "";

        public QuantumRealityModV2()
        {
            _atlas = new AtlasService(LoadApiKey());
            _hud = new QuantumHudRenderer(_weatherTypes);
            _vendor = new ElixirVendor((reason, durationMs) => TriggerCollapse(reason, durationMs));
            _controlMenu = new QuantumControlMenu(
                _autoCollapseEnabled,
                _showTelemetryHud,
                onTriggerCollapse: () => TriggerCollapse("Menu (F5)", DefaultDurationMs),
                onSummonVendor: () => _vendor.SummonVendor(),
                onStabilize: () => ClearActiveEffects(notify: true),
                onAutoChanged: val => _autoCollapseEnabled = val,
                onHudChanged: val => _showTelemetryHud = val
            );

            KeyDown += OnKeyDown;
            Tick += OnTick;
            Interval = 0;

            Notification.Show(
                "~p~Quantum Reality Shift V2 ~w~loaded!\n" +
                "~y~F5~w~:Menu ~y~F6~w~:Collapse ~y~F8~w~:Reset ~y~F9~w~:HUD ~y~F10~w~:Elixir"
            );
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (Game.IsLoading) return;
            var ped = Game.Player.Character;
            if (ped == null || !ped.Exists()) return;

            if (e.KeyCode == MenuKey)
            {
                _controlMenu.Toggle();
            }
            else if (e.KeyCode == CollapseKey)
            {
                TriggerCollapse("Manual (F6)", DefaultDurationMs);
            }
            else if (e.KeyCode == ToggleAutoKey)
            {
                _autoCollapseEnabled = !_autoCollapseEnabled;
                _controlMenu.SyncToggles(_autoCollapseEnabled, _showTelemetryHud);
                Notification.Show($"~p~Auto Quantum Collapse: {(_autoCollapseEnabled ? "~g~ON" : "~r~OFF")}");
            }
            else if (e.KeyCode == StabilizeKey)
            {
                ClearActiveEffects(notify: true);
            }
            else if (e.KeyCode == ToggleLogKey)
            {
                _showTelemetryHud = !_showTelemetryHud;
                _controlMenu.SyncToggles(_autoCollapseEnabled, _showTelemetryHud);
                Notification.Show($"~p~Quantum Telemetry HUD: {(_showTelemetryHud ? "~g~ON" : "~r~OFF")}");
            }
            else if (e.KeyCode == VendorKey)
            {
                _controlMenu.Close();
                _vendor.SummonVendor();
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            while (_notifications.TryDequeue(out string msg))
                Notification.Show(msg);

            if (Game.IsLoading) return;

            int now = Game.GameTime;
            var player = Game.Player;
            var ped = player.Character;
            if (ped == null || !ped.Exists()) return;

            _controlMenu.Update();
            _vendor.Update(now);

            // 1. Auto-trigger on Wanted Level spikes
            int currentWanted = player.WantedLevel;
            if (_autoCollapseEnabled && !_isFetching && currentWanted > _lastWantedLevel)
            {
                if (now - _lastAutoTriggerTime >= AutoCollapseCooldownMs)
                {
                    _lastAutoTriggerTime = now;
                    TriggerCollapse($"Auto (Wanted {currentWanted}*)", DefaultDurationMs);
                }
            }
            _lastWantedLevel = currentWanted;

            // 2. Apply completed Atlas collapse
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

            // 3. Active reality effects + Phase 2 3D world visuals
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

                    // Keep the player's own vehicle free from residual icy traction if stealing an affected NPC car
                    if (ped.IsInVehicle() && ped.CurrentVehicle != null && ped.CurrentVehicle.Exists())
                    {
                        Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, ped.CurrentVehicle, false);
                    }

                    // Draw 3D glowing strike pillars + ground rings every frame
                    _hud.DrawHotspotMarkers(ped.Position, _activeHotspots, _activeWeatherIndex, now, _nextAnomalyPulseTime);

                    // Draw floating cyan tags over affected cars + magenta Entangled Twin tether
                    _hud.DrawWorldEntityTagsAndTether(ped, _entangledTwin, _activeWeatherIndex, _lastTelemetry);

                    // Mirror player velocity/steering onto Entangled Twin during SURGE
                    if (_activeSurge && now >= _nextTwinMirrorTime)
                    {
                        _nextTwinMirrorTime = now + 150;
                        MirrorEntangledTwin(ped);
                    }

                    // Periodic EMP / anti-gravity pulses at the 3 hotspots
                    if (now >= _nextAnomalyPulseTime)
                    {
                        _nextAnomalyPulseTime = now + AnomalyPulseIntervalMs;
                        PulseSpatialAnomalies(ped);
                    }
                }
            }

            // 4. Top-screen Telemetry HUD + 4x4 Heatmap
            if (_showTelemetryHud)
            {
                _hud.DrawTelemetryAndHeatmap(
                    now, _isFetching, _lastFetchReason, _autoCollapseEnabled,
                    _effectActive, _effectEndTime, _activeEffectLabel, _lastTelemetry
                );
            }
        }

        private void TriggerCollapse(string reason, int durationMs)
        {
            if (_isFetching) return;
            _isFetching = true;

            try
            {
                ChaosSnapshot chaos = CaptureChaosSnapshot();
                double[][] spatialGrid = BuildSpatialEntityGrid(out double densitySum, out int vehCount);
                chaos.NearbyVehicles = vehCount;
                _lastFetchReason = reason;
                QuantumBiasMode biasMode = _controlMenu.BiasMode;

                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "FocusIn", "HintCamSounds", true);
                Notification.Show($"~p~Quantum collapsing ({reason})...");
                Task.Run(() => FetchQuantumStateAsync(reason, durationMs, biasMode, chaos, spatialGrid, densitySum));
            }
            catch
            {
                _isFetching = false;
                throw;
            }
        }

        private async Task FetchQuantumStateAsync(
            string reason, int durationMs, QuantumBiasMode biasMode, ChaosSnapshot chaos, double[][] spatialGrid, double densitySum)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                double[] weatherWeights = BuildInitialWeatherWeights(chaos.Total, biasMode);
                double blurStrength = Math.Max(0.25, Math.Min(0.95, 0.25 + chaos.Total * 0.7));

                var weatherTask = _atlas.RunEngineJobAsync("blur-core-v1", new
                {
                    values = weatherWeights,
                    strength = blurStrength,
                    reach = 0.5,
                    shots = 128,
                    style = "xy"
                });

                var spatialTask = _atlas.RunEngineJobAsync("blur-core-v1", new
                {
                    values = spatialGrid,
                    strength = Math.Max(0.35, blurStrength),
                    reach = 0.7,
                    shots = 128,
                    style = "xy"
                });

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

                if (weatherErr != null) _notifications.Enqueue($"~r~Atlas weather error: {weatherErr}");
                if (spatialErr != null) _notifications.Enqueue($"~r~Atlas spatial error: {spatialErr}");
                if (coinErr != null) _notifications.Enqueue($"~r~Atlas coin error: {coinErr}");

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

                double[][] parsedGrid = ParseGrid(UnwrapOutputArray(spatialRes));
                bool spatialOk = parsedGrid != null;
                double[][] blurredGrid = parsedGrid ?? spatialGrid;
                List<HotspotTarget> hotspots = ExtractHotspots(blurredGrid, rng);

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
                    BiasLabel = FormatBiasLabel(biasMode),
                    SourceLabel = sourceLabel,
                    LatencySec = sw.Elapsed.TotalSeconds,
                    Chaos = chaos,
                    BlurStrength = blurStrength,
                    SentWeatherPriors = weatherWeights,
                    RecvWeatherProbs = recvWeatherProbs,
                    ChosenWeatherIndex = chosenIndex,
                    InputGrid = spatialGrid,
                    BlurredGrid = blurredGrid,
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
                        DurationMs = durationMs,
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
                double[] fallbackWeights = BuildInitialWeatherWeights(chaos.Total, biasMode);
                int idx = rng.Next(_weatherTypes.Length);
                var hotspots = ExtractHotspots(spatialGrid, rng);

                lock (_resultLock)
                {
                    _pendingCollapse = new PendingCollapse
                    {
                        WeatherIndex = idx,
                        FlashIntensity = 0.75f,
                        Surge = false,
                        ChaosLevel = chaos.Total,
                        UsedFallback = true,
                        DurationMs = durationMs,
                        Hotspots = hotspots,
                        Telemetry = new TelemetrySnapshot
                        {
                            TriggerReason = reason,
                            BiasLabel = FormatBiasLabel(biasMode),
                            SourceLabel = "FALLBACK (Exception)",
                            LatencySec = sw.Elapsed.TotalSeconds,
                            Chaos = chaos,
                            BlurStrength = 0.5,
                            SentWeatherPriors = fallbackWeights,
                            RecvWeatherProbs = fallbackWeights,
                            ChosenWeatherIndex = idx,
                            InputGrid = spatialGrid,
                            BlurredGrid = spatialGrid,
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

        private void ApplyQuantumReality(PendingCollapse collapse, int now)
        {
            int idx = Math.Max(0, Math.Min(collapse.WeatherIndex, _weatherTypes.Length - 1));
            string weather = _weatherTypes[idx];

            _effectActive = true;
            _activeWeatherIndex = idx;
            _activeSurge = collapse.Surge;
            _effectEndTime = now + collapse.DurationMs;
            _nextAnomalyPulseTime = now + 800;
            _activeHotspots = collapse.Hotspots;
            _entangledTwin = null;
            _slowMoEndTime = 0;
            _stealthEndTime = 0;
            Game.TimeScale = 1.0f;
            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, false);

            Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, weather);
            int flashDuration = (int)(collapse.FlashIntensity * 1800);
            Function.Call(Hash.ANIMPOSTFX_PLAY, "RaceTurbo", flashDuration, false);
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "FocusOut", "HintCamSounds", true);
            GameplayCamera.Shake(CameraShake.Jolt, collapse.Surge ? 0.85f : 0.45f);

            var player = Game.Player;
            var ped = player.Character;

            // Pick nearest vehicle as Entangled Twin when SURGE is active
            if (_activeSurge)
            {
                _entangledTwin = World.GetNearbyVehicles(ped.Position, 55f)
                    .Where(v => v != null && v.Exists() && v.IsAlive && v != ped.CurrentVehicle)
                    .OrderBy(v => v.Position.DistanceTo(ped.Position))
                    .FirstOrDefault();
            }

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
                effectLabel = "Quantum Storm (EMP Pillars + Icy Grip)";
            }
            else
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.1f);
                Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 1000, false);
                effectLabel = "Singularity (Anti-Gravity Pillars)";
            }

            _activeEffectLabel = effectLabel;

            string sourceTag = collapse.UsedFallback ? "~o~(Fallback)" : "~g~(Atlas)";
            string surgeTag = collapse.Surge ? " ~r~[SURGE+TWIN]" : "";
            Notification.Show($"~p~{weather}{surgeTag} {sourceTag}\n~w~{effectLabel}");
        }

        private void MirrorEntangledTwin(Ped playerPed)
        {
            if (_entangledTwin == null || !_entangledTwin.Exists() || !_entangledTwin.IsAlive)
            {
                _entangledTwin = null;
                return;
            }

            if (_entangledTwin.Position.DistanceTo(playerPed.Position) > 95f)
                return;

            // Mirror player's driving or sprinting kinetic state onto the entangled car
            if (playerPed.IsInVehicle() && playerPed.CurrentVehicle != null)
            {
                Vehicle myCar = playerPed.CurrentVehicle;
                if (myCar.Speed > 3f)
                {
                    Vector3 push = myCar.Velocity * 0.14f;
                    _entangledTwin.ApplyForce(new Vector3(push.X, push.Y, 0.35f), Vector3.Zero, ForceType.MaxForceRot);
                }
            }
            else if (playerPed.IsJumping || playerPed.IsSprinting)
            {
                _entangledTwin.ApplyForce(new Vector3(0f, 0f, 1.8f), new Vector3(0.2f, 0f, 0f), ForceType.MaxForceRot);
            }
        }

        private void PulseSpatialAnomalies(Ped playerPed)
        {
            Vector3 playerPos = playerPed.Position;
            float forceScale = _activeSurge ? 1.35f : 1.0f;

            if (_activeWeatherIndex <= 1)
            {
                if (playerPed.IsAlive && playerPed.Health < playerPed.MaxHealth)
                    playerPed.Health = Math.Min(playerPed.MaxHealth, playerPed.Health + 5);

                // Standing or driving near an Overclock green pillar grants extra armor/repair boost
                foreach (var h in _activeHotspots)
                {
                    Vector3 spot = SnapToGround(playerPos.X + h.OffsetX, playerPos.Y + h.OffsetY, playerPos.Z);
                    if (spot.DistanceTo(playerPos) <= 12f)
                    {
                        if (playerPed.Armor < 100)
                            playerPed.Armor = Math.Min(100, playerPed.Armor + 10);
                        if (playerPed.IsInVehicle() && playerPed.CurrentVehicle != null && playerPed.CurrentVehicle.Exists())
                            playerPed.CurrentVehicle.Repair();
                        break;
                    }
                }
                return;
            }

            if (_activeWeatherIndex <= 4)
            {
                foreach (var h in _activeHotspots.Take(1))
                {
                    Vector3 spot = SnapToGround(playerPos.X + h.OffsetX, playerPos.Y + h.OffsetY, playerPos.Z);
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

            GameplayCamera.Shake(CameraShake.SmallExplosion, _activeSurge ? 0.45f : 0.25f);

            foreach (var h in _activeHotspots)
            {
                Vector3 spot = SnapToGround(playerPos.X + h.OffsetX, playerPos.Y + h.OffsetY, playerPos.Z);
                if (spot.DistanceTo(playerPos) < 8f) continue;

                ExplosionType expType = _activeWeatherIndex >= 7
                    ? ExplosionType.RayGun
                    : ExplosionType.EmpLauncherEmp;
                World.AddExplosion(spot, expType, 3.0f, 0.25f, null, true, false);

                foreach (Vehicle veh in World.GetNearbyVehicles(spot, 16f))
                {
                    if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle) continue;
                    float up = (_activeWeatherIndex >= 7 ? 11f : 6.5f) * forceScale;
                    veh.ApplyForce(new Vector3(h.OffsetX * 0.08f, h.OffsetY * 0.08f, up), new Vector3(0.4f, 0.2f, 0f), ForceType.MaxForceRot);
                }

                foreach (Ped p in World.GetNearbyPeds(spot, 14f))
                {
                    if (p == null || !p.Exists() || p.IsPlayer) continue;
                    p.Ragdoll(2000, RagdollType.Relax);
                }
            }

            // Trigger panic on nearby witnesses outside direct ragdoll radius
            foreach (Ped witness in World.GetNearbyPeds(playerPos, 35f))
            {
                if (witness == null || !witness.Exists() || witness.IsPlayer || !witness.IsAlive || witness.IsRagdoll) continue;
                witness.Task.ReactAndFlee(playerPed);
            }
        }

        private Vector3 SnapToGround(float x, float y, float fallbackZ)
        {
            float groundZ = World.GetGroundHeight(new Vector3(x, y, fallbackZ + 6f));
            if (groundZ <= -200f || Math.Abs(groundZ) <= 0.01f)
                groundZ = World.GetGroundHeight(new Vector2(x, y));
            float z = groundZ > -200f && Math.Abs(groundZ) > 0.01f ? groundZ : fallbackZ;
            return new Vector3(x, y, z);
        }

        private void ClearActiveEffects(bool notify)
        {
            _effectActive = false;
            _slowMoEndTime = 0;
            _stealthEndTime = 0;
            _entangledTwin = null;
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

        private JArray UnwrapOutputArray(JToken token)
        {
            if (token is JArray arr) return arr;
            if (token is JObject obj && obj["output"] is JArray outArr) return outArr;
            return null;
        }

        private double[][] ParseGrid(JArray arr)
        {
            if (arr == null || arr.Count != GridSize) return null;
            double[][] grid = new double[GridSize][];
            for (int r = 0; r < GridSize; r++)
            {
                if (!(arr[r] is JArray rowArr) || rowArr.Count != GridSize) return null;
                grid[r] = new double[GridSize];
                for (int c = 0; c < GridSize; c++)
                    grid[r][c] = Math.Max(0.0, rowArr[c].ToObject<double>());
            }
            return grid;
        }

        private List<HotspotTarget> ExtractHotspots(double[][] blurredGrid, Random rng)
        {
            var cells = new List<HotspotTarget>();
            float cellSize = (GridHalfSpan * 2f) / GridSize;

            if (blurredGrid != null && blurredGrid.Length == GridSize)
            {
                for (int r = 0; r < GridSize; r++)
                {
                    for (int c = 0; c < GridSize; c++)
                    {
                        double val = Math.Max(0.0, blurredGrid[r][c]);
                        cells.Add(new HotspotTarget
                        {
                            Row = r,
                            Col = c,
                            OffsetX = -GridHalfSpan + (c + 0.5f) * cellSize,
                            OffsetY = -GridHalfSpan + (r + 0.5f) * cellSize,
                            Score = val + rng.NextDouble() * 0.001
                        });
                    }
                }
            }

            if (cells.Count == 0)
            {
                for (int i = 0; i < 3; i++)
                {
                    int r = rng.Next(GridSize);
                    int c = rng.Next(GridSize);
                    cells.Add(new HotspotTarget
                    {
                        Row = r,
                        Col = c,
                        OffsetX = -GridHalfSpan + (c + 0.5f) * cellSize,
                        OffsetY = -GridHalfSpan + (r + 0.5f) * cellSize,
                        Score = rng.NextDouble()
                    });
                }
            }

            return cells.OrderByDescending(x => x.Score).Take(3).ToList();
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

        private double[] BuildInitialWeatherWeights(float chaosLevel, QuantumBiasMode biasMode)
        {
            double center;
            switch (biasMode)
            {
                case QuantumBiasMode.FavorOverclock:   center = 0.5; break; // EXTRASUNNY / CLEAR
                case QuantumBiasMode.FavorStealth:     center = 3.0; break; // CLOUDS / OVERCAST / FOGGY
                case QuantumBiasMode.FavorStorm:       center = 5.6; break; // RAIN / THUNDER
                case QuantumBiasMode.FavorSingularity: center = 7.0; break; // BLIZZARD
                default: center = chaosLevel * (_weatherTypes.Length - 1); break;
            }

            double[] weights = new double[_weatherTypes.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                double dist = Math.Abs(i - center);
                weights[i] = Math.Exp(-(dist * dist) / 2.2);
            }
            return weights;
        }

        private string FormatBiasLabel(QuantumBiasMode biasMode)
        {
            switch (biasMode)
            {
                case QuantumBiasMode.FavorOverclock:   return "Bias:Overclock";
                case QuantumBiasMode.FavorStealth:     return "Bias:Stealth";
                case QuantumBiasMode.FavorStorm:       return "Bias:Storm";
                case QuantumBiasMode.FavorSingularity: return "Bias:Singularity";
                default:                               return "AutoChaos";
            }
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
