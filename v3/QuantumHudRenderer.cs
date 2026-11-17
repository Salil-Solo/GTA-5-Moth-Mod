using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.UI;
using Font = GTA.UI.Font;

namespace QuantumRealityShiftV3
{
    /// <summary>
    /// Renders all V3 visual elements:
    ///   1. Top-left Moth Atlas 4-Engine Telemetry Log (F9 toggle)
    ///   2. Top-right Live 4x4 Heatmap + OTOC Echo 8-Beat Waveform + Entanglement Shader LUT Thumbnail
    ///   3. Translucent Shader Vision Screen Overlay during F10 Elixir trips & OTOC Echo beats
    ///   4. 3D Glowing Quantum Strike Pillars & Ground Rings (colored by constructive vs. inverted OTOC echo)
    ///   5. 3D Floating Sci-Fi Target Tags over affected cars/NPCs & Entangled Twin Vehicle
    /// </summary>
    public class QuantumHudRenderer
    {
        private readonly string[] _weatherTypes;

        private string _cachedShaderPath = null;
        private CustomSprite _hudShaderSprite = null;
        private CustomSprite _overlayShaderSprite = null;

        public QuantumHudRenderer(string[] weatherTypes)
        {
            _weatherTypes = weatherTypes;
        }

        public void DrawShaderVisionOverlay(
            TelemetrySnapshot t,
            bool effectActive,
            bool isElixirTrip,
            bool shaderOverlayEnabled,
            int now,
            int lastOtocBeatTime,
            double lastOtocValue)
        {
            if (!effectActive || !shaderOverlayEnabled || t == null || string.IsNullOrEmpty(t.ShaderPngPath))
                return;

            bool beatFlash = (now - lastOtocBeatTime) >= 0 && (now - lastOtocBeatTime) <= 550;
            if (!isElixirTrip && !beatFlash)
                return;

            EnsureShaderSprites(t.ShaderPngPath);
            if (_overlayShaderSprite == null)
                return;

            int alpha = isElixirTrip ? (beatFlash ? 50 : 32) : 26;
            Color tint = lastOtocValue >= 0
                ? Color.FromArgb(alpha, 130, 235, 255)
                : Color.FromArgb(alpha, 255, 110, 235);

            _overlayShaderSprite.Size = new SizeF(Screen.ScaledWidth, 720f);
            _overlayShaderSprite.Position = new PointF(0f, 0f);
            _overlayShaderSprite.Color = tint;
            _overlayShaderSprite.ScaledDraw();
        }

