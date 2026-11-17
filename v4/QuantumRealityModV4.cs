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

namespace QuantumRealityShiftV4
{
    /// <summary>
    /// Quantum Reality Shift V4 — Gameplay-First 4-Engine Moth Atlas Edition.
    ///
    /// Keeps all 4 Moth Atlas engines running asynchronously on background threads:
    ///   1. blur-core-v1 (1D + 2D)      -> Mode collapse + world-anchored 3D strike pillars & 4x4 heatmap
    ///   2. coin-toss-v1                -> Screen flash + active [SURGE+TWIN] Entangled Twin Vehicle
    ///   3. otoc-echo-v1                -> 8 timed OTOC aftershock pulses (Singularity pull/launch, Storm EMPs, Time Dilation beats)
    ///   4. entanglement-shader-v1      -> Live R_lut/T_lut ZIP -> PNG in HUD & Elixir Vision overlay
    ///
    /// CONTROLS:
    ///   F5             -> Quantum Control Menu (Prior Bias, HUD mode, Linked Car, Chemist)
    ///   F6             -> Charge & collapse quantum reality across 4 Moth Atlas engines
    ///   F7             -> Toggle Auto-Collapse (triggers on Wanted Level spikes)
    ///   F8             -> Stabilize / reset all active modifiers & Containment Breach
    ///   F9             -> Cycle 3 HUD states: Compact Player HUD -> Full Atlas Telemetry -> Off
    ///   F10            -> Summon the 3-Tier Quantum Chemist ($10 / $100 / $500 Elixirs)
    ///   X / Car Horn   -> Launch Linked Twin Car toward aim target or nearest cop
    ///   Jump (Active)  -> Super Jump across all active modes
    /// </summary>
    public class QuantumRealityModV4 : Script
    {
        private const Keys MenuKey = Keys.F5;
        private const Keys CollapseKey = Keys.F6;
        private const Keys ToggleAutoKey = Keys.F7;
        private const Keys StabilizeKey = Keys.F8;
        private const Keys ToggleHudKey = Keys.F9;
        private const Keys VendorKey = Keys.F10;
        private const Keys LaunchTwinKey = Keys.X;

        private const int GridSize = 4;
        private const float GridHalfSpan = 40f;
        private const float PedSearchRadius = 80f;
        private const int DefaultDurationMs = 30000;
        private const int DefaultOtocIntervalMs = 3600;
        private const int AutoCollapseCooldownMs = 20000;
        private const int ContainmentDurationMs = 45000;

        private readonly string[] _weatherTypes =
        {
            "EXTRASUNNY", "CLEAR", "CLOUDS", "OVERCAST",
            "FOGGY", "RAIN", "THUNDER", "BLIZZARD"
        };

        private readonly string _scriptDirectory;
        private readonly string _shaderCacheDir;
        private readonly bool _offlineTestMode;
        private readonly AtlasService _atlas;
        private readonly QuantumHudRenderer _hud;
        private readonly ElixirVendor _vendor;
        private readonly QuantumControlMenu _controlMenu;
        private readonly ConcurrentQueue<string> _notifications = new ConcurrentQueue<string>();
        private readonly object _resultLock = new object();

        private volatile bool _isFetching = false;
        private PendingCollapse _pendingCollapse = null;
        private PendingShaderUpdate _pendingShaderUpdate = null;
        private int _shaderCounter = 0;

        private bool _autoCollapseEnabled = true;
        private HudDisplayMode _hudMode = HudDisplayMode.Compact;
        private bool _shaderOverlayEnabled = true;
        private int _lastWantedLevel = 0;
        private int _lastAutoTriggerTime = 0;
        private int _nextChargeCueTime = 0;

        private bool _effectActive = false;
        private ElixirTier _activeElixirTier = ElixirTier.None;
        private bool _overdriveActive = false;
        private int _activeWeatherIndex = 0;
        private bool _activeSurge = false;
        private int _effectStartTime = 0;
        private int _effectEndTime = 0;
        private int _otocPulseIntervalMs = DefaultOtocIntervalMs;
        private int _nextAnomalyPulseTime = 0;
        private int _slowMoEndTime = 0;
        private int _stealthEndTime = 0;
        private int _overclockAmmoBuffEndTime = 0;
        private int _nextSuctionTickTime = 0;

        // OTOC Beat State
        private int _activeOtocStep = 0;
        private int _lastOtocBeatTime = -10000;
        private double _lastOtocValue = 1.0;
        private double[] _activeOtocSteps = { 1.0, 0.72, 0.45, -0.35, 0.28, -0.22, 0.15, -0.10 };

        // Entangled Twin + Stalled EMP vehicles
        private List<HotspotTarget> _activeHotspots = new List<HotspotTarget>();
        private Vehicle _entangledTwin = null;
        private int _nextTwinMirrorTime = 0;
        private int _nextTwinRamTime = 0;
        private int _nextTwinLaunchAllowedTime = 0;
        private readonly Dictionary<int, (Vehicle veh, int restoreAt)> _stalledVehicles = new Dictionary<int, (Vehicle, int)>();
        private readonly Dictionary<int, Vehicle> _icyVehicles = new Dictionary<int, Vehicle>();
        private readonly HashSet<WeaponHash> _modifiedClipWeapons = new HashSet<WeaponHash>();

        // Phase 3: Reality Instability & Containment Breach
        private float _instabilityPercent = 0f;
        private int _lastDecayTime = 0;
        private bool _breachActive = false;
        private int _breachEndTime = 0;
        private RelationshipGroup _containmentGroup;
        private readonly List<Ped> _containmentPeds = new List<Ped>();
        private readonly List<Vehicle> _containmentVehicles = new List<Vehicle>();
        private readonly List<Blip> _containmentBlips = new List<Blip>();

        private TelemetrySnapshot _lastTelemetry = null;
        private string _activeModeShortName = "READY";
        private string _activeEffectLabel = "Idle (F6 Collapse / F10 Chemist)";
        private string _lastFetchReason = "";
        private string _engineBannerText = "";
        private int _engineBannerEndTime = 0;

