using System;
using System.Drawing;
using LemonUI;
using LemonUI.Menus;

namespace QuantumRealityShiftV2
{
    /// <summary>
    /// F5 Control Menu (LemonUI):
    /// Lets you trigger collapses, summon the Elixir vendor, toggle HUD/Auto mode,
    /// and bias the prior probability vector sent to Moth Atlas blur-core-v1.
    /// </summary>
    public class QuantumControlMenu
    {
        private readonly ObjectPool _pool = new ObjectPool();
        private readonly NativeMenu _menu;
        private readonly NativeCheckboxItem _autoItem;
        private readonly NativeCheckboxItem _hudItem;
        private readonly NativeListItem<string> _biasItem;

        public QuantumBiasMode BiasMode { get; private set; } = QuantumBiasMode.AutoChaos;

        public QuantumControlMenu(
            bool initialAuto,
            bool initialHud,
            Action onTriggerCollapse,
            Action onSummonVendor,
            Action onStabilize,
            Action<bool> onAutoChanged,
            Action<bool> onHudChanged)
        {
            _menu = new NativeMenu("QUANTUM REALITY V2", "Moth Atlas Control Panel");
            _menu.Offset = new PointF(16f, 200f);

            var collapseNow = new NativeItem(
                "Trigger Quantum Collapse",
                "Sends current city chaos + 4x4 grid to Moth Atlas (same as F6)."
            );
            collapseNow.Activated += (s, e) => onTriggerCollapse?.Invoke();

            var summonVendor = new NativeItem(
                "Summon Quantum Elixir Dealer",
                "Spawns the sketchy chemist ahead of you (same as F10)."
            );
            summonVendor.Activated += (s, e) =>
            {
                _menu.Visible = false;
                onSummonVendor?.Invoke();
            };

            _biasItem = new NativeListItem<string>(
                "Atlas Prior Bias",
                "Weights sent to blur-core-v1 (still runs real Atlas quantum interference).",
                new[]
                {
                    "Auto (City Chaos)",
                    "Favor Overclock",
                    "Favor Stealth",
                    "Favor Storm",
                    "Favor Singularity"
                }
            );
            _biasItem.ItemChanged += (s, e) =>
            {
                BiasMode = (QuantumBiasMode)_biasItem.SelectedIndex;
            };

            _autoItem = new NativeCheckboxItem(
                "Auto-Collapse on Wanted",
                "Automatically triggers Atlas collapse when Wanted Level rises (F7).",
                initialAuto
            );
            _autoItem.CheckboxChanged += (s, e) => onAutoChanged?.Invoke(_autoItem.Checked);

            _hudItem = new NativeCheckboxItem(
                "Telemetry HUD + 4x4 Heatmap",
                "Shows live Atlas requests, job IDs, and 4x4 interference grids (F9).",
                initialHud
            );
            _hudItem.CheckboxChanged += (s, e) => onHudChanged?.Invoke(_hudItem.Checked);

            var stabilize = new NativeItem(
                "Stabilize / Reset Reality",
                "Clears active physics, slow-mo, sprint, and weather modifiers (F8)."
            );
            stabilize.Activated += (s, e) => onStabilize?.Invoke();

            _menu.Add(collapseNow);
            _menu.Add(summonVendor);
            _menu.Add(_biasItem);
            _menu.Add(_autoItem);
            _menu.Add(_hudItem);
            _menu.Add(stabilize);

            _pool.Add(_menu);
        }

        public void Toggle()
        {
            _menu.Visible = !_menu.Visible;
        }

        public void Close()
        {
            _menu.Visible = false;
        }

        public void SyncToggles(bool autoEnabled, bool hudEnabled)
        {
            _autoItem.Checked = autoEnabled;
            _hudItem.Checked = hudEnabled;
        }

        public void Update()
        {
            _pool.Process();
        }
    }
}
