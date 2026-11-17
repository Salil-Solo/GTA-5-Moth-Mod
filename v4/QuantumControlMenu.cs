using System;
using System.Drawing;
using LemonUI;
using LemonUI.Menus;

namespace QuantumRealityShiftV4
{
    /// <summary>
    /// F5 Control Menu (LemonUI):
    /// Exposes collapse trigger, Launch Linked Car, Elixir Chemist summon,
    /// Atlas Prior Bias, 3-state HUD selector, and Stabilize.
    /// </summary>
    public class QuantumControlMenu
    {
        private readonly ObjectPool _pool = new ObjectPool();
        private readonly NativeMenu _menu;
        private readonly NativeCheckboxItem _autoItem;
        private readonly NativeCheckboxItem _shaderOverlayItem;
        private readonly NativeListItem<string> _biasItem;
        private readonly NativeListItem<string> _hudModeItem;

        public QuantumBiasMode BiasMode { get; private set; } = QuantumBiasMode.AutoChaos;
        public bool IsOpen => _menu.Visible;

        public QuantumControlMenu(
            bool initialAuto,
            HudDisplayMode initialHudMode,
            bool initialShaderOverlay,
            Action onTriggerCollapse,
            Action onLaunchTwin,
            Action onSummonVendor,
            Action onStabilize,
            Action<bool> onAutoChanged,
            Action<HudDisplayMode> onHudModeChanged,
            Action<bool> onShaderOverlayChanged)
        {
            _menu = new NativeMenu("QUANTUM REALITY V4", "Moth Atlas Gameplay Panel");
            _menu.Offset = new PointF(16f, 214f);

            var collapseNow = new NativeItem(
                "Trigger Quantum Collapse (F6)",
                "Charges reality and runs 4 Moth Atlas engines in parallel. Chaining while active builds Instability!"
            );
            collapseNow.Activated += (s, e) => onTriggerCollapse?.Invoke();

            var launchTwinItem = new NativeItem(
                "Launch Linked Car (X / Horn)",
                "Catapults your [TWIN] linked car toward your aim target or closest pursuing cop."
            );
            launchTwinItem.Activated += (s, e) =>
            {
                _menu.Visible = false;
                onLaunchTwin?.Invoke();
            };

            var summonVendor = new NativeItem(
                "Summon Quantum Chemist (F10)",
                "Spawns the street chemist with 3-tier Elixirs ($10 / $100 / $500)."
            );
            summonVendor.Activated += (s, e) =>
            {
                _menu.Visible = false;
                onSummonVendor?.Invoke();
            };

            _biasItem = new NativeListItem<string>(
                "Atlas Prior Bias",
                "Sets the preferred starting odds for your next F6 / Collapse (still uses real Atlas quantum math).",
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

            _hudModeItem = new NativeListItem<string>(
                "HUD Mode (F9)",
                "Cycle Compact Player HUD, Full Atlas Telemetry + Heatmap, or Off.",
                new[]
                {
                    "Compact Player HUD",
                    "Full Atlas Telemetry",
                    "Off"
                }
            );
            _hudModeItem.SelectedIndex = (int)initialHudMode;
            _hudModeItem.ItemChanged += (s, e) =>
            {
                onHudModeChanged?.Invoke((HudDisplayMode)_hudModeItem.SelectedIndex);
            };

            _autoItem = new NativeCheckboxItem(
                "Auto-Collapse on Wanted (F7)",
                "Automatically triggers Atlas collapse when Wanted Level rises.",
                initialAuto
            );
            _autoItem.CheckboxChanged += (s, e) => onAutoChanged?.Invoke(_autoItem.Checked);

            _shaderOverlayItem = new NativeCheckboxItem(
                "Shader Vision Overlay",
                "Renders entanglement-shader-v1 interference overlay on Elixir trips & OTOC beats.",
                initialShaderOverlay
            );
            _shaderOverlayItem.CheckboxChanged += (s, e) => onShaderOverlayChanged?.Invoke(_shaderOverlayItem.Checked);

            var stabilize = new NativeItem(
                "Stabilize Reality (F8)",
                "Clears active modifiers, Containment Breach squads, and lowers Reality Instability."
            );
            stabilize.Activated += (s, e) => onStabilize?.Invoke();

            _menu.Add(collapseNow);
            _menu.Add(launchTwinItem);
            _menu.Add(summonVendor);
            _menu.Add(_biasItem);
            _menu.Add(_hudModeItem);
            _menu.Add(_autoItem);
            _menu.Add(_shaderOverlayItem);
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

        public void SyncToggles(bool autoEnabled, HudDisplayMode hudMode, bool shaderOverlayEnabled)
        {
            _autoItem.Checked = autoEnabled;
            _hudModeItem.SelectedIndex = (int)hudMode;
            _shaderOverlayItem.Checked = shaderOverlayEnabled;
        }

        public void Update()
        {
            _pool.Process();
        }
    }
}