        public QuantumRealityModV4()
        {
            _scriptDirectory = ResolveScriptDirectory();
            ModLogger.Init(_scriptDirectory);
            _shaderCacheDir = Path.Combine(_scriptDirectory, "quantum_shader_cache");
            ShaderTextureBuilder.CleanupCacheDirectory(_shaderCacheDir);

            _offlineTestMode = LoadOfflineTestMode();
            if (_offlineTestMode)
                ModLogger.Info("[MODE] OfflineTestMode=true in QuantumWeather.ini — API calls paused for local gameplay testing.");

            _atlas = new AtlasService(LoadApiKey());
            _hud = new QuantumHudRenderer(_weatherTypes);
            _vendor = new ElixirVendor((reason, durationMs, tier) => TriggerCollapse(reason, durationMs, tier));
            _controlMenu = new QuantumControlMenu(
                _autoCollapseEnabled,
                _hudMode,
                _shaderOverlayEnabled,
                onTriggerCollapse: () => TriggerCollapse("Menu (F5)", DefaultDurationMs, ElixirTier.None),
                onLaunchTwin: () => LaunchEntangledTwin(notifyIfMissing: true),
                onSummonVendor: () => _vendor.SummonVendor(),
                onStabilize: () => ClearActiveEffects(notify: true, clearBreach: true),
                onAutoChanged: val => _autoCollapseEnabled = val,
                onHudModeChanged: mode => _hudMode = mode,
                onShaderOverlayChanged: val => _shaderOverlayEnabled = val
            );

            KeyDown += OnKeyDown;
            Tick += OnTick;
            Aborted += OnScriptAborted;
            Interval = 0;

            Notification.Show(
                "~p~Quantum Reality Shift V4 ~w~loaded!\n" +
                "~y~F5~w~:Menu ~y~F6~w~:Collapse ~y~F9~w~:HUD ~y~F10~w~:Chemist ~y~X~w~:Twin"
            );
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (Game.IsLoading) return;
            var ped = Game.Player.Character;
            if (ped == null || !ped.Exists()) return;

            if (e.KeyCode == MenuKey)
            {
                _vendor.CloseMenu();
                _controlMenu.Toggle();
            }
            else if (e.KeyCode == CollapseKey)
            {
                TriggerCollapse("Manual (F6)", DefaultDurationMs, ElixirTier.None);
            }
            else if (e.KeyCode == ToggleAutoKey)
            {
                _autoCollapseEnabled = !_autoCollapseEnabled;
                _controlMenu.SyncToggles(_autoCollapseEnabled, _hudMode, _shaderOverlayEnabled);
                Notification.Show($"~p~Auto Quantum Collapse: {(_autoCollapseEnabled ? "~g~ON" : "~r~OFF")}");
            }
            else if (e.KeyCode == StabilizeKey)
            {
                ClearActiveEffects(notify: true, clearBreach: true);
            }
            else if (e.KeyCode == ToggleHudKey)
            {
                _hudMode = (HudDisplayMode)(((int)_hudMode + 1) % 3);
                _controlMenu.SyncToggles(_autoCollapseEnabled, _hudMode, _shaderOverlayEnabled);
                string label = _hudMode == HudDisplayMode.Compact
                    ? "Compact Player HUD"
                    : (_hudMode == HudDisplayMode.FullTelemetry ? "Full Atlas Telemetry" : "Hidden");
                Notification.Show($"~p~HUD Mode: ~w~{label}");
            }
            else if (e.KeyCode == VendorKey)
            {
                _controlMenu.Close();
                _vendor.SummonVendor();
            }
            else if (e.KeyCode == LaunchTwinKey)
            {
                if (!ped.IsInVehicle() && _effectActive && _activeSurge && _entangledTwin != null && _entangledTwin.Exists())
                    LaunchEntangledTwin(notifyIfMissing: false);
            }
        }

        private void OnScriptAborted(object sender, EventArgs e)
        {
            ModLogger.Info("[ABORT] Script Aborted — cleaning up all V4 modifiers & squads.");
            ClearActiveEffects(notify: false, clearBreach: true);
            _vendor.CleanupVendor();
            CleanupContainmentSquad(markNoLongerNeeded: false);
            _atlas.Dispose();
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
            _vendor.Update(now, allowAmbientSpawn: !_isFetching && !_controlMenu.IsOpen && !_breachActive);

            // Restore stalled EMP vehicles after timer
            RestoreStalledVehicles(now);

            // Reality Instability decay
            UpdateInstabilityDecay(now);

            // 1. Instant charge-up feedback while Moth Atlas polls in background
            if (_isFetching)
            {
                if (!ped.IsAlive)
                {
                    Function.Call(Hash.ANIMPOSTFX_STOP, "FocusIn");
                }
                else if (now >= _nextChargeCueTime)
                {
                    _nextChargeCueTime = now + 650;
                    Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "NAV_UP_DOWN", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
                    GameplayCamera.Shake(CameraShake.Hand, 0.11f);
                }
            }

            // 2. Auto-trigger on Wanted Level spikes
            int currentWanted = player.WantedLevel;
            if (_autoCollapseEnabled && !_isFetching && currentWanted > _lastWantedLevel)
            {
                if (now - _lastAutoTriggerTime >= AutoCollapseCooldownMs)
                {
                    _lastAutoTriggerTime = now;
                    TriggerCollapse($"Auto (Wanted {currentWanted}*)", DefaultDurationMs, ElixirTier.None);
                }
            }
            _lastWantedLevel = currentWanted;

            // 3. Apply completed Atlas collapse
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

            // Patch delayed entanglement-shader-v1 output onto HUD/overlay as soon as ZIP downloads
            PendingShaderUpdate shaderUpdate = null;
            lock (_resultLock)
            {
                if (_pendingShaderUpdate != null)
                {
                    shaderUpdate = _pendingShaderUpdate;
                    _pendingShaderUpdate = null;
                }
            }
            if (shaderUpdate != null && _lastTelemetry != null && _lastTelemetry.CollapseId == shaderUpdate.CollapseId)
            {
                _lastTelemetry.ShaderJobId = ShortId(shaderUpdate.ShaderJobId);
                _lastTelemetry.ShaderPngPath = shaderUpdate.ShaderPngPath;
                _lastTelemetry.ShaderMeanR = shaderUpdate.MeanR;
                _lastTelemetry.ShaderMeanT = shaderUpdate.MeanT;
                if (!string.IsNullOrEmpty(shaderUpdate.ShaderPngPath) && _lastTelemetry.SourceLabel.StartsWith("LIVE"))
                    _lastTelemetry.SourceLabel = "LIVE ATLAS (4 ENGINES)";
                SetEngineBanner("SHADER READY · Engine: entanglement-shader-v1", now, 3200);
            }

            // 4. Containment Breach monitoring
            if (_breachActive)
            {
                if (!ped.IsAlive)
                {
                    CleanupContainmentSquad(markNoLongerNeeded: true);
                    _breachActive = false;
                    _instabilityPercent = 0f;
                }
                else if (now >= _breachEndTime)
                {
                    CleanupContainmentSquad(markNoLongerNeeded: true);
                    _breachActive = false;
                    _instabilityPercent = 25f;
                    Notification.Show("~g~CONTAINMENT SURVIVED! ~w~Reality Instability dropped to 25%.");
                }
            }

            // 5. Active reality effects + Phase 1/2/3 gameplay mechanics
            if (_effectActive)
            {
                if (!ped.IsAlive || now >= _effectEndTime)
                {
                    bool triggerOverdoseHangover = ped.IsAlive && _activeElixirTier == ElixirTier.SingularityOverdose;
                    ClearActiveEffects(notify: ped.IsAlive && !triggerOverdoseHangover, clearBreach: false);
                    if (triggerOverdoseHangover)
                    {
                        player.WantedLevel = Math.Max(player.WantedLevel, 4);
                        Notification.Show("~r~REALITY HANGOVER! ~w~Singularity Overdose triggered a 4-Star pursuit!");
                    }
                }
                else
                {
                    // Infinite health + Super Jump across all active modes ("energy state")
                    player.IsInvincible = true;
                    ped.Health = ped.MaxHealth;
                    player.SetSuperJumpThisFrame();

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
                        Vehicle playerVeh = ped.CurrentVehicle;
                        Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, playerVeh, false);
                        if (!playerVeh.IsEngineRunning)
                            playerVeh.IsEngineRunning = true;
                    }

                    // Explosive ammo + rapid clip during Overclock pillar buff or Singularity Overdose
                    if (_activeElixirTier == ElixirTier.SingularityOverdose || now < _overclockAmmoBuffEndTime)
                    {
                        player.SetExplosiveAmmoThisFrame();
                        Weapon curWep = ped.Weapons.Current;
                        if (curWep != null)
                        {
                            curWep.InfiniteAmmoClip = true;
                            _modifiedClipWeapons.Add(curWep.Hash);
                        }
                    }

                    // Active ability: Twin Launch (Horn while driving)
                    if (_entangledTwin != null && _entangledTwin.Exists() && ped.IsInVehicle() &&
                        Game.IsControlJustPressed(GTA.Control.VehicleHorn))
                    {
                        LaunchEntangledTwin(notifyIfMissing: false);
                    }

                    // Singularity constructive phase continuous vortex suction
                    if (_activeWeatherIndex >= 7 && _lastOtocValue >= 0 && now >= _nextSuctionTickTime)
                    {
                        _nextSuctionTickTime = now + 110;
                        ApplySingularitySuctionTick(ped);
                    }

                    // Screen Shader Vision overlay
                    _hud.DrawShaderVisionOverlay(
                        _lastTelemetry,
                        _effectActive,
                        _activeElixirTier != ElixirTier.None,
                        _overdriveActive,
                        _shaderOverlayEnabled,
                        now,
                        _lastOtocBeatTime,
                        _lastOtocValue
                    );

                    // World-anchored street rings + Floating entity cards & Linked Car tether
                    _hud.DrawHotspotMarkers(
                        ped.Position,
                        _activeHotspots,
                        _activeWeatherIndex,
                        now,
                        _nextAnomalyPulseTime,
                        _lastOtocValue
                    );
                    _hud.DrawWorldEntityTagsAndTether(ped, _entangledTwin, _activeHotspots, _activeWeatherIndex, _lastTelemetry, _hudMode, _lastOtocValue);

                    // Active Entangled Twin mirroring, auto-ram, and teleport recovery
                    if (_activeSurge)
                        UpdateEntangledTwinBehavior(ped, now);

                    // 8-beat OTOC Quantum Aftershock waves
                    if (now >= _nextAnomalyPulseTime)
                    {
                        _nextAnomalyPulseTime = now + _otocPulseIntervalMs;
                        PulseOtocAftershock(ped, now);
                    }
                }
            }

