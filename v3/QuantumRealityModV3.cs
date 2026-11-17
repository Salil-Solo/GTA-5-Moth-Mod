using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Newtonsoft.Json.Linq;

namespace QuantumRealityShiftV3
{
    /// <summary>
    /// Quantum Reality Shift V3 — 4-Engine Moth Atlas Showcase.
    ///
    /// Engines used in parallel on every collapse:
    ///   1. blur-core-v1 (1D + 2D)      -> Gameplay state + 3D strike pillars & 4x4 heatmap
    ///   2. coin-toss-v1                -> Screen flash + [SURGE+TWIN] Entangled Twin Vehicle
    ///   3. otoc-echo-v1                -> 8 timed OTOC quantum aftershock pulses (30s / 60s)
    ///   4. entanglement-shader-v1      -> Live R_lut/T_lut ZIP -> PNG texture in F9 HUD & F10 Elixir vision
    ///
    /// CONTROLS:
    ///   F5  -> Open Quantum Control Menu (Bias Mode, Shader Overlay toggle, Triggers)
    ///   F6  -> Collapse reality manually via Moth Atlas (30s)
    ///   F7  -> Toggle Auto-Collapse (triggers on Wanted Level spikes)
    ///   F8  -> Stabilize / reset all active quantum modifiers
    ///   F9  -> Toggle top-screen Atlas Telemetry HUD + 4x4 Heatmap + OTOC Wave + Shader LUT
    ///   F10 -> Summon the "Quantum Elixir" Chemist Vendor (60s Shader-Vision trip)
    /// </summary>
    public class QuantumRealityModV3 : Script
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
        private const int OtocPulseIntervalMs = 3600;
        private const int AutoCollapseCooldownMs = 20000;

        private readonly string[] _weatherTypes =
        {
            "EXTRASUNNY", "CLEAR", "CLOUDS", "OVERCAST",
            "FOGGY", "RAIN", "THUNDER", "BLIZZARD"
        };

        private readonly string _scriptDirectory;
        private readonly string _shaderCacheDir;
        private readonly AtlasService _atlas;
        private readonly QuantumHudRenderer _hud;
        private readonly ElixirVendor _vendor;
        private readonly QuantumControlMenu _controlMenu;
        private readonly ConcurrentQueue<string> _notifications = new ConcurrentQueue<string>();
        private readonly object _resultLock = new object();

        private volatile bool _isFetching = false;
        private PendingCollapse _pendingCollapse = null;
        private int _shaderCounter = 0;

        private bool _autoCollapseEnabled = true;
        private bool _showTelemetryHud = true;
        private bool _shaderOverlayEnabled = true;
        private int _lastWantedLevel = 0;
        private int _lastAutoTriggerTime = 0;

        private bool _effectActive = false;
        private bool _isElixirTrip = false;
        private int _activeWeatherIndex = 0;
        private bool _activeSurge = false;
        private int _effectEndTime = 0;
        private int _nextAnomalyPulseTime = 0;
        private int _slowMoEndTime = 0;
        private int _stealthEndTime = 0;
        private int _nextTwinMirrorTime = 0;

        private int _activeOtocStep = 0;
        private int _lastOtocBeatTime = -10000;
        private double _lastOtocValue = 1.0;
        private double[] _activeOtocSteps = { 1.0, 0.72, 0.45, -0.35, 0.28, -0.22, 0.15, -0.10 };

        private List<HotspotTarget> _activeHotspots = new List<HotspotTarget>();
        private Vehicle _entangledTwin = null;

        private TelemetrySnapshot _lastTelemetry = null;
        private string _activeEffectLabel = "Idle (F6 Collapse / F10 Elixir)";
        private string _lastFetchReason = "";