        public void DrawTelemetryAndHeatmap(
            int now,
            bool isFetching,
            string lastFetchReason,
            bool autoCollapseEnabled,
            bool effectActive,
            int effectEndTime,
            string activeEffectLabel,
            TelemetrySnapshot t)
        {
            // ── Left Panel: 4-Engine Telemetry Log ───────────────────────────
            var box = new ContainerElement(
                new PointF(16f, 12f),
                new SizeF(885f, 196f),
                Color.FromArgb(192, 10, 14, 28)
            );

            string autoTag = autoCollapseEnabled ? "ON" : "OFF";
            string remainTag = effectActive
                ? $"{Math.Max(0, (effectEndTime - now) / 1000)}s left"
                : "Inactive";

            var lines = new List<(string text, Color color)>();

            if (isFetching)
            {
                lines.Add(($"MOTH ATLAS V3 [F9 Hide] | SENDING 4-ENGINE REQUEST ({lastFetchReason})...", Color.FromArgb(255, 255, 210, 90)));
                lines.Add(("POST /engines/blur-core-v1/process (1D Weather + 2D 4x4 Entity Grid)", Color.FromArgb(255, 165, 225, 255)));
                lines.Add(("POST /engines/coin-toss-v1/process + /engines/otoc-echo-v1/process (depth=8)", Color.FromArgb(255, 165, 225, 255)));
                lines.Add(("POST /engines/entanglement-shader-v1/process -> R_lut/T_lut ZIP -> PNG", Color.FromArgb(255, 220, 180, 255)));
                lines.Add(("Polling GET /jobs/{id}/status async on background thread (60 FPS unblocked)...", Color.FromArgb(255, 200, 205, 220)));
            }
            else if (t == null)
            {
                lines.Add(($"QUANTUM REALITY SHIFT V3 [F9 Hide] | Auto[F7]: {autoTag} | Ready", Color.FromArgb(255, 195, 170, 255)));
                lines.Add(("F5:Menu | F6:Collapse | F7:Auto | F8:Reset | F10:Quantum Elixir Vendor", Color.FromArgb(255, 220, 230, 240)));
                lines.Add(("Engines: blur-core-v1 + coin-toss-v1 + otoc-echo-v1 + entanglement-shader-v1", Color.FromArgb(255, 150, 220, 255)));
                lines.Add(("OTOC drives 8 timed aftershocks; Entanglement Shader drives live HUD & Elixir vision.", Color.FromArgb(255, 150, 245, 190)));
            }
            else
            {
                Color statusColor = t.SourceLabel.StartsWith("LIVE")
                    ? Color.FromArgb(255, 100, 255, 165)
                    : Color.FromArgb(255, 255, 185, 90);

                double activeF = (t.OtocEchoSteps != null && t.ActiveOtocStep >= 0 && t.ActiveOtocStep < t.OtocEchoSteps.Length)
                    ? t.OtocEchoSteps[t.ActiveOtocStep]
                    : 0.0;
                string echoMode = activeF >= 0 ? "IN-PHASE" : "SCRAMBLED";

                lines.Add(($"ATLAS V3 [F9] | {t.SourceLabel} ({t.LatencySec:F2}s) | {t.TriggerReason} | {remainTag}", statusColor));
                lines.Add(($"INPUT ({t.BiasLabel}): Chaos={t.Chaos.Total:F2} (Stars:{t.Chaos.WantedStars} Peds:{t.Chaos.NearbyPeds} Cars:{t.Chaos.NearbyVehicles}) Str={t.BlurStrength:F2}", Color.FromArgb(255, 235, 235, 245)));
                lines.Add(($"BLUR 1D [{t.WeatherJobId}]: [{FormatArray(t.SentWeatherPriors)}] -> #{t.ChosenWeatherIndex} {_weatherTypes[t.ChosenWeatherIndex]}", Color.FromArgb(255, 145, 215, 255)));
                lines.Add(($"BLUR 2D [{t.SpatialJobId}, sum={t.GridDensitySum:F1}] -> Pillars: {FormatHotspots(t.Hotspots)}", Color.FromArgb(255, 255, 220, 130)));
                lines.Add(($"COIN [{t.CoinJobId}]: {t.CoinHeads}/{t.CoinShots} -> Flash={t.FlashMs}ms Surge={(t.Surge ? "ON (Twin)" : "OFF")}", Color.FromArgb(255, 165, 245, 220)));
                lines.Add(($"OTOC [{t.OtocJobId}]: Step {t.ActiveOtocStep + 1}/8 F_re={activeF:+0.00;-0.00;0.00} ({echoMode}) -> [{FormatArray(t.OtocEchoSteps)}]", Color.FromArgb(255, 255, 175, 245)));
                lines.Add(($"SHADER [{t.ShaderJobId}]: style={t.ShaderStyle} meanR={t.ShaderMeanR:F2} meanT={t.ShaderMeanT:F2}", Color.FromArgb(255, 150, 255, 235)));
                lines.Add(($"EFFECT: {activeEffectLabel} | F5:Menu F6:Collapse F8:Reset F10:Elixir", Color.FromArgb(255, 255, 255, 255)));
            }

            float y = 7f;
            foreach (var (text, color) in lines)
            {
                string safeText = text.Length > 98 ? text.Substring(0, 95) + "..." : text;
                box.Items.Add(new TextElement(
                    safeText,
                    new PointF(12f, y),
                    0.305f,
                    color,
                    Font.ChaletLondon,
                    Alignment.Left,
                    false,
                    true
                ));
                y += 22f;
            }

            float panelHeight = Math.Max(110f, y + 7f);
            box.Size = new SizeF(885f, panelHeight);
            box.ScaledDraw();

            // ── Right Panel: 4x4 Heatmap + OTOC Waveform + Shader LUT ────────
            DrawRightVisualizerPanel(t, panelHeight);
        }