            // Keep syncing telemetry snapshot HUD values cleanly
            if (_lastTelemetry != null)
            {
                _lastTelemetry.InstabilityPercent = _instabilityPercent;
                _lastTelemetry.OverdriveActive = _overdriveActive;
                _lastTelemetry.ContainmentBreachActive = _breachActive;
                _lastTelemetry.ActiveModeName = _activeModeShortName;
            }

            // 6. 3-Mode F9 HUD
            bool twinActive = _entangledTwin != null && _entangledTwin.Exists() && _entangledTwin.IsAlive;
            _hud.DrawHud(
                _hudMode,
                now,
                _isFetching,
                _lastFetchReason,
                _autoCollapseEnabled,
                _effectActive,
                _effectStartTime,
                _effectEndTime,
                _activeModeShortName,
                _activeEffectLabel,
                _instabilityPercent,
                _overdriveActive,
                _breachActive,
                _breachEndTime,
                twinActive,
                _activeOtocSteps,
                _activeOtocStep,
                _lastTelemetry
            );
            _hud.DrawEngineBanner(_hudMode, now, _effectActive, _effectEndTime, _activeModeShortName, _engineBannerText, _engineBannerEndTime);
        }

        private void SetEngineBanner(string text, int now, int durationMs)
        {
            _engineBannerText = text;
            _engineBannerEndTime = now + durationMs;
        }

        private void TriggerCollapse(string reason, int durationMs, ElixirTier tier)
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

                Vector3 pos = Game.Player.Character.Position;
                int shaderIdx = ++_shaderCounter;
                string shaderPngPath = Path.Combine(_shaderCacheDir, $"qshader_{shaderIdx}.png");
                bool forceSurge = tier == ElixirTier.UnstableBrew || tier == ElixirTier.SingularityOverdose;

                // Instant Phase 1 Reality Charge-Up audio + visual cue while Atlas polls
                _nextChargeCueTime = Game.GameTime + 650;
                SetEngineBanner(
                    _offlineTestMode
                        ? "SANDBOX TEST · Simulating 4 Moth Engines locally"
                        : "MOTH ATLAS · Calling blur-core, coin-toss, otoc-echo, shader...",
                    Game.GameTime,
                    4800
                );
                Function.Call(Hash.ANIMPOSTFX_PLAY, "FocusIn", 1800, false);
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "FocusIn", "HintCamSounds", true);
                GameplayCamera.Shake(CameraShake.Hand, 0.16f);

                ModLogger.Info($"[COLLAPSE_START] reason='{reason}' durMs={durationMs} tier={tier} bias={biasMode} chaos={chaos.Total:F2} stars={chaos.WantedStars} peds={chaos.NearbyPeds} vehs={chaos.NearbyVehicles} hp={chaos.PlayerHp} origin=({pos.X:F1},{pos.Y:F1},{pos.Z:F1})");
                Task.Run(() => FetchQuantumStateAsync(
                    reason, durationMs, tier, forceSurge, biasMode,
                    chaos, spatialGrid, densitySum,
                    pos.X, pos.Y, pos.Z, shaderPngPath
                ));
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
            ElixirTier tier,
            bool forceSurge,
            QuantumBiasMode biasMode,
            ChaosSnapshot chaos,
            double[][] spatialGrid,
            double densitySum,
            float originX,
            float originY,
            float originZ,
            string shaderPngPath)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                QuantumBiasMode effectiveBias = tier == ElixirTier.SingularityOverdose && biasMode == QuantumBiasMode.AutoChaos
                    ? QuantumBiasMode.FavorSingularity
                    : biasMode;

                double[] weatherWeights = BuildInitialWeatherWeights(chaos.Total, effectiveBias);
                double blurStrength = Math.Max(0.25, Math.Min(0.95, 0.25 + chaos.Total * 0.7));

                if (_offlineTestMode)
                {
                    await Task.Delay(750);
                    sw.Stop();
                    var simRng = new Random();
                    double wSum = Math.Max(1e-9, weatherWeights.Sum());
                    double[] simProbs = weatherWeights.Select(w => w / wSum).ToArray();
                    int simIdx = SampleFromDistribution(simProbs, simRng);
                    var simHotspots = ExtractHotspots(spatialGrid, simRng);
                    int simHeads = simRng.Next(7, 15);
                    bool simSurge = forceSurge || simHeads >= 8;
                    double[] simOtoc = { 0.95, 0.55, -0.62, 0.42, -0.78, 0.35, -0.85, 0.25 };
                    int simCollapseId = shaderPngPath.GetHashCode() ^ Environment.TickCount;

                    ModLogger.Info($"[SANDBOX] Local collapse mode idx={simIdx} surge={simSurge} (API disabled via OfflineTestMode=true).");

                    lock (_resultLock)
                    {
                        _pendingCollapse = new PendingCollapse
                        {
                            WeatherIndex = simIdx,
                            FlashIntensity = simHeads / 16f,
                            Surge = simSurge,
                            ChaosLevel = chaos.Total,
                            UsedFallback = false,
                            DurationMs = durationMs,
                            ElixirTier = tier,
                            OriginX = originX,
                            OriginY = originY,
                            OriginZ = originZ,
                            Hotspots = simHotspots,
                            OtocEchoSteps = simOtoc,
                            ShaderPngPath = null,
                            Telemetry = new TelemetrySnapshot
                            {
                                CollapseId = simCollapseId,
                                TriggerReason = reason,
                                BiasLabel = FormatBiasLabel(effectiveBias),
                                SourceLabel = "LOCAL SANDBOX (NO API)",
                                LatencySec = sw.Elapsed.TotalSeconds,
                                Chaos = chaos,
                                BlurStrength = blurStrength,
                                SentWeatherPriors = weatherWeights,
                                RecvWeatherProbs = simProbs,
                                ChosenWeatherIndex = simIdx,
                                InputGrid = spatialGrid,
                                BlurredGrid = spatialGrid,
                                GridDensitySum = densitySum,
                                Hotspots = simHotspots,
                                CoinHeads = simHeads,
                                CoinShots = 16,
                                FlashMs = (int)((simHeads / 16f) * 1800),
                                Surge = simSurge,
                                OtocEchoSteps = simOtoc,
                                ActiveOtocStep = 0,
                                ShaderStyle = "sandbox",
                                ShaderMeanR = 0.0,
                                ShaderMeanT = 0.0,
                                ShaderPngPath = null,
                                WeatherJobId = "sandbox",
                                SpatialJobId = "sandbox",
                                CoinJobId = "sandbox",
                                OtocJobId = "sandbox",
                                ShaderJobId = "off"
                            }
                        };
                    }
                    return;
                }

                string shaderStyle = PickShaderStyle(effectiveBias, chaos.Total);
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
                    incoming_rays = 6,
                    interaction = shaderInteract,
                    layers = 2,
                    reflectance = shaderReflect,
                    resolution = 16,
                    style = shaderStyle
                });

                // Apply reality shift as soon as the 4 fast JSON engines complete (~4-6s) without blocking on shader ZIP
                await Task.WhenAll(weatherTask, spatialTask, coinTask, otocTask);
                sw.Stop();

                var (weatherRes, weatherJobId, weatherErr) = weatherTask.Result;
                var (spatialRes, spatialJobId, spatialErr) = spatialTask.Result;
                var (coinRes, coinJobId, coinErr) = coinTask.Result;
                var (otocRes, otocJobId, otocErr) = otocTask.Result;

                if (weatherErr != null) _notifications.Enqueue($"~r~Atlas weather error: {weatherErr}");
                if (spatialErr != null) _notifications.Enqueue($"~r~Atlas spatial error: {spatialErr}");
                if (coinErr != null) _notifications.Enqueue($"~r~Atlas coin error: {coinErr}");
                if (otocErr != null) _notifications.Enqueue($"~r~Atlas OTOC error: {otocErr}");

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
                bool surge = forceSurge;
                bool coinOk = false;
                if (coinRes is JObject coinObj && coinObj["heads"] != null && coinObj["shots"] != null)
                {
                    coinOk = true;
                    heads = coinObj["heads"].ToObject<int>();
                    shots = Math.Max(1, coinObj["shots"].ToObject<int>());
                    flashIntensity = Math.Max(0.3f, Math.Min(1.0f, (float)heads / shots));
                    surge = forceSurge || heads >= (shots / 2);
                }

                double[] otocSteps = ParseOtocEchoSteps(otocRes, out bool otocOk);

                int collapseId = shaderPngPath.GetHashCode() ^ Environment.TickCount;
                string shaderJobId = "loading";
                double meanR = 0.0, meanT = 0.0;
                string finalShaderPath = null;
                bool shaderDoneNow = shaderTask.IsCompleted;
                if (shaderDoneNow)
                {
                    var (shaderZip, sJobId, shaderErr) = shaderTask.Result;
                    shaderJobId = sJobId;
                    if (shaderErr != null) _notifications.Enqueue($"~r~Atlas shader error: {shaderErr}");
                    if (shaderZip != null && ShaderTextureBuilder.TryBuildTextureFromZip(shaderZip, shaderPngPath, out meanR, out meanT))
                        finalShaderPath = shaderPngPath;
                }
                else
                {
                    _ = CompleteDelayedShaderAsync(shaderTask, shaderPngPath, collapseId);
                }

                bool coreOk = weatherOk && spatialOk && coinOk && otocOk;
                bool anyOk = weatherOk || spatialOk || coinOk || otocOk;
                bool fallback = !coreOk;
                string sourceLabel = coreOk
                    ? (finalShaderPath != null ? "LIVE ATLAS (4 ENGINES)" : "LIVE ATLAS (SHADER STREAMING)")
                    : (anyOk ? "PARTIAL ATLAS" : "FALLBACK (API offline/no key)");

                var telemetry = new TelemetrySnapshot
                {
                    CollapseId = collapseId,
                    TriggerReason = reason,
                    BiasLabel = FormatBiasLabel(effectiveBias),
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
                        ElixirTier = tier,
                        OriginX = originX,
                        OriginY = originY,
                        OriginZ = originZ,
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
                        Surge = forceSurge,
                        ChaosLevel = chaos.Total,
                        UsedFallback = true,
                        DurationMs = durationMs,
                        ElixirTier = tier,
                        OriginX = originX,
                        OriginY = originY,
                        OriginZ = originZ,
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
                            Surge = forceSurge,
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

        private async Task CompleteDelayedShaderAsync(
            Task<(byte[] FileBytes, string JobId, string Error)> shaderTask,
            string shaderPngPath,
            int collapseId)
        {
            try
            {
                var (shaderZip, sJobId, shaderErr) = await shaderTask;
                if (shaderErr != null)
                {
                    ModLogger.Warn($"[SHADER_DELAYED] job_id={sJobId ?? "none"} err={shaderErr}");
                    return;
                }

                double meanR = 0.0, meanT = 0.0;
                if (shaderZip != null && ShaderTextureBuilder.TryBuildTextureFromZip(shaderZip, shaderPngPath, out meanR, out meanT))
                {
                    ModLogger.Info($"[SHADER_DELAYED] job_id={sJobId} ready png='{Path.GetFileName(shaderPngPath)}' meanR={meanR:F3} meanT={meanT:F3}");
                    lock (_resultLock)
                    {
                        _pendingShaderUpdate = new PendingShaderUpdate
                        {
                            CollapseId = collapseId,
                            ShaderJobId = sJobId,
                            ShaderPngPath = shaderPngPath,
                            MeanR = meanR,
                            MeanT = meanT
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[SHADER_DELAYED] exception={ex.Message}");
            }
        }

        private void ApplyQuantumReality(PendingCollapse collapse, int now)
        {
            bool wasAlreadyActive = _effectActive;
            bool isFullFallback = collapse.Telemetry?.SourceLabel != null &&
                                  collapse.Telemetry.SourceLabel.StartsWith("FALLBACK");

            int idx = Math.Max(0, Math.Min(collapse.WeatherIndex, _weatherTypes.Length - 1));
            int durationMs = collapse.DurationMs;
            bool surgeAllowed = collapse.Surge ||
                                collapse.ElixirTier == ElixirTier.UnstableBrew ||
                                collapse.ElixirTier == ElixirTier.SingularityOverdose;

            // Clearly differentiate offline fallback from live Moth Atlas runs
            if (isFullFallback)
            {
                idx = Math.Min(idx, 6); // Singularity requires live Atlas
                durationMs = Math.Min(durationMs, 15000);
                surgeAllowed = false;
            }

            string weather = _weatherTypes[idx];

            var player = Game.Player;
            var ped = player.Character;

            // Chain collapse + Overdrive + Instability calculation
            bool isChain = !isFullFallback && (wasAlreadyActive || collapse.ElixirTier == ElixirTier.SingularityOverdose);
            _overdriveActive = isChain;

            float instGain = collapse.ElixirTier == ElixirTier.SingularityOverdose
                ? 50f
                : (collapse.ElixirTier == ElixirTier.UnstableBrew
                    ? 35f
                    : (isChain ? 42f : 22f));
            _instabilityPercent = Math.Min(100f, _instabilityPercent + instGain);

            _effectActive = true;
            _activeElixirTier = collapse.ElixirTier;
            _activeWeatherIndex = idx;
            _activeSurge = surgeAllowed;
            _effectStartTime = now;
            _effectEndTime = now + durationMs;
            _otocPulseIntervalMs = collapse.ElixirTier == ElixirTier.SingularityOverdose ? 2200 : DefaultOtocIntervalMs;
            _nextAnomalyPulseTime = now + 550;
            _activeOtocSteps = collapse.OtocEchoSteps ?? new[] { 1.0, 0.72, 0.45, -0.35, 0.28, -0.22, 0.15, -0.10 };
            _activeOtocStep = -1;
            _lastOtocValue = _activeOtocSteps[0];
            _slowMoEndTime = 0;
            _stealthEndTime = 0;
            _overclockAmmoBuffEndTime = 0;
            Game.TimeScale = 1.0f;
            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player, false);

            // World-anchor all 3 strike pillars at collapse time (keep 1 Overclock pillar following player)
            Vector3 anchorCenter = ped != null && ped.Exists()
                ? ped.Position
                : new Vector3(collapse.OriginX, collapse.OriginY, collapse.OriginZ);
            _activeHotspots = collapse.Hotspots ?? new List<HotspotTarget>();
            for (int i = 0; i < _activeHotspots.Count; i++)
            {
                var h = _activeHotspots[i];
                Vector3 rawPos = new Vector3(anchorCenter.X + h.OffsetX, anchorCenter.Y + h.OffsetY, anchorCenter.Z);
                Vector3 candidate = World.GetSafeCoordForPed(rawPos, true, 16);
                if (candidate == Vector3.Zero || Math.Abs(candidate.Z - anchorCenter.Z) > 6.5f)
                {
                    Vector3 street = World.GetNextPositionOnStreet(rawPos, true);
                    candidate = (street != Vector3.Zero && Math.Abs(street.Z - anchorCenter.Z) <= 6.5f) ? street : rawPos;
                }
                Vector3 snapped = SnapToGround(candidate.X, candidate.Y, candidate.Z);
                h.WorldX = snapped.X;
                h.WorldY = snapped.Y;
                h.GroundZ = snapped.Z;
                h.FollowPlayer = false; // Keep all 3 ground circles locked to accessible street/sidewalk spots
            }

            // Transition out of charge-up into collapse shock
            Function.Call(Hash.ANIMPOSTFX_STOP, "FocusIn");
            Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, weather);
            int flashDuration = (int)(collapse.FlashIntensity * 1800);
            Function.Call(Hash.ANIMPOSTFX_PLAY, "RaceTurbo", flashDuration, false);
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "FocusOut", "HintCamSounds", true);
            GameplayCamera.Shake(CameraShake.Jolt, _activeSurge || _overdriveActive ? 0.90f : 0.45f);

            // Acquire Entangled Twin if SURGE active (releasing stale handle cleanly)
            if (_activeSurge && ped != null && ped.Exists())
            {
                if (_entangledTwin == null || !_entangledTwin.Exists() || !_entangledTwin.IsAlive)
                {
                    _entangledTwin = World.GetNearbyVehicles(ped.Position, 65f)
                        .Where(v => v != null && v.Exists() && v.IsAlive && v != ped.CurrentVehicle)
                        .OrderBy(v => v.Position.DistanceTo(ped.Position))
                        .FirstOrDefault();
                }
            }
            else
            {
                if (_entangledTwin != null && _entangledTwin.Exists())
                    _entangledTwin.MarkAsNoLongerNeeded();
                _entangledTwin = null;
            }

            string effectLabel;
            if (idx <= 1)
            {
                _activeModeShortName = "OVERCLOCK";
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.35f);
                ped.Health = Math.Min(ped.MaxHealth, ped.Health + 25);
                if (player.WantedLevel > 0)
                    player.WantedLevel = Math.Max(0, player.WantedLevel - 1);
                effectLabel = "Overclock (Pillar Buffs + Expl. Ammo)";
            }
            else if (idx <= 4)
            {
                _activeModeShortName = "TIME DILATION";
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.49f);
                Function.Call(Hash.SET_SWIM_MULTIPLIER_FOR_PLAYER, player, 1.35f);
                Game.TimeScale = 0.58f;
                _slowMoEndTime = now + 2000;
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player, true);
                _stealthEndTime = now + 7000;
                effectLabel = "Time Dilation (1.49x Speed + Slow-Mo + Stealth)";
            }
            else if (idx <= 6)
            {
                _activeModeShortName = "QUANTUM STORM";
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.05f);
                Function.Call(Hash.FORCE_LIGHTNING_FLASH);
                Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 700, false);
                effectLabel = "Quantum Storm (EMP Stalls + Police Zaps)";
            }
            else
            {
                _activeModeShortName = "SINGULARITY";
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.15f);
                Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 1000, false);
                effectLabel = "Singularity (OTOC Pull -> Detonation)";
            }

            if (_overdriveActive)
            {
                Function.Call(Hash.FORCE_LIGHTNING_FLASH);
                effectLabel = "[OVERDRIVE] " + effectLabel;
            }

            _activeEffectLabel = effectLabel;

            string sourceTag = _offlineTestMode
                ? "~b~(Sandbox - No API)"
                : (isFullFallback
                    ? "~r~(FALLBACK - NO ATLAS KEY)"
                    : (collapse.UsedFallback ? "~o~(Partial Atlas)" : "~g~(Atlas V4)"));
            string surgeTag = _activeSurge ? " ~r~[TWIN]" : "";
            string odTag = _overdriveActive ? " ~p~[OVERDRIVE]" : "";
            ModLogger.Info($"[COLLAPSE_APPLY] weather={weather} mode='{_activeModeShortName}' source='{collapse.Telemetry?.SourceLabel}' latency={collapse.Telemetry?.LatencySec:F2}s durMs={durationMs} surge={_activeSurge} overdrive={_overdriveActive} instab={_instabilityPercent:F0}% twin={(_entangledTwin != null && _entangledTwin.Exists() ? _entangledTwin.DisplayName : "none")} shaderR={collapse.Telemetry?.ShaderMeanR:F3} shaderT={collapse.Telemetry?.ShaderMeanT:F3}");
            SetEngineBanner(
                $"{_activeModeShortName} · Engines: blur-core-v1 + otoc-echo-v1" + (_activeSurge ? " + coin-toss-v1" : ""),
                now,
                5200
            );
            Notification.Show($"~p~{weather}{surgeTag}{odTag} {sourceTag}\n~w~{effectLabel}");

            // Trigger Containment Breach if Reality Instability hits 100%!
            if (_instabilityPercent >= 100f && !_breachActive)
            {
                TriggerContainmentBreach(ped, now);
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
            ModLogger.Info($"[OTOC_BEAT] step={_activeOtocStep + 1}/8 F_re={echoVal:F4} kind={(scrambled ? "SCRAMBLED" : "CONSTRUCTIVE")} mode={_activeModeShortName}");
            SetEngineBanner($"{_activeModeShortName} BEAT ({_activeOtocStep + 1}/8) · Engine: otoc-echo-v1", now, 1800);
            float amp = (float)Math.Max(0.25, Math.Min(1.25, Math.Abs(echoVal)));
            float tierMult = _activeElixirTier == ElixirTier.UnstableBrew || _activeElixirTier == ElixirTier.SingularityOverdose ? 1.3f : 1.0f;
            float odMult = _overdriveActive ? 1.45f : 1.0f;
            float surgeMult = _activeSurge ? 1.25f : 1.0f;
            float forceScale = (0.72f + 0.65f * amp) * surgeMult * tierMult * odMult * (scrambled ? 1.2f : 1.0f);

            Vector3 playerPos = playerPed.Position;

            // 1. Overclock Mode: Stepping inside any green pillar grants armor, repair & 4s explosive ammo burst!
            if (_activeWeatherIndex <= 1)
            {
                int heal = 3 + (int)(6 * amp);
                if (playerPed.IsAlive && playerPed.Health < playerPed.MaxHealth)
                    playerPed.Health = Math.Min(playerPed.MaxHealth, playerPed.Health + heal);

                if (_activeElixirTier == ElixirTier.SingularityOverdose && playerPed.Armor < 100)
                    playerPed.Armor = Math.Min(100, playerPed.Armor + 12);

                foreach (var h in _activeHotspots)
                {
                    Vector3 spot = _hud.GetHotspotWorldPos(playerPos, h);
                    if (spot.DistanceTo(playerPos) <= 13f)
                    {
                        if (playerPed.Armor < 100)
                            playerPed.Armor = Math.Min(100, playerPed.Armor + 14);
                        if (playerPed.IsInVehicle() && playerPed.CurrentVehicle != null && playerPed.CurrentVehicle.Exists())
                            playerPed.CurrentVehicle.Repair();
                        _overclockAmmoBuffEndTime = now + 4200;
                        Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "PICK_UP", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
                        break;
                    }
                }
                return;
            }

            // 2. Time Dilation & Stealth Mode: Recurring 1.2s slow-mo pulses on constructive beats across the whole 30s!
            if (_activeWeatherIndex <= 4)
            {
                if (!scrambled)
                {
                    Game.TimeScale = 0.58f;
                    _slowMoEndTime = now + 1250;
                    Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, true);
                    _stealthEndTime = Math.Max(_stealthEndTime, now + 3800);
                }

                foreach (var h in _activeHotspots.Take(2))
                {
                    Vector3 spot = _hud.GetHotspotWorldPos(playerPos, h);
                    if (spot.DistanceTo(playerPos) > 120f) continue;

                    foreach (Vehicle veh in World.GetNearbyVehicles(spot, 16f))
                    {
                        if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == _entangledTwin) continue;
                        veh.ApplyForce(new Vector3(0f, 0f, (scrambled ? 4.5f : 2.0f) * forceScale), Vector3.Zero, ForceType.MaxForceRot);
                    }
                }
                return;
            }

            // 3. Quantum Storm Mode: EMP blasts and engine stalls strictly localized to the 3 Atlas street circles!
            if (_activeWeatherIndex <= 6)
            {
                if (scrambled || _overdriveActive || _activeWeatherIndex == 6)
                    Function.Call(Hash.FORCE_LIGHTNING_FLASH);

                GameplayCamera.Shake(CameraShake.SmallExplosion, Math.Min(0.65f, 0.18f * forceScale));

                foreach (Vehicle veh in World.GetNearbyVehicles(playerPos, 50f))
                {
                    if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == _entangledTwin) continue;
                    ApplyIcyGrip(veh);
                }

                // EMP explosions, engine shutdowns, and upward tosses happen strictly inside/around the 3 Atlas circles
                foreach (var h in _activeHotspots)
                {
                    Vector3 spot = _hud.GetHotspotWorldPos(playerPos, h);
                    if (spot.DistanceTo(playerPos) > 160f) continue;

                    if (spot.DistanceTo(playerPos) >= 8f)
                        World.AddExplosion(spot, ExplosionType.EmpLauncherEmp, 2.8f + 1.4f * amp, 0.25f * amp, null, true, false);

                    foreach (Vehicle veh in World.GetNearbyVehicles(spot, 14f))
                    {
                        if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == _entangledTwin) continue;
                        StallVehicleEmp(veh, now + 5000);
                        veh.ApplyForce(new Vector3(0f, 0f, 5.0f * forceScale), new Vector3(0.3f, 0.2f, 0f), ForceType.MaxForceRot);
                    }

                    foreach (Ped p in World.GetNearbyPeds(spot, 12f))
                    {
                        if (p == null || !p.Exists() || p.IsPlayer) continue;
                        p.Ragdoll(2200, RagdollType.Relax);
                    }
                }

                PanicNearbyWitnesses(playerPed, playerPos);
                return;
            }

            // 4. Quantum Singularity Mode: Constructive OTOC (pull) vs. Scrambled OTOC (outward RayGun detonation!)
            GameplayCamera.Shake(CameraShake.SmallExplosion, Math.Min(0.8f, (scrambled ? 0.28f : 0.12f) * forceScale));

            foreach (Vehicle veh in World.GetNearbyVehicles(playerPos, 55f))
            {
                if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == _entangledTwin) continue;
                ApplyIcyGrip(veh);
            }

            if (scrambled)
            {
                // Violent outward + upward RayGun detonation of everything pulled into the pillars!
                Function.Call(Hash.FORCE_LIGHTNING_FLASH);
                foreach (var h in _activeHotspots)
                {
                    Vector3 spot = _hud.GetHotspotWorldPos(playerPos, h);
                    if (spot.DistanceTo(playerPos) > 160f) continue;

                    if (spot.DistanceTo(playerPos) >= 8f)
                        World.AddExplosion(spot, ExplosionType.RayGun, 3.2f + 1.5f * amp, 0.35f * amp, null, true, false);

                    foreach (Vehicle veh in World.GetNearbyVehicles(spot, 22f))
                    {
                        if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == _entangledTwin) continue;
                        Vector3 outward = veh.Position - spot;
                        outward.Z = 0f;
                        if (outward.Length() < 0.5f) outward = new Vector3(1f, 0f, 0f);
                        outward.Normalize();
                        veh.ApplyForce(outward * (8.5f * forceScale) + new Vector3(0f, 0f, 11.5f * forceScale), new Vector3(0.5f, 0.3f, 0f), ForceType.MaxForceRot);
                    }

                    foreach (Ped p in World.GetNearbyPeds(spot, 16f))
                    {
                        if (p == null || !p.Exists() || p.IsPlayer) continue;
                        p.Ragdoll(2600, RagdollType.Relax);
                    }
                }
            }
            else
            {
                // Constructive beat burst: sharp inward gravitational collapse wave
                foreach (var h in _activeHotspots)
                {
                    Vector3 spot = _hud.GetHotspotWorldPos(playerPos, h);
                    if (spot.DistanceTo(playerPos) > 160f) continue;

                    foreach (Vehicle veh in World.GetNearbyVehicles(spot, 28f))
                    {
                        if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == _entangledTwin) continue;
                        Vector3 pull = spot - veh.Position;
                        float dist = pull.Length();
                        if (dist > 3f)
                        {
                            pull.Normalize();
                            veh.ApplyForce(pull * (10.0f * forceScale) + new Vector3(0f, 0f, 1.5f), Vector3.Zero, ForceType.MaxForceRot);
                        }
                    }
                }
            }

            PanicNearbyWitnesses(playerPed, playerPos);
        }

        private void ApplySingularitySuctionTick(Ped playerPed)
        {
            Vector3 playerPos = playerPed.Position;
            foreach (var h in _activeHotspots)
            {
                Vector3 spot = _hud.GetHotspotWorldPos(playerPos, h);
                if (spot.DistanceTo(playerPos) > 140f) continue;

                foreach (Vehicle veh in World.GetNearbyVehicles(spot, 26f))
                {
                    if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == _entangledTwin) continue;
                    Vector3 pull = spot - veh.Position;
                    if (pull.Length() > 3.5f)
                    {
                        pull.Normalize();
                        veh.ApplyForce(pull * 2.6f + new Vector3(0f, 0f, 0.45f), Vector3.Zero, ForceType.MaxForceRot);
                    }
                }

                foreach (Ped p in World.GetNearbyPeds(spot, 18f))
                {
                    if (p == null || !p.Exists() || p.IsPlayer || !p.IsAlive) continue;
                    Vector3 pull = spot - p.Position;
                    if (pull.Length() > 2.5f)
                    {
                        pull.Normalize();
                        p.ApplyForce(pull * 2.2f + new Vector3(0f, 0f, 0.35f), Vector3.Zero, ForceType.MaxForceRot);
                    }
                }
            }
        }

        private void ApplyIcyGrip(Vehicle veh)
        {
            if (veh == null || !veh.Exists()) return;
            Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, veh, true);
            _icyVehicles[veh.Handle] = veh;
        }

        private void StallVehicleEmp(Vehicle veh, int restoreAt)
        {
            if (veh == null || !veh.Exists()) return;
            veh.IsEngineRunning = false;
            veh.AreLightsOn = false;
            veh.IsAlarmSet = true;
            veh.StartAlarm();
            _stalledVehicles[veh.Handle] = (veh, restoreAt);
        }

        private void RestoreStalledVehicles(int now)
        {
            if (_stalledVehicles.Count == 0) return;
            var toRemove = new List<int>();
            foreach (var kv in _stalledVehicles)
            {
                Vehicle veh = kv.Value.veh;
                int restoreAt = kv.Value.restoreAt;
                if (veh == null || !veh.Exists())
                {
                    toRemove.Add(kv.Key);
                    continue;
                }
                if (now >= restoreAt)
                {
                    veh.IsEngineRunning = true;
                    veh.AreLightsOn = true;
                    veh.IsAlarmSet = false;
                    toRemove.Add(kv.Key);
                }
            }
            foreach (int handle in toRemove)
                _stalledVehicles.Remove(handle);
        }

        private void UpdateEntangledTwinBehavior(Ped playerPed, int now)
        {
            if (_entangledTwin == null || !_entangledTwin.Exists() || !_entangledTwin.IsAlive)
            {
                if (_entangledTwin != null)
                {
                    if (_entangledTwin.Exists())
                        _entangledTwin.MarkAsNoLongerNeeded();
                    _entangledTwin = null;
                    Notification.Show("~o~Entangled Twin lost.");
                }
                return;
            }

            float dist = _entangledTwin.Position.DistanceTo(playerPed.Position);

            // Tether recovery if Twin gets left > 95m behind (keep on same elevation road, avoiding hills/overpasses)
            if (dist > 95f)
            {
                Vector3 desired = playerPed.Position + playerPed.ForwardVector * 14f;
                Vector3 streetSpot = World.GetNextPositionOnStreet(desired, true);
                if (streetSpot == Vector3.Zero || Math.Abs(streetSpot.Z - playerPed.Position.Z) > 5.5f)
                {
                    Vector3 fallbackPos = playerPed.Position - playerPed.ForwardVector * 7f;
                    streetSpot = SnapToGround(fallbackPos.X, fallbackPos.Y, playerPed.Position.Z);
                }
                _entangledTwin.Position = streetSpot + new Vector3(0f, 0f, 0.8f);
                _entangledTwin.PlaceOnGround();
                Function.Call(Hash.ANIMPOSTFX_PLAY, "RaceTurbo", 350, false);
                Notification.Show("~p~Entangled Twin phased back nearby!");
                return;
            }

            // Standard 150ms movement mirroring
            if (now >= _nextTwinMirrorTime)
            {
                _nextTwinMirrorTime = now + 150;
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

            // Auto-ram nearby police / containment threats in combat
            if (now >= _nextTwinRamTime && (Game.Player.WantedLevel > 0 || _breachActive))
            {
                _nextTwinRamTime = now + 2400;
                Entity hostile = FindNearestTwinTarget(playerPed, maxRadius: 48f, preferAim: false);
                if (hostile != null && hostile.Exists())
                {
                    Vector3 dir = hostile.Position - _entangledTwin.Position;
                    if (dir.Length() > 2.5f)
                    {
                        dir.Normalize();
                        _entangledTwin.ApplyForce(dir * 22f + new Vector3(0f, 0f, 1.6f), Vector3.Zero, ForceType.MaxForceRot);
                    }
                }
            }
        }

        private void LaunchEntangledTwin(bool notifyIfMissing)
        {
            if (_entangledTwin == null || !_entangledTwin.Exists() || !_entangledTwin.IsAlive)
            {
                if (notifyIfMissing)
                    Notification.Show("~o~No active [SURGE+TWIN] car linked right now.");
                return;
            }

            int now = Game.GameTime;
            if (now < _nextTwinLaunchAllowedTime) return;
            _nextTwinLaunchAllowedTime = now + 1800;

            Ped ped = Game.Player.Character;
            Entity target = FindNearestTwinTarget(ped, maxRadius: 85f, preferAim: true);

            Vector3 launchDir;
            if (target != null && target.Exists())
            {
                launchDir = (target.Position + new Vector3(0f, 0f, 0.7f)) - _entangledTwin.Position;
            }
            else
            {
                launchDir = GameplayCamera.Direction;
            }

            if (launchDir.Length() < 0.1f) launchDir = ped.ForwardVector;
            launchDir.Normalize();

            _entangledTwin.ApplyForce(launchDir * 42f + new Vector3(0f, 0f, 5.5f), new Vector3(0.4f, 0.2f, 0f), ForceType.MaxForceRot);
            ModLogger.Info($"[TWIN_LAUNCH] twin={_entangledTwin.DisplayName} target={(target != null && target.Exists() ? target.GetType().Name : "CameraForward")} dir=({launchDir.X:F2},{launchDir.Y:F2},{launchDir.Z:F2})");
            SetEngineBanner("LINKED CAR THROWN · Engine: coin-toss-v1", now, 2800);
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "FocusOut", "HintCamSounds", true);
            GameplayCamera.Shake(CameraShake.SmallExplosion, 0.35f);
            Notification.Show("~p~LINKED CAR THROWN!");
        }

        private Entity FindNearestTwinTarget(Ped playerPed, float maxRadius, bool preferAim)
        {
            if (preferAim && Game.Player.TargetedEntity != null && Game.Player.TargetedEntity.Exists() &&
                Game.Player.TargetedEntity != _entangledTwin && Game.Player.TargetedEntity != playerPed.CurrentVehicle)
            {
                return Game.Player.TargetedEntity;
            }

            // Prefer containment agents or police vehicles first
            Ped containment = _containmentPeds
                .Where(p => p != null && p.Exists() && p.IsAlive && p.Position.DistanceTo(_entangledTwin.Position) <= maxRadius)
                .OrderBy(p => p.Position.DistanceTo(_entangledTwin.Position))
                .FirstOrDefault();
            if (containment != null) return containment;

            Vehicle policeVeh = World.GetNearbyVehicles(playerPed.Position, maxRadius)
                .Where(v => v != null && v.Exists() && v.IsAlive && v != playerPed.CurrentVehicle && v != _entangledTwin &&
                            (v.ClassType == VehicleClass.Emergency || v.HasSiren))
                .OrderBy(v => v.Position.DistanceTo(_entangledTwin.Position))
                .FirstOrDefault();
            if (policeVeh != null) return policeVeh;

            return World.GetNearbyVehicles(playerPed.Position, maxRadius)
                .Where(v => v != null && v.Exists() && v.IsAlive && v != playerPed.CurrentVehicle && v != _entangledTwin)
                .OrderBy(v => v.Position.DistanceTo(_entangledTwin.Position))
                .FirstOrDefault();
        }

        private void TriggerContainmentBreach(Ped playerPed, int now)
        {
            CleanupContainmentSquad(markNoLongerNeeded: true);

            _breachActive = true;
            _breachEndTime = now + ContainmentDurationMs;
            _containmentGroup = World.AddRelationshipGroup("QUANTUM_CONTAINMENT");
            _containmentGroup.SetRelationshipBetweenGroups(playerPed.RelationshipGroup, Relationship.Hate, true);

            var rng = new Random();
            for (int squad = 0; squad < 2; squad++)
            {
                double angle = (squad == 0 ? 0.5 : 3.6) + rng.NextDouble() * 0.6;
                Vector3 desired = playerPed.Position + new Vector3((float)Math.Cos(angle) * 48f, (float)Math.Sin(angle) * 48f, 0f);
                Vector3 streetPos = World.GetNextPositionOnStreet(desired, true);
                if (streetPos == Vector3.Zero) streetPos = desired;

                Vehicle suv = World.CreateVehicle(new Model(VehicleHash.FBI2), streetPos, (float)(angle * 57.3 + 180.0));
                if (suv != null && suv.Exists())
                {
                    suv.IsSirenActive = true;
                    _containmentVehicles.Add(suv);
                }

                for (int seatIdx = 0; seatIdx < 3; seatIdx++)
                {
                    PedHash model = seatIdx == 0 ? PedHash.FibSec01SMM : PedHash.Swat01SMY;
                    Vector3 agentSpawn = World.GetSafeCoordForPed(streetPos + new Vector3(seatIdx * 1.5f, 0f, 0f), true, 16);
                    if (agentSpawn == Vector3.Zero) agentSpawn = streetPos;
                    Ped agent = World.CreatePed(new Model(model), agentSpawn);
                    if (agent == null || !agent.Exists()) continue;

                    agent.RelationshipGroup = _containmentGroup;
                    agent.MaxHealth = 220;
                    agent.Health = 220;
                    agent.Armor = 100;
                    agent.Weapons.Give(WeaponHash.CarbineRifle, 300, true, true);
                    agent.Task.FightAgainst(playerPed);

                    Blip blip = agent.AddBlip();
                    if (blip != null && blip.Exists())
                    {
                        blip.Color = BlipColor.Red;
                        blip.Scale = 0.75f;
                        blip.Name = "Quantum Containment Agent";
                        _containmentBlips.Add(blip);
                    }

                    _containmentPeds.Add(agent);
                    if (suv != null && suv.Exists())
                    {
                        if (seatIdx == 0) agent.SetIntoVehicle(suv, VehicleSeat.Driver);
                        else if (seatIdx == 1) agent.SetIntoVehicle(suv, VehicleSeat.Passenger);
                        else agent.SetIntoVehicle(suv, VehicleSeat.LeftRear);
                    }
                }
            }

            Function.Call(Hash.FORCE_LIGHTNING_FLASH);
            GameplayCamera.Shake(CameraShake.MediumExplosion, 0.75f);
            ModLogger.Warn($"[CONTAINMENT_BREACH] Spawned vehicles={_containmentVehicles.Count} agents={_containmentPeds.Count} instab={_instabilityPercent:F0}%");
            Notification.Show("~r~CONTAINMENT BREACH (100% Instability)!\n~w~FIB/NOOSE Quantum Squads hunting you for 45s (or F8 Reset)!");
        }

        private void CleanupContainmentSquad(bool markNoLongerNeeded)
        {
            foreach (var blip in _containmentBlips)
            {
                if (blip != null && blip.Exists()) blip.Delete();
            }
            _containmentBlips.Clear();

            foreach (var agent in _containmentPeds)
            {
                if (agent != null && agent.Exists())
                {
                    if (markNoLongerNeeded) agent.MarkAsNoLongerNeeded();
                    else agent.Delete();
                }
            }
            _containmentPeds.Clear();

            foreach (var veh in _containmentVehicles)
            {
                if (veh != null && veh.Exists())
                {
                    if (markNoLongerNeeded) veh.MarkAsNoLongerNeeded();
                    else veh.Delete();
                }
            }
            _containmentVehicles.Clear();
        }

        private void UpdateInstabilityDecay(int now)
        {
            if (_lastDecayTime == 0)
            {
                _lastDecayTime = now;
                return;
            }
            float dt = (now - _lastDecayTime) / 1000f;
            if (dt < 0.25f) return;
            _lastDecayTime = now;

            if (_breachActive) return;

            float rate = _effectActive ? 0.55f : 2.0f;
            _instabilityPercent = Math.Max(0f, _instabilityPercent - rate * dt);
        }

        private void PanicNearbyWitnesses(Ped playerPed, Vector3 playerPos)
        {
            foreach (Ped witness in World.GetNearbyPeds(playerPos, 35f))
            {
                if (witness == null || !witness.Exists() || witness.IsPlayer || !witness.IsAlive || witness.IsRagdoll) continue;
                if (_containmentPeds.Contains(witness)) continue;
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

        private void ClearActiveEffects(bool notify, bool clearBreach)
        {
            _effectActive = false;
            _activeElixirTier = ElixirTier.None;
            _overdriveActive = false;
            _slowMoEndTime = 0;
            _stealthEndTime = 0;
            _overclockAmmoBuffEndTime = 0;
            if (_entangledTwin != null && _entangledTwin.Exists())
                _entangledTwin.MarkAsNoLongerNeeded();
            _entangledTwin = null;
            _activeModeShortName = "READY";
            _activeEffectLabel = "Stabilized (idle)";
            Game.TimeScale = 1.0f;

            var player = Game.Player;
            player.IsInvincible = false;
            Function.Call(Hash.ANIMPOSTFX_STOP_ALL);
            Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, player, 1.0f);
            Function.Call(Hash.SET_SWIM_MULTIPLIER_FOR_PLAYER, player, 1.0f);
            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, player, false);
            Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);
            Function.Call(Hash.SET_WEATHER_TYPE_NOW, "CLEAR");
            Function.Call(Hash.SET_CLOCK_TIME, 12, 0, 0);
            try { World.CurrentTimeOfDay = new TimeSpan(12, 0, 0); } catch { }

            if (player.Character != null && player.Character.Exists())
            {
                foreach (WeaponHash wh in _modifiedClipWeapons)
                {
                    if (player.Character.Weapons.HasWeapon(wh))
                        player.Character.Weapons[wh].InfiniteAmmoClip = false;
                }
                _modifiedClipWeapons.Clear();

                Weapon curWep = player.Character.Weapons.Current;
                if (curWep != null) curWep.InfiniteAmmoClip = false;

                foreach (Vehicle veh in World.GetNearbyVehicles(player.Character.Position, 85f))
                {
                    if (veh != null && veh.Exists())
                    {
                        Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, veh, false);
                        veh.IsAlarmSet = false;
                        veh.AreLightsOn = true;
                    }
                }
            }
            else
            {
                _modifiedClipWeapons.Clear();
            }

            foreach (Vehicle icyVeh in _icyVehicles.Values)
            {
                if (icyVeh != null && icyVeh.Exists())
                    Function.Call(Hash.SET_VEHICLE_REDUCE_GRIP, icyVeh, false);
            }
            _icyVehicles.Clear();

            RestoreStalledVehicles(int.MaxValue);

            if (clearBreach)
            {
                player.WantedLevel = 0;
                if (player.Character != null && player.Character.Exists() && player.Character.IsAlive)
                    player.Character.Health = player.Character.MaxHealth;

                if (_breachActive)
                {
                    CleanupContainmentSquad(markNoLongerNeeded: true);
                    _breachActive = false;
                }
                _instabilityPercent = Math.Max(0f, _instabilityPercent * 0.25f);
            }

            ModLogger.Info($"[STABILIZE] clearBreach={clearBreach} instab={_instabilityPercent:F0}%");
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

        private bool LoadOfflineTestMode()
        {
            try
            {
                string iniPath = Path.Combine(_scriptDirectory, "QuantumWeather.ini");
                if (!File.Exists(iniPath)) return false;
                foreach (var line in File.ReadAllLines(iniPath))
                {
                    if (line.Trim().Equals("OfflineTestMode=true", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private string LoadApiKey()
        {
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                string envKey = Environment.GetEnvironmentVariable("MOTH_API_KEY", target);
                if (!string.IsNullOrWhiteSpace(envKey))
                {
                    ModLogger.Info($"[AUTH] Loaded MOTH_API_KEY from EnvVar:{target} (redacted).");
                    return envKey.Trim();
                }
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
                                ModLogger.Info("[AUTH] Loaded API key from QuantumWeather.ini (redacted).");
                                return key;
                            }
                        }
                    }
                }
            }
            catch { }

            ModLogger.Warn("[AUTH] No MOTH_API_KEY or QuantumWeather.ini key found — mod will run in degraded FALLBACK mode.");
            return null;
        }
    }
}
