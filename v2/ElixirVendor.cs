using System;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;

namespace QuantumRealityShiftV2
{
    /// <summary>
    /// Manages the "Quantum Elixir" street encounter (F10):
    ///   1. Spawns a sketchy lab-coat scientist NPC ahead of the player.
    ///   2. Scientist walks up to the player and opens a LemonUI choice menu.
    ///   3. Accepting deducts $10, plays GTA V's built-in drinking animation,
    ///      and fires a 60-second Quantum Reality Shift trip via Moth Atlas.
    /// </summary>
    public class ElixirVendor
    {
        private enum VendorState
        {
            Idle,
            Approaching,
            MenuOpen,
            Drinking,
            Leaving
        }

        private const int ElixirPrice = 10;
        private readonly ObjectPool _pool = new ObjectPool();
        private readonly NativeMenu _menu;
        private readonly Action<string, int> _onElixirConsumed;

        private VendorState _state = VendorState.Idle;
        private Ped _vendorPed = null;
        private int _stateEndTime = 0;

        public ElixirVendor(Action<string, int> onElixirConsumed)
        {
            _onElixirConsumed = onElixirConsumed;

            _menu = new NativeMenu("QUANTUM DEALER", "Sketchy Street Chemist");
            _menu.Offset = new PointF(16f, 200f); // Sit cleanly below the F9 top Telemetry HUD
            var drinkItem = new NativeItem(
                $"Drink Quantum Elixir (${ElixirPrice})",
                "\"Pure superposition juice. Cures bad luck... mostly.\" Triggers a 60s Quantum Reality Shift."
            );
            var declineItem = new NativeItem(
                "No thanks (Walk away)",
                "Tell the chemist to keep his glowing bottle."
            );

            drinkItem.Activated += (s, e) => OnAcceptElixir();
            declineItem.Activated += (s, e) => OnDeclineElixir();

            _menu.Add(drinkItem);
            _menu.Add(declineItem);
            _pool.Add(_menu);
        }

        public void SummonVendor()
        {
            Ped playerPed = Game.Player.Character;
            if (playerPed == null || !playerPed.Exists()) return;

            if (_state != VendorState.Idle)
            {
                CleanupVendor();
            }

            Vector3 forward = playerPed.ForwardVector;
            Vector3 desired = playerPed.Position + forward * 7.5f;
            Vector3 spawnPos = World.GetSafeCoordForPed(desired, true, 16);
            if (spawnPos == Vector3.Zero)
                spawnPos = desired;

            _vendorPed = World.CreatePed(new Model(PedHash.Scientist01SMM), spawnPos, playerPed.Heading + 180f);
            if (_vendorPed == null || !_vendorPed.Exists())
            {
                Notification.Show("~r~Could not spawn Quantum Dealer nearby.");
                return;
            }

            // Preload built-in drinking anim dictionary while vendor walks up
            Function.Call(Hash.REQUEST_ANIM_DICT, "mp_player_intdrink");

            _vendorPed.Task.GoTo(playerPed, Vector3.Zero, 8000);
            _state = VendorState.Approaching;
            _stateEndTime = Game.GameTime + 8000;

            Notification.Show("~p~A sketchy Quantum Chemist is approaching you...");
        }

        public void Update(int now)
        {
            _pool.Process();

            if (_state == VendorState.Idle) return;

            Ped playerPed = Game.Player.Character;
            if (playerPed == null || !playerPed.Exists() || !playerPed.IsAlive)
            {
                CleanupVendor();
                return;
            }

            if (_state == VendorState.Approaching)
            {
                if (_vendorPed == null || !_vendorPed.Exists() || !_vendorPed.IsAlive)
                {
                    CleanupVendor();
                    return;
                }

                float dist = _vendorPed.Position.DistanceTo(playerPed.Position);
                if (dist <= 2.8f || now >= _stateEndTime)
                {
                    _vendorPed.Task.TurnTo(playerPed, 2000);
                    _state = VendorState.MenuOpen;
                    _menu.Visible = true;
                    GTA.UI.Screen.ShowSubtitle("~p~Dealer: ~w~\"Hey! Pure quantum medicine. Ten bucks. Wanna try?\"", 6000);
                }
            }
            else if (_state == VendorState.MenuOpen)
            {
                if (!_menu.Visible)
                {
                    // Closed menu with Backspace/Esc
                    OnDeclineElixir();
                }
            }
            else if (_state == VendorState.Drinking)
            {
                if (now >= _stateEndTime)
                {
                    _state = VendorState.Idle;
                    CleanupVendor();
                    _onElixirConsumed?.Invoke("Quantum Elixir (60s Trip)", 60000);
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

        private void OnAcceptElixir()
        {
            _menu.Visible = false;

            if (Game.Player.Money >= ElixirPrice)
                Game.Player.Money -= ElixirPrice;

            Ped playerPed = Game.Player.Character;
            if (playerPed != null && playerPed.Exists())
            {
                if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, "mp_player_intdrink"))
                {
                    Function.Call(Hash.REQUEST_ANIM_DICT, "mp_player_intdrink");
                    int waitUntil = Game.GameTime + 600;
                    while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, "mp_player_intdrink") && Game.GameTime < waitUntil)
                        Script.Wait(20);
                }

                // Built-in GTA V drinking animation — zero custom animation files needed
                if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, "mp_player_intdrink"))
                {
                    playerPed.Task.PlayAnimation(
                        "mp_player_intdrink",
                        "loop_bottle",
                        8.0f,
                        2800,
                        AnimationFlags.UpperBodyOnly | AnimationFlags.Secondary
                    );
                }
            }

            Function.Call(Hash.ANIMPOSTFX_PLAY, "DrugsMichaelAliensFight", 3000, false);
            GTA.UI.Screen.ShowSubtitle("~p~Dealer: ~w~\"Enjoy the superposition, kid...\"", 3000);

            if (_vendorPed != null && _vendorPed.Exists())
            {
                _vendorPed.Task.WanderAround();
            }

            _state = VendorState.Drinking;
            _stateEndTime = Game.GameTime + 2800;
        }

        private void OnDeclineElixir()
        {
            _menu.Visible = false;
            GTA.UI.Screen.ShowSubtitle("~p~Dealer: ~w~\"Your loss. Stay deterministic.\"", 3000);

            if (_vendorPed != null && _vendorPed.Exists())
            {
                _vendorPed.Task.WanderAround();
            }

            _state = VendorState.Leaving;
            _stateEndTime = Game.GameTime + 5000;
        }

        public void CleanupVendor()
        {
            _menu.Visible = false;
            if (_vendorPed != null && _vendorPed.Exists())
            {
                _vendorPed.MarkAsNoLongerNeeded();
                _vendorPed = null;
            }
            _state = VendorState.Idle;
        }
    }
}
