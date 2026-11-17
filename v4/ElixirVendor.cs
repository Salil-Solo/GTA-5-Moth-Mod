using System;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;

namespace QuantumRealityShiftV4
{
    /// <summary>
    /// Manages both on-demand (F10) and ambient street-roaming "Quantum Chemist" encounters:
    ///   - Periodically spawns on nearby sidewalks with a purple minimap blip.
    ///   - Approaches when the player gets close and opens a 3-tier LemonUI Elixir menu:
    ///       1. $10  - Mild Dose (60s trip + Shader Vision)
    ///       2. $100 - Unstable Brew (60s trip + Guaranteed [SURGE+TWIN] + stronger OTOC)
    ///       3. $500 - Singularity Overdose (45s infinite/explosive clip + Overdrive -> 4* Hangover)
    /// </summary>
    public class ElixirVendor
    {
        private enum VendorState
        {
            Idle,
            AmbientWaiting,
            Approaching,
            MenuOpen,
            WaitingAnimLoad,
            Drinking,
            Leaving
        }

        private readonly ObjectPool _pool = new ObjectPool();
        private readonly NativeMenu _menu;
        private readonly Action<string, int, ElixirTier> _onElixirConsumed;
        private readonly Random _rng = new Random();

        private VendorState _state = VendorState.Idle;
        private Ped _vendorPed = null;
        private Blip _vendorBlip = null;
        private int _stateEndTime = 0;
        private int _nextAmbientSpawnTime = 0;
        private ElixirTier _pendingTier = ElixirTier.MildDose;
        private int _pendingDurationMs = 60000;
        private string _pendingReason = "Elixir: Mild Dose (60s)";

        public bool IsMenuOpen => _menu.Visible;

        public ElixirVendor(Action<string, int, ElixirTier> onElixirConsumed)
        {
            _onElixirConsumed = onElixirConsumed;

            _menu = new NativeMenu("QUANTUM DEALER", "3-Tier Quantum Elixirs");
            _menu.Offset = new PointF(16f, 214f);

            var mildItem = new NativeItem(
                "Mild Dose ($10 · 60s)",
                "Standard 60s Quantum Reality Shift trip + Shader Vision overlay."
            );
            var unstableItem = new NativeItem(
                "Unstable Brew ($100 · 60s)",
                "Guaranteed [SURGE+TWIN] Entangled Car, wider shockwaves & +35% Instability."
            );
            var overdoseItem = new NativeItem(
                "Singularity Overdose ($500 · 45s)",
                "Quantum Overdrive + Infinite Clip & Explosive Ammo — ends with 4-Star Hangover!"
            );
            var declineItem = new NativeItem(
                "No thanks (Walk away)",
                "Tell the chemist to keep his glowing bottles."
            );

            mildItem.Activated += (s, e) => TryAcceptElixir(ElixirTier.MildDose, 10, 60000, "Elixir: Mild Dose");
            unstableItem.Activated += (s, e) => TryAcceptElixir(ElixirTier.UnstableBrew, 100, 60000, "Elixir: Unstable Brew");
            overdoseItem.Activated += (s, e) => TryAcceptElixir(ElixirTier.SingularityOverdose, 500, 45000, "Elixir: Overdose");
            declineItem.Activated += (s, e) => OnDeclineElixir();

            _menu.Add(mildItem);
            _menu.Add(unstableItem);
            _menu.Add(overdoseItem);
            _menu.Add(declineItem);
            _pool.Add(_menu);

            _nextAmbientSpawnTime = Game.GameTime + _rng.Next(65000, 105000);
        }

        public void SummonVendor()
        {
            Ped playerPed = Game.Player.Character;
            if (playerPed == null || !playerPed.Exists()) return;

            if (_state != VendorState.Idle)
                CleanupVendor();

            if (!SpawnChemistPed(playerPed, 7.5f, 18f))
            {
                Notification.Show("~r~Could not spawn Quantum Chemist nearby.");
                return;
            }

            _vendorPed.Task.GoTo(playerPed, Vector3.Zero, 8000);
            _state = VendorState.Approaching;
            _stateEndTime = Game.GameTime + 8000;
            Notification.Show("~p~A sketchy Quantum Chemist is approaching you...");
        }