        private void DrawRightVisualizerPanel(TelemetrySnapshot t, float leftPanelHeight)
        {
            const float panelX = 912f;
            const float panelY = 12f;
            const float panelWidth = 352f;
            float panelHeight = Math.Max(296f, leftPanelHeight);

            var heatBox = new ContainerElement(
                new PointF(panelX, panelY),
                new SizeF(panelWidth, panelHeight),
                Color.FromArgb(192, 10, 14, 28)
            );

            heatBox.Items.Add(new TextElement(
                "4x4 QUANTUM INTERFERENCE HEATMAP",
                new PointF(176f, 6f),
                0.29f,
                Color.FromArgb(255, 130, 235, 255),
                Font.ChaletLondon,
                Alignment.Center,
                false,
                true
            ));

            heatBox.Items.Add(new TextElement(
                "INPUT DENSITY",
                new PointF(88f, 26f),
                0.25f,
                Color.FromArgb(255, 180, 215, 255),
                Font.ChaletLondon,
                Alignment.Center,
                false,
                true
            ));

            heatBox.Items.Add(new TextElement(
                "ATLAS BLURRED",
                new PointF(264f, 26f),
                0.25f,
                Color.FromArgb(255, 240, 165, 255),
                Font.ChaletLondon,
                Alignment.Center,
                false,
                true
            ));

            double[][] input = t?.InputGrid;
            double[][] blurred = t?.BlurredGrid;
            double inputMax = MaxGridValue(input);
            double blurMax = MaxGridValue(blurred);

            var hotspotCells = new HashSet<(int r, int c)>();
            if (t?.Hotspots != null)
            {
                foreach (var h in t.Hotspots)
                    hotspotCells.Add((h.Row, h.Col));
            }

            const float cellSize = 25f;
            const float gap = 4f;
            const float startY = 44f;

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double inVal = (input != null && r < input.Length && input[r] != null && c < input[r].Length) ? input[r][c] : 0.05;
                    float inNorm = (float)Math.Max(0.0, Math.Min(1.0, inVal / Math.Max(0.25, inputMax)));
                    int inAlpha = 55 + (int)(inNorm * 195);
                    Color inColor = Color.FromArgb(inAlpha, 30, (int)(130 + inNorm * 125), 255);

                    heatBox.Items.Add(new ContainerElement(
                        new PointF(32f + c * (cellSize + gap), startY + r * (cellSize + gap)),
                        new SizeF(cellSize, cellSize),
                        inColor
                    ));

                    double outVal = (blurred != null && r < blurred.Length && blurred[r] != null && c < blurred[r].Length) ? blurred[r][c] : 0.05;
                    float outNorm = (float)Math.Max(0.0, Math.Min(1.0, outVal / Math.Max(0.25, blurMax)));
                    bool isHotspot = hotspotCells.Contains((r, c));

                    Color outColor = isHotspot
                        ? Color.FromArgb(245, 255, 70, 210)
                        : Color.FromArgb(55 + (int)(outNorm * 185), (int)(110 + outNorm * 135), 55, 225);

                    float cellX = 206f + c * (cellSize + gap);
                    float cellY = startY + r * (cellSize + gap);

                    if (isHotspot)
                    {
                        heatBox.Items.Add(new ContainerElement(
                            new PointF(cellX - 1.5f, cellY - 1.5f),
                            new SizeF(cellSize + 3f, cellSize + 3f),
                            Color.FromArgb(255, 255, 225, 80)
                        ));
                    }

                    heatBox.Items.Add(new ContainerElement(
                        new PointF(cellX, cellY),
                        new SizeF(cellSize, cellSize),
                        outColor
                    ));
                }
            }