        public QuantumRealityModV3()
        {
            _scriptDirectory = ResolveScriptDirectory();
            _shaderCacheDir = Path.Combine(_scriptDirectory, "quantum_shader_cache");
            ShaderTextureBuilder.CleanupCacheDirectory(_shaderCacheDir);

            _atlas = new AtlasService(LoadApiKey());
            _hud = new QuantumHudRenderer(_weatherTypes);
            _vendor = new ElixirVendor((reason, durationMs) => TriggerCollapse(reason, durationMs));
            _controlMenu = new QuantumControlMenu(
                _autoCollapseEnabled,
                _showTelemetryHud,
                _shaderOverlayEnabled,
                onTriggerCollapse: () => TriggerCollapse("Menu (F5)", DefaultDurationMs),
                onSummonVendor: () => _vendor.SummonVendor(),
                onStabilize: () => ClearActiveEffects(notify: true),
                onAutoChanged: val => _autoCollapseEnabled = val,
                onHudChanged: val => _showTelemetryHud = val,
                onShaderOverlayChanged: val => _shaderOverlayEnabled = val
            );

            KeyDown += OnKeyDown;
            Tick += OnTick;
            Interval = 0;

            Notification.Show(
                "~p~Quantum Reality Shift V3 ~w~(4 Atlas Engines) loaded!\n" +
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
                _controlMenu.SyncToggles(_autoCollapseEnabled, _showTelemetryHud, _shaderOverlayEnabled);
                Notification.Show($"~p~Auto Quantum Collapse: {(_autoCollapseEnabled ? "~g~ON" : "~r~OFF")}");
            }
            else if (e.KeyCode == StabilizeKey)
            {
                ClearActiveEffects(notify: true);
            }
            else if (e.KeyCode == ToggleLogKey)
            {
                _showTelemetryHud = !_showTelemetryHud;
                _controlMenu.SyncToggles(_autoCollapseEnabled, _showTelemetryHud, _shaderOverlayEnabled);
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

            // 3. Active reality effects + 3D world visuals + Shader Vision overlay
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

                    // Draw translucent entanglement-shader-v1 screen overlay (Elixir trips & OTOC beat flashes)
                    _hud.DrawShaderVisionOverlay(
                        _lastTelemetry,
                        _effectActive,
                        _isElixirTrip,
                        _shaderOverlayEnabled,
                        now,
                        _lastOtocBeatTime,
                        _lastOtocValue
                    );

                    // Draw 3D glowing strike pillars + ground rings every frame
                    _hud.DrawHotspotMarkers(
                        ped.Position,
                        _activeHotspots,
                        _activeWeatherIndex,
                        now,
                        _nextAnomalyPulseTime,
                        _lastOtocValue
                    );

                    // Draw floating cyan tags over affected cars + magenta Entangled Twin tether
                    _hud.DrawWorldEntityTagsAndTether(ped, _entangledTwin, _activeWeatherIndex, _lastTelemetry);

                    // Mirror player velocity/steering onto Entangled Twin during SURGE
                    if (_activeSurge && now >= _nextTwinMirrorTime)
                    {
                        _nextTwinMirrorTime = now + 150;
                        MirrorEntangledTwin(ped);
                    }

                    // Periodic OTOC-modulated quantum aftershock waves at the 3 hotspots
                    if (now >= _nextAnomalyPulseTime)
                    {
                        _nextAnomalyPulseTime = now + OtocPulseIntervalMs;
                        PulseOtocAftershock(ped, now);
                    }
                }
            }