        public void CloseMenu()
        {
            _menu.Visible = false;
        }

        public void Update(int now, bool allowAmbientSpawn)
        {
            _pool.Process();

            Ped playerPed = Game.Player.Character;
            if (playerPed == null || !playerPed.Exists() || !playerPed.IsAlive)
            {
                CleanupVendor();
                return;
            }

            // Ambient street-roaming chemist spawner
            if (_state == VendorState.Idle)
            {
                if (allowAmbientSpawn && now >= _nextAmbientSpawnTime)
                {
                    _nextAmbientSpawnTime = now + _rng.Next(95000, 145000);
                    if (!playerPed.IsInVehicle() && SpawnChemistPed(playerPed, 22f, 28f))
                    {
                        _vendorPed.Task.WanderAround(_vendorPed.Position, 6f);
                        _state = VendorState.AmbientWaiting;
                        _stateEndTime = now + 75000;
                        ModLogger.Info("[CHEMIST] Ambient street chemist spawned nearby.");
                        Notification.Show("~p~A street Quantum Chemist is nearby ~w~(check purple blip).");
                    }
                }
                return;
            }

            if (_state == VendorState.AmbientWaiting)
            {
                if (_vendorPed == null || !_vendorPed.Exists() || !_vendorPed.IsAlive ||
                    now >= _stateEndTime || _vendorPed.Position.DistanceTo(playerPed.Position) > 120f)
                {
                    CleanupVendor();
                    return;
                }

                float dist = _vendorPed.Position.DistanceTo(playerPed.Position);
                if (dist <= 6.5f)
                {
                    _vendorPed.Task.GoTo(playerPed, Vector3.Zero, 5000);
                    _state = VendorState.Approaching;
                    _stateEndTime = now + 5000;
                    GTA.UI.Screen.ShowSubtitle("~p~Chemist: ~w~\"Hey! Want to bend spacetime for a few bucks?\"", 4000);
                }
            }
            else if (_state == VendorState.Approaching)
            {
                if (_vendorPed == null || !_vendorPed.Exists() || !_vendorPed.IsAlive)
                {
                    CleanupVendor();
                    return;
                }

                float dist = _vendorPed.Position.DistanceTo(playerPed.Position);
                if (dist <= 2.9f || now >= _stateEndTime)
                {
                    _vendorPed.Task.TurnTo(playerPed, 2000);
                    _state = VendorState.MenuOpen;
                    _menu.Visible = true;
                    GTA.UI.Screen.ShowSubtitle("~p~Chemist: ~w~\"Pick your dose carefully. No refunds across timelines.\"", 6000);
                }
            }
            else if (_state == VendorState.MenuOpen)
            {
                if (!_menu.Visible)
                {
                    OnDeclineElixir();
                }
            }
            else if (_state == VendorState.WaitingAnimLoad)
            {
                bool loaded = Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, "mp_player_intdrink");
                if (loaded || now >= _stateEndTime)
                {
                    if (loaded)
                    {
                        playerPed.Task.PlayAnimation(
                            "mp_player_intdrink",
                            "loop_bottle",
                            8.0f,
                            2800,
                            AnimationFlags.UpperBodyOnly | AnimationFlags.Secondary
                        );
                    }
                    Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 3000, false);
                    _state = VendorState.Drinking;
                    _stateEndTime = now + 2800;
                }
            }
            else if (_state == VendorState.Drinking)
            {
                if (now >= _stateEndTime)
                {
                    ElixirTier tier = _pendingTier;
                    int dur = _pendingDurationMs;
                    string reason = _pendingReason;
                    CleanupVendor();
                    _onElixirConsumed?.Invoke(reason, dur, tier);
                }
            }
            else if (_state == VendorState.Leaving)
            {
                if (now >= _stateEndTime)
                {
                    CleanupVendor();
                }
            }
        }

        private bool SpawnChemistPed(Ped playerPed, float minDist, float maxDist)
        {
            float dist = (float)(minDist + _rng.NextDouble() * (maxDist - minDist));
            Vector3 desired = minDist < 10f
                ? playerPed.Position + playerPed.ForwardVector * dist
                : playerPed.Position + new Vector3(
                    (float)((_rng.NextDouble() * 2.0 - 1.0) * dist),
                    (float)((_rng.NextDouble() * 2.0 - 1.0) * dist),
                    0f);

            Vector3 spawnPos = World.GetSafeCoordForPed(desired, true, 16);
            if (spawnPos == Vector3.Zero)
                spawnPos = desired;

            _vendorPed = World.CreatePed(new Model(PedHash.Scientist01SMM), spawnPos, playerPed.Heading + 180f);
            if (_vendorPed == null || !_vendorPed.Exists())
                return false;

            _vendorBlip = _vendorPed.AddBlip();
            if (_vendorBlip != null && _vendorBlip.Exists())
            {
                _vendorBlip.Color = BlipColor.Purple;
                _vendorBlip.Scale = 0.85f;
                _vendorBlip.Name = "Quantum Chemist";
            }

            Function.Call(Hash.REQUEST_ANIM_DICT, "mp_player_intdrink");
            return true;
        }

        private void TryAcceptElixir(ElixirTier tier, int price, int durationMs, string reason)
        {
            if (Game.Player.Money < price)
            {
                Notification.Show($"~r~Not enough cash (~y~${price}~r~ needed for {reason}).");
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "ERROR", "HUD_FRONTEND_DEFAULT_SOUNDSET", true);
                return;
            }

            Game.Player.Money -= price;
            ModLogger.Info($"[CHEMIST] Purchased tier={tier} price=${price} durationMs={durationMs}");

            _pendingTier = tier;
            _pendingDurationMs = durationMs;
            _pendingReason = reason;
            _menu.Visible = false;

            GTA.UI.Screen.ShowSubtitle(
                tier == ElixirTier.SingularityOverdose
                    ? "~r~Chemist: ~w~\"Whoa—that was 10x concentration! Good luck!!\""
                    : "~p~Chemist: ~w~\"Enjoy the superposition, kid...\"",
                3200
            );

            if (_vendorPed != null && _vendorPed.Exists())
                _vendorPed.Task.WanderAround();

            if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, "mp_player_intdrink"))
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, "mp_player_intdrink");
                _state = VendorState.WaitingAnimLoad;
                _stateEndTime = Game.GameTime + 650;
                return;
            }

            Ped playerPed = Game.Player.Character;
            if (playerPed != null && playerPed.Exists())
            {
                playerPed.Task.PlayAnimation(
                    "mp_player_intdrink",
                    "loop_bottle",
                    8.0f,
                    2800,
                    AnimationFlags.UpperBodyOnly | AnimationFlags.Secondary
                );
            }

            Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 3000, false);
            _state = VendorState.Drinking;
            _stateEndTime = Game.GameTime + 2800;
        }

        private void OnDeclineElixir()
        {
            _menu.Visible = false;
            GTA.UI.Screen.ShowSubtitle("~p~Chemist: ~w~\"Your loss. Stay deterministic.\"", 3000);

            if (_vendorPed != null && _vendorPed.Exists())
                _vendorPed.Task.WanderAround();

            _state = VendorState.Leaving;
            _stateEndTime = Game.GameTime + 5000;
        }

        public void CleanupVendor()
        {
            _menu.Visible = false;
            if (_vendorBlip != null && _vendorBlip.Exists())
            {
                _vendorBlip.Delete();
                _vendorBlip = null;
            }
            if (_vendorPed != null && _vendorPed.Exists())
            {
                _vendorPed.MarkAsNoLongerNeeded();
                _vendorPed = null;
            }
            _state = VendorState.Idle;
            _nextAmbientSpawnTime = Game.GameTime + _rng.Next(95000, 145000);
        }
    }
}