            // ── Bottom-Left: OTOC Echo 8-Step Waveform ───────────────────────
            heatBox.Items.Add(new TextElement(
                "OTOC ECHO (t=1..8)",
                new PointF(110f, 168f),
                0.24f,
                Color.FromArgb(255, 255, 190, 245),
                Font.ChaletLondon,
                Alignment.Center,
                false,
                true
            ));

            // Baseline line across waveform
            const float waveX = 18f;
            const float zeroY = 236f;
            const float maxBarH = 42f;
            heatBox.Items.Add(new ContainerElement(
                new PointF(waveX, zeroY),
                new SizeF(186f, 1.5f),
                Color.FromArgb(140, 200, 220, 255)
            ));

            double[] steps = t?.OtocEchoSteps;
            int activeStep = t != null ? t.ActiveOtocStep : -1;
            for (int i = 0; i < 8; i++)
            {
                double val = (steps != null && i < steps.Length) ? steps[i] : 0.0;
                float barH = Math.Max(4f, (float)(Math.Min(1.0, Math.Abs(val)) * maxBarH));
                float bx = waveX + 4f + i * 22.5f;
                float by = val >= 0 ? (zeroY - barH) : (zeroY + 2f);
                bool isActive = i == activeStep;

                Color barCol = isActive
                    ? Color.FromArgb(255, 255, 230, 80)
                    : (val >= 0
                        ? Color.FromArgb(220, 60, 235, 255)
                        : Color.FromArgb(220, 255, 75, 205));

                if (isActive)
                {
                    heatBox.Items.Add(new ContainerElement(
                        new PointF(bx - 1.5f, by - 1.5f),
                        new SizeF(19f, barH + 3f),
                        Color.FromArgb(255, 255, 255, 255)
                    ));
                }

                heatBox.Items.Add(new ContainerElement(
                    new PointF(bx, by),
                    new SizeF(16f, barH),
                    barCol
                ));
            }

            // ── Bottom-Right: Live Entanglement Shader LUT Thumbnail ─────────
            heatBox.Items.Add(new TextElement(
                "SHADER LUT",
                new PointF(286f, 168f),
                0.24f,
                Color.FromArgb(255, 145, 255, 230),
                Font.ChaletLondon,
                Alignment.Center,
                false,
                true
            ));

            // Frame behind sprite
            heatBox.Items.Add(new ContainerElement(
                new PointF(236f, 188f),
                new SizeF(100f, 100f),
                Color.FromArgb(220, 20, 32, 54)
            ));

            heatBox.ScaledDraw();