            // 4. Top-screen Telemetry HUD + 4x4 Heatmap + OTOC Wave + Shader LUT
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
                int shaderIdx = ++_shaderCounter;
                string shaderPngPath = Path.Combine(_shaderCacheDir, $"qshader_{shaderIdx}.png");
                bool isElixir = reason.IndexOf("Elixir", StringComparison.OrdinalIgnoreCase) >= 0 || durationMs >= 60000;

                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "FocusIn", "HintCamSounds", true);
                Notification.Show($"~p~Collapsing 4 Atlas engines ({reason})...");
                Task.Run(() => FetchQuantumStateAsync(reason, durationMs, isElixir, biasMode, chaos, spatialGrid, densitySum, shaderPngPath));
            }
            catch
            {
                _isFetching = false;
                throw;
            }
        }

        private async Task FetchQuantumStateAsync(
            string reason,
            int durationMs,
            bool isElixirTrip,
            QuantumBiasMode biasMode,
            ChaosSnapshot chaos,
            double[][] spatialGrid,
            double densitySum,
            string shaderPngPath)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                double[] weatherWeights = BuildInitialWeatherWeights(chaos.Total, biasMode);
                double blurStrength = Math.Max(0.25, Math.Min(0.95, 0.25 + chaos.Total * 0.7));

                string shaderStyle = PickShaderStyle(biasMode, chaos.Total);
                double shaderAbsorb = Math.Round(0.40 + 0.55 * chaos.Total, 2);
                double shaderReflect = Math.Round(0.15 + 0.65 * Math.Min(1.0, chaos.NearbyVehicles / 12.0), 2);
                double shaderInteract = Math.Round((chaos.WantedStars >= 3 ? -1.0 : 1.0) * (0.45 + chaos.Total * 0.85), 2);

                double otocThetaX = Math.Round(0.35 + chaos.Total * 1.15, 4);
                double otocThetaZz = Math.Round(0.75 + Math.Min(5, chaos.WantedStars) * 0.22, 4);
                double otocDisorder = Math.Round(Math.Min(0.65, chaos.Total * 0.45), 3);
                string otocKick = chaos.Total >= 0.5f ? "X" : "Z";

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

                var otocTask = _atlas.RunEngineJobAsync("otoc-echo-v1", new
                {
                    depth = 8,
                    n_sites = 4,
                    lattice = "chain",
                    exact = true,
                    include_taps = false,
                    kick = otocKick,
                    disorder = otocDisorder,
                    theta_x = otocThetaX,
                    theta_zz = otocThetaZz
                });

                var shaderTask = _atlas.RunEngineFileJobAsync("entanglement-shader-v1", new
                {
                    absorption = shaderAbsorb,
                    incoming_rays = 8,
                    interaction = shaderInteract,
                    layers = 2,
                    reflectance = shaderReflect,
                    resolution = 32,
                    style = shaderStyle
                });

                await Task.WhenAll(weatherTask, spatialTask, coinTask, otocTask, shaderTask);
                sw.Stop();

                var (weatherRes, weatherJobId, weatherErr) = weatherTask.Result;
                var (spatialRes, spatialJobId, spatialErr) = spatialTask.Result;
                var (coinRes, coinJobId, coinErr) = coinTask.Result;
                var (otocRes, otocJobId, otocErr) = otocTask.Result;
                var (shaderZip, shaderJobId, shaderErr) = shaderTask.Result;

                if (weatherErr != null) _notifications.Enqueue($"~r~Atlas weather error: {weatherErr}");
                if (spatialErr != null) _notifications.Enqueue($"~r~Atlas spatial error: {spatialErr}");
                if (coinErr != null) _notifications.Enqueue($"~r~Atlas coin error: {coinErr}");
                if (otocErr != null) _notifications.Enqueue($"~r~Atlas OTOC error: {otocErr}");
                if (shaderErr != null) _notifications.Enqueue($"~r~Atlas shader error: {shaderErr}");

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

                int heads = 6;
                int shots = 16;
                float flashIntensity = 0.75f;
                bool surge = false;
                bool coinOk = false;
                if (coinRes is JObject coinObj && coinObj["heads"] != null && coinObj["shots"] != null)
                {
                    coinOk = true;
                    heads = coinObj["heads"].ToObject<int>();
                    shots = Math.Max(1, coinObj["shots"].ToObject<int>());
                    flashIntensity = Math.Max(0.3f, Math.Min(1.0f, (float)heads / shots));
                    surge = heads >= (shots / 2);
                }

                double[] otocSteps = ParseOtocEchoSteps(otocRes, out bool otocOk);

                double meanR = 0.0, meanT = 0.0;
                bool shaderOk = shaderZip != null &&
                                ShaderTextureBuilder.TryBuildTextureFromZip(shaderZip, shaderPngPath, out meanR, out meanT);
                string finalShaderPath = shaderOk ? shaderPngPath : null;

                bool allOk = weatherOk && spatialOk && coinOk && otocOk && shaderOk;
                bool anyOk = weatherOk || spatialOk || coinOk || otocOk || shaderOk;
                bool fallback = !allOk;
                string sourceLabel = allOk
                    ? "LIVE ATLAS (4 ENGINES)"
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
                    OtocEchoSteps = otocSteps,
                    ActiveOtocStep = 0,
                    ShaderStyle = shaderStyle,
                    ShaderMeanR = meanR,
                    ShaderMeanT = meanT,
                    ShaderPngPath = finalShaderPath,
                    WeatherJobId = ShortId(weatherJobId),
                    SpatialJobId = ShortId(spatialJobId),
                    CoinJobId = ShortId(coinJobId),
                    OtocJobId = ShortId(otocJobId),
                    ShaderJobId = ShortId(shaderJobId)
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
                        IsElixirTrip = isElixirTrip,
                        Hotspots = hotspots,
                        OtocEchoSteps = otocSteps,
                        ShaderPngPath = finalShaderPath,
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
                double[] fallbackOtoc = { 1.0, 0.72, 0.45, -0.35, 0.28, -0.22, 0.15, -0.10 };

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
                        IsElixirTrip = isElixirTrip,
                        Hotspots = hotspots,
                        OtocEchoSteps = fallbackOtoc,
                        ShaderPngPath = null,
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
                            OtocEchoSteps = fallbackOtoc,
                            ActiveOtocStep = 0,
                            ShaderStyle = "fallback",
                            ShaderMeanR = 0.0,
                            ShaderMeanT = 0.0,
                            ShaderPngPath = null,
                            WeatherJobId = "none",
                            SpatialJobId = "none",
                            CoinJobId = "none",
                            OtocJobId = "none",
                            ShaderJobId = "none"
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
            _isElixirTrip = collapse.IsElixirTrip;
            _activeWeatherIndex = idx;
            _activeSurge = collapse.Surge;
            _effectEndTime = now + collapse.DurationMs;
            _nextAnomalyPulseTime = now + 700;
            _activeHotspots = collapse.Hotspots;
            _activeOtocSteps = collapse.OtocEchoSteps ?? new[] { 1.0, 0.72, 0.45, -0.35, 0.28, -0.22, 0.15, -0.10 };
            _activeOtocStep = -1; // first PulseOtocAftershock advances to 0
            _lastOtocValue = _activeOtocSteps[0];
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
                effectLabel = "Overclock + OTOC Resonance";
            }
            else if (idx <= 4)
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.15f);
                Game.TimeScale = 0.65f;
                _slowMoEndTime = now + 3500;
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player, true);
                _stealthEndTime = now + 7000;
                effectLabel = "Time Dilation & Stealth + OTOC";
            }
            else if (idx <= 6)
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.0f);
                Function.Call(Hash.FORCE_LIGHTNING_FLASH);
                Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 700, false);
                effectLabel = "Quantum Storm (OTOC EMP Pillars)";
            }
            else
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.1f);
                Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 1000, false);
                effectLabel = "Singularity (OTOC Anti-Gravity)";
            }

            _activeEffectLabel = effectLabel;

            string sourceTag = collapse.UsedFallback ? "~o~(Fallback)" : "~g~(4-Engine Atlas)";
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

        private void PulseOtocAftershock(Ped playerPed, int now)
        {
            _activeOtocStep = (_activeOtocStep + 1) % _activeOtocSteps.Length;
            double echoVal = _activeOtocSteps[_activeOtocStep];
            _lastOtocValue = echoVal;
            _lastOtocBeatTime = now;

            if (_lastTelemetry != null)
                _lastTelemetry.ActiveOtocStep = _activeOtocStep;

            bool scrambled = echoVal < 0;
            float amp = (float)Math.Max(0.25, Math.Min(1.2, Math.Abs(echoVal)));
            float surgeScale = _activeSurge ? 1.35f : 1.0f;
            float forceScale = (0.70f + 0.65f * amp) * surgeScale * (scrambled ? 1.2f : 1.0f);

            Vector3 playerPos = playerPed.Position;

            if (_activeWeatherIndex <= 1)
            {
                int heal = 3 + (int)(7 * amp);
                if (playerPed.IsAlive && playerPed.Health < playerPed.MaxHealth)
                    playerPed.Health = Math.Min(playerPed.MaxHealth, playerPed.Health + heal);

                foreach (var h in _activeHotspots)
                {
                    Vector3 spot = SnapToGround(playerPos.X + h.OffsetX, playerPos.Y + h.OffsetY, playerPos.Z);
                    if (spot.DistanceTo(playerPos) <= 12f)
                    {
                        if (playerPed.Armor < 100)
                            playerPed.Armor = Math.Min(100, playerPed.Armor + 8 + (int)(8 * amp));
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
                    foreach (Vehicle veh in World.GetNearbyVehicles(spot, 15f))
                    {
                        if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle) continue;
                        veh.ApplyForce(new Vector3(0f, 0f, (scrambled ? 4.2f : 2.2f) * forceScale), Vector3.Zero, ForceType.MaxForceRot);
                    }
                }
                return;
            }

            foreach (Vehicle veh in World.GetNearbyVehicles(playerPos, 55f))
            {
                if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle) continue;
                Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, veh, true);
            }

            if (_activeWeatherIndex == 6 || (scrambled && amp > 0.45f))
                Function.Call(Hash.FORCE_LIGHTNING_FLASH);

            GameplayCamera.Shake(CameraShake.SmallExplosion, Math.Min(0.75f, 0.20f * forceScale));

            foreach (var h in _activeHotspots)
            {
                Vector3 spot = SnapToGround(playerPos.X + h.OffsetX, playerPos.Y + h.OffsetY, playerPos.Z);
                if (spot.DistanceTo(playerPos) < 8f) continue;

                ExplosionType expType = (_activeWeatherIndex >= 7 || scrambled)
                    ? ExplosionType.RayGun
                    : ExplosionType.EmpLauncherEmp;
                World.AddExplosion(spot, expType, 2.5f + 1.5f * amp, 0.25f * amp, null, true, false);

                foreach (Vehicle veh in World.GetNearbyVehicles(spot, 16f))
                {
                    if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle) continue;
                    float up = (_activeWeatherIndex >= 7 ? 10f : 6f) * forceScale;
                    float radial = scrambled ? 0.11f : -0.05f;
                    veh.ApplyForce(new Vector3(h.OffsetX * radial, h.OffsetY * radial, up), new Vector3(0.4f, 0.2f, 0f), ForceType.MaxForceRot);
                }

                foreach (Ped p in World.GetNearbyPeds(spot, 14f))
                {
                    if (p == null || !p.Exists() || p.IsPlayer) continue;
                    p.Ragdoll(scrambled ? 2600 : 1800, RagdollType.Relax);
                }
            }

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
            _isElixirTrip = false;
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

        private double[] ParseOtocEchoSteps(JToken otocRes, out bool ok)
        {
            double[] fallback = { 1.0, 0.72, 0.45, -0.35, 0.28, -0.22, 0.15, -0.10 };
            ok = false;

            try
            {
                var fRe = otocRes?["output"]?["data"]?["series"]?["F_re"] as JArray;
                if (fRe == null || fRe.Count == 0)
                    return fallback;

                int kickSite = otocRes["output"]?["extras"]?["kick_site"]?.ToObject<int?>() ?? (fRe.Count / 2);
                kickSite = Math.Max(0, Math.Min(fRe.Count - 1, kickSite));

                if (!(fRe[kickSite] is JArray row) || row.Count == 0)
                    return fallback;

                var steps = new double[8];
                for (int i = 0; i < 8; i++)
                {
                    steps[i] = i < row.Count ? Math.Max(-1.0, Math.Min(1.0, row[i].ToObject<double>())) : 0.0;
                }
                ok = true;
                return steps;
            }
            catch
            {
                return fallback;
            }
        }

        private string PickShaderStyle(QuantumBiasMode biasMode, float chaos)
        {
            switch (biasMode)
            {
                case QuantumBiasMode.FavorOverclock:   return "peaked";
                case QuantumBiasMode.FavorStealth:     return "constrained";
                case QuantumBiasMode.FavorStorm:       return "frustrated";
                case QuantumBiasMode.FavorSingularity: return "3-body";
                default:
                    if (chaos >= 0.7f) return "3-body";
                    if (chaos >= 0.4f) return "frustrated";
                    return "peaked";
            }
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
                case QuantumBiasMode.FavorOverclock:   center = 0.5; break;
                case QuantumBiasMode.FavorStealth:     center = 3.0; break;
                case QuantumBiasMode.FavorStorm:       center = 5.6; break;
                case QuantumBiasMode.FavorSingularity: center = 7.0; break;
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

        private string ResolveScriptDirectory()
        {
            if (!string.IsNullOrWhiteSpace(BaseDirectory))
                return BaseDirectory;

            string asmLoc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrWhiteSpace(asmLoc))
                return Path.GetDirectoryName(asmLoc);

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
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
                string iniPath = Path.Combine(_scriptDirectory, "QuantumWeather.ini");
                if (File.Exists(iniPath))
                {
                    foreach (var line in File.ReadAllLines(iniPath))
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