            // Draw actual PNG sprite on top of frame
            if (t != null && !string.IsNullOrEmpty(t.ShaderPngPath))
            {
                EnsureShaderSprites(t.ShaderPngPath);
                if (_hudShaderSprite != null)
                {
                    _hudShaderSprite.Position = new PointF(panelX + 239f, panelY + 191f);
                    _hudShaderSprite.Size = new SizeF(94f, 94f);
                    _hudShaderSprite.Color = Color.White;
                    _hudShaderSprite.ScaledDraw();
                }
            }
        }

        public void DrawHotspotMarkers(
            Vector3 playerPos,
            List<HotspotTarget> hotspots,
            int weatherIndex,
            int now,
            int nextPulseTime,
            double activeEchoVal)
        {
            if (hotspots == null || hotspots.Count == 0) return;

            bool imminent = (nextPulseTime - now) <= 1100;
            bool scrambled = activeEchoVal < 0;

            Color beamColor = weatherIndex <= 1
                ? (imminent ? Color.FromArgb(165, 130, 255, 215) : Color.FromArgb(115, 80, 255, 175))
                : (imminent
                    ? (scrambled ? Color.FromArgb(195, 255, 55, 185) : Color.FromArgb(190, 95, 245, 255))
                    : Color.FromArgb(135, 45, 225, 255));

            Color ringColor = imminent
                ? Color.FromArgb(230, 255, 210, 65)
                : (scrambled ? Color.FromArgb(190, 255, 80, 220) : Color.FromArgb(175, 0, 240, 255));

            float ampBoost = (float)(0.85 + Math.Min(1.0, Math.Abs(activeEchoVal)) * 0.45);
            float pulseScale = (imminent ? 10.5f : 8.5f) * ampBoost;

            foreach (var h in hotspots)
            {
                float sx = playerPos.X + h.OffsetX;
                float sy = playerPos.Y + h.OffsetY;
                float groundZ = World.GetGroundHeight(new Vector3(sx, sy, playerPos.Z + 6f));
                if (groundZ <= -200f || Math.Abs(groundZ) <= 0.01f)
                    groundZ = World.GetGroundHeight(new Vector2(sx, sy));
                float sz = groundZ > -200f && Math.Abs(groundZ) > 0.01f ? groundZ : playerPos.Z;
                Vector3 spot = new Vector3(sx, sy, sz);

                World.DrawMarker(
                    MarkerType.VerticalCylinder,
                    spot,
                    Vector3.Zero,
                    Vector3.Zero,
                    new Vector3(2.2f, 2.2f, 18.0f),
                    beamColor
                );

                World.DrawMarker(
                    MarkerType.HorizontalCircleSkinny,
                    spot + new Vector3(0f, 0f, 0.12f),
                    Vector3.Zero,
                    Vector3.Zero,
                    new Vector3(pulseScale, pulseScale, 1.0f),
                    ringColor
                );
            }
        }

        public void DrawWorldEntityTagsAndTether(
            Ped playerPed,
            Vehicle entangledTwin,
            int weatherIndex,
            TelemetrySnapshot t)
        {
            Vector3 playerPos = playerPed.Position;
            string jobTag = t != null ? t.SpatialJobId : "sim";
            string coinTag = t != null ? t.CoinJobId : "sim";

            // 1. Entangled Twin tether line + bright tag
            if (entangledTwin != null && entangledTwin.Exists() && entangledTwin.IsAlive)
            {
                Vector3 from = (playerPed.CurrentVehicle != null && playerPed.CurrentVehicle.Exists())
                    ? playerPed.CurrentVehicle.Position + new Vector3(0f, 0f, 1.0f)
                    : playerPos + new Vector3(0f, 0f, 1.0f);
                Vector3 to = entangledTwin.Position + new Vector3(0f, 0f, 1.1f);

                World.DrawLine(from, to, Color.FromArgb(230, 255, 60, 220));

                if (entangledTwin.IsOnScreen && entangledTwin.Position.DistanceTo(playerPos) < 85f)
                {
                    DrawFloatingCard(
                        entangledTwin.Position + new Vector3(0f, 0f, 2.1f),
                        $"ENTANGLED TWIN [{coinTag}]",
                        $"SYNC: 1.35x | SPD: {entangledTwin.Speed:F1}m/s",
                        Color.FromArgb(255, 255, 95, 235)
                    );
                }
            }

            // 2. Floating sci-fi tags over up to 3 nearby affected vehicles
            if (weatherIndex <= 1) return;

            string statusText = weatherIndex >= 7
                ? "GRAVITY: INVERTED"
                : (weatherIndex >= 5 ? "TRACTION: 0% (ICY)" : "QUANTUM DRIFT");

            int drawnVeh = 0;
            foreach (Vehicle veh in World.GetNearbyVehicles(playerPos, 45f))
            {
                if (drawnVeh >= 3) break;
                if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == entangledTwin) continue;
                if (!veh.IsOnScreen) continue;

                float dist = veh.Position.DistanceTo(playerPos);
                DrawFloatingCard(
                    veh.Position + new Vector3(0f, 0f, 1.75f),
                    $"QUANTUM LOCK // blur-core [{jobTag}]",
                    $"{statusText} | {dist:F0}m",
                    Color.FromArgb(255, 80, 245, 255)
                );
                drawnVeh++;
            }

            // 3. Floating sci-fi tags over up to 2 nearby affected NPCs/cops
            string pedState = weatherIndex >= 7
                ? "ANTI-GRAV RISK"
                : (weatherIndex >= 5 ? "QUANTUM PANIC" : "DETECT: BLIND");

            int drawnPed = 0;
            foreach (Ped p in World.GetNearbyPeds(playerPos, 30f))
            {
                if (drawnPed >= 2) break;
                if (p == null || !p.Exists() || p.IsPlayer || !p.IsAlive || !p.IsOnScreen) continue;

                string tagState = p.IsRagdoll ? "COLLAPSED" : pedState;
                DrawFloatingCard(
                    p.Position + new Vector3(0f, 0f, 1.15f),
                    $"SUBJECT // blur-core [{jobTag}]",
                    $"{p.Health} HP | {tagState}",
                    Color.FromArgb(255, 130, 255, 195)
                );
                drawnPed++;
            }
        }

        private void EnsureShaderSprites(string pngPath)
        {
            if (string.Equals(_cachedShaderPath, pngPath, StringComparison.OrdinalIgnoreCase) &&
                _hudShaderSprite != null &&
                _overlayShaderSprite != null)
            {
                return;
            }

            _cachedShaderPath = pngPath;
            _hudShaderSprite = null;
            _overlayShaderSprite = null;

            try
            {
                if (!File.Exists(pngPath)) return;
                _hudShaderSprite = new CustomSprite(pngPath, new SizeF(94f, 94f), new PointF(0f, 0f));
                _overlayShaderSprite = new CustomSprite(pngPath, new SizeF(1280f, 720f), new PointF(0f, 0f));
            }
            catch
            {
                _hudShaderSprite = null;
                _overlayShaderSprite = null;
            }
        }

        private void DrawFloatingCard(Vector3 worldPos, string line1, string line2, Color accentColor)
        {
            PointF pt = Screen.WorldToScreen(worldPos, true);
            if (pt.IsEmpty || pt.X < 25f || pt.X > Screen.ScaledWidth - 225f || pt.Y < 218f || pt.Y > 650f)
                return;

            float cardX = Math.Max(8f, Math.Min(Screen.ScaledWidth - 203f, pt.X - 95f));
            var card = new ContainerElement(
                new PointF(cardX, pt.Y - 36f),
                new SizeF(195f, 38f),
                Color.FromArgb(180, 8, 22, 34)
            );

            card.Items.Add(new ContainerElement(
                new PointF(0f, 0f),
                new SizeF(195f, 2.5f),
                accentColor
            ));

            card.Items.Add(new TextElement(
                line1,
                new PointF(6f, 4f),
                0.24f,
                accentColor,
                Font.ChaletLondon,
                Alignment.Left,
                false,
                true
            ));

            card.Items.Add(new TextElement(
                line2,
                new PointF(6f, 19f),
                0.23f,
                Color.FromArgb(255, 230, 245, 255),
                Font.ChaletLondon,
                Alignment.Left,
                false,
                true
            ));

            card.ScaledDraw();
        }

        private double MaxGridValue(double[][] grid)
        {
            if (grid == null) return 1.0;
            double max = 0.0;
            foreach (var row in grid)
            {
                if (row == null) continue;
                foreach (var v in row)
                    if (v > max) max = v;
            }
            return max;
        }

        private string FormatArray(double[] values)
        {
            if (values == null || values.Length == 0) return "";
            return string.Join(",", values.Select(v => v.ToString("F2")));
        }

        private string FormatHotspots(List<HotspotTarget> hotspots)
        {
            if (hotspots == null || hotspots.Count == 0) return "none";
            return string.Join(" ", hotspots.Select(h => $"({h.OffsetX:+0;-0},{h.OffsetY:+0;-0})m"));
        }
    }
}
