using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.UI;
using Font = GTA.UI.Font;

namespace QuantumRealityShiftV4
{
    /// <summary>
    /// Renders all V4 visual elements:
    ///   1. Compact Player HUD (Default F9 mode) — mode, remaining bar, Instability %, 8 OTOC beat dots
    ///   2. Full Atlas Telemetry HUD (2nd F9 mode) — job IDs, 4x4 Heatmap, OTOC Waveform & Shader LUT
    ///   3. World-Anchored Street Rings (Green player buff vs. Chaos hazard zones)
    ///   4. Translucent Shader Vision Screen Overlay & Active Engine Banner
    ///   5. Floating Sci-Fi Target Cards over affected/stalled vehicles & Entangled Twin tether
    /// </summary>
    public class QuantumHudRenderer
    {
        private readonly string[] _weatherTypes;

        private string _cachedShaderPath = null;
        private CustomSprite _hudShaderSprite = null;
        private CustomSprite _overlayShaderSprite = null;
        private bool _isFullTelemetryFrame = false;

        public QuantumHudRenderer(string[] weatherTypes)
        {
            _weatherTypes = weatherTypes;
        }

        public void DrawShaderVisionOverlay(
            TelemetrySnapshot t,
            bool effectActive,
            bool isElixirTrip,
            bool overdriveActive,
            bool shaderOverlayEnabled,
            int now,
            int lastOtocBeatTime,
            double lastOtocValue)
        {
            if (!effectActive || !shaderOverlayEnabled || t == null || string.IsNullOrEmpty(t.ShaderPngPath))
                return;

            bool beatFlash = (now - lastOtocBeatTime) >= 0 && (now - lastOtocBeatTime) <= 550;
            if (!isElixirTrip && !overdriveActive && !beatFlash)
                return;

            EnsureShaderSprites(t.ShaderPngPath);
            if (_overlayShaderSprite == null)
                return;

            int alpha = isElixirTrip ? (beatFlash ? 68 : 44) : (overdriveActive ? 42 : 34);
            Color tint = lastOtocValue >= 0
                ? Color.FromArgb(alpha, 130, 235, 255)
                : Color.FromArgb(alpha, 255, 110, 235);

            _overlayShaderSprite.Size = new SizeF(Screen.ScaledWidth, 720f);
            _overlayShaderSprite.Position = new PointF(0f, 0f);
            _overlayShaderSprite.Color = tint;
            _overlayShaderSprite.ScaledDraw();
        }

        public void DrawEngineBanner(
            HudDisplayMode hudMode,
            int now,
            bool effectActive,
            int effectEndTime,
            string modeName,
            string bannerText,
            int bannerEndTime)
        {
            if (hudMode == HudDisplayMode.FullTelemetry)
                return;

            float nextY = hudMode == HudDisplayMode.Compact ? 102f : 14f;

            // Always show a slim countdown pill in Off mode while a shift is active
            if (hudMode == HudDisplayMode.Off && effectActive)
            {
                int remSec = Math.Max(0, (effectEndTime - now) / 1000);
                var timerPill = new ContainerElement(new PointF(16f, nextY), new SizeF(215f, 25f), Color.FromArgb(195, 12, 18, 36));
                timerPill.Items.Add(new ContainerElement(new PointF(0f, 0f), new SizeF(3.5f, 25f), Color.FromArgb(255, 255, 225, 110)));
                timerPill.Items.Add(new TextElement($"{modeName} · {remSec}s left", new PointF(10f, 4f), 0.27f, Color.FromArgb(255, 255, 230, 120), Font.ChaletLondon, Alignment.Left, false, true));
                timerPill.ScaledDraw();
                nextY += 30f;
            }

            if (string.IsNullOrEmpty(bannerText) || now >= bannerEndTime)
                return;

            var pill = new ContainerElement(new PointF(16f, nextY), new SizeF(440f, 24f), Color.FromArgb(195, 12, 18, 36));
            pill.Items.Add(new ContainerElement(new PointF(0f, 0f), new SizeF(3.5f, 24f), Color.FromArgb(255, 110, 245, 255)));
            pill.Items.Add(new TextElement(bannerText, new PointF(10f, 4f), 0.25f, Color.FromArgb(255, 165, 245, 255), Font.ChaletLondon, Alignment.Left, false, true));
            pill.ScaledDraw();
        }

        public void DrawHud(
            HudDisplayMode hudMode,
            int now,
            bool isFetching,
            string lastFetchReason,
            bool autoCollapseEnabled,
            bool effectActive,
            int effectStartTime,
            int effectEndTime,
            string activeModeShortName,
            string activeEffectLabel,
            float instabilityPercent,
            bool overdriveActive,
            bool breachActive,
            int breachEndTime,
            bool surgeActive,
            double[] otocSteps,
            int activeOtocStep,
            TelemetrySnapshot t)
        {
            if (hudMode == HudDisplayMode.Off) return;

            if (hudMode == HudDisplayMode.Compact)
            {
                bool isFullFallback = t != null && t.SourceLabel != null && t.SourceLabel.StartsWith("FALLBACK");
                DrawCompactPlayerHud(
                    now, isFetching, lastFetchReason, effectActive, effectStartTime, effectEndTime,
                    activeModeShortName, instabilityPercent, overdriveActive, breachActive, breachEndTime,
                    surgeActive, isFullFallback, otocSteps, activeOtocStep
                );
                return;
            }

            DrawTelemetryAndHeatmap(
                now, isFetching, lastFetchReason, autoCollapseEnabled,
                effectActive, effectEndTime, activeEffectLabel,
                instabilityPercent, overdriveActive, breachActive, t
            );
        }

        private void DrawCompactPlayerHud(
            int now,
            bool isFetching,
            string lastFetchReason,
            bool effectActive,
            int effectStartTime,
            int effectEndTime,
            string modeName,
            float instabilityPercent,
            bool overdriveActive,
            bool breachActive,
            int breachEndTime,
            bool surgeActive,
            bool isFullFallback,
            double[] otocSteps,
            int activeOtocStep)
        {
            const float x = 16f;
            const float y = 12f;
            const float w = 365f;
            const float h = 84f;

            var card = new ContainerElement(new PointF(x, y), new SizeF(w, h), Color.FromArgb(190, 10, 14, 28));

            Color accent = breachActive
                ? Color.FromArgb(255, 255, 70, 95)
                : (isFetching
                    ? Color.FromArgb(255, 255, 210, 85)
                    : (isFullFallback && effectActive
                        ? Color.FromArgb(255, 255, 175, 65)
                        : (overdriveActive ? Color.FromArgb(255, 255, 110, 235) : Color.FromArgb(255, 80, 235, 255))));

            card.Items.Add(new ContainerElement(new PointF(0f, 0f), new SizeF(w, 2.5f), accent));

            string titleLeft;
            string titleRight;
            if (isFetching)
            {
                titleLeft = $"CHARGING REALITY ({lastFetchReason})";
                titleRight = "ATLAS...";
            }
            else if (breachActive)
            {
                int breachSec = Math.Max(0, (breachEndTime - now) / 1000);
                titleLeft = "CONTAINMENT BREACH!";
                titleRight = $"{breachSec}s | F8 Reset";
            }
            else if (effectActive)
            {
                int remSec = Math.Max(0, (effectEndTime - now) / 1000);
                string badges = (isFullFallback ? " [NO KEY]" : "") + (overdriveActive ? " [OD]" : "") + (surgeActive ? " [TWIN]" : "");
                titleLeft = $"{modeName}{badges}";
                titleRight = $"{remSec}s left";
            }
            else
            {
                titleLeft = "QUANTUM V4 READY";
                titleRight = "F6:Shift F5:Menu";
            }

            Color rightCol = effectActive ? Color.FromArgb(255, 255, 230, 115) : Color.FromArgb(255, 230, 240, 255);
            card.Items.Add(new TextElement(titleLeft, new PointF(10f, 6f), 0.28f, accent, Font.ChaletLondon, Alignment.Left, false, true));
            card.Items.Add(new TextElement(titleRight, new PointF(w - 10f, 5f), effectActive ? 0.30f : 0.27f, rightCol, Font.ChaletLondon, Alignment.Right, false, true));

            // Duration progress bar
            float durFrac = 0f;
            if (effectActive && effectEndTime > effectStartTime)
                durFrac = Math.Max(0f, Math.Min(1f, (float)(effectEndTime - now) / (effectEndTime - effectStartTime)));
            else if (isFetching)
                durFrac = ((now % 900) / 900f);

            card.Items.Add(new ContainerElement(new PointF(10f, 27f), new SizeF(w - 20f, 4f), Color.FromArgb(140, 35, 45, 70)));
            if (durFrac > 0.01f)
            {
                card.Items.Add(new ContainerElement(new PointF(10f, 27f), new SizeF((w - 20f) * durFrac, 4f), accent));
            }

            // Instability bar
            float instClamped = Math.Max(0f, Math.Min(100f, instabilityPercent));
            Color instColor = instClamped >= 80f
                ? Color.FromArgb(255, 255, 70, 95)
                : (instClamped >= 50f ? Color.FromArgb(255, 255, 175, 70) : Color.FromArgb(255, 110, 225, 255));

            card.Items.Add(new TextElement($"INSTABILITY {instClamped:F0}%", new PointF(10f, 36f), 0.24f, instColor, Font.ChaletLondon, Alignment.Left, false, true));
            card.Items.Add(new ContainerElement(new PointF(112f, 41f), new SizeF(w - 122f, 6f), Color.FromArgb(140, 35, 45, 70)));
            if (instClamped > 0.5f)
            {
                card.Items.Add(new ContainerElement(new PointF(112f, 41f), new SizeF((w - 122f) * (instClamped / 100f), 6f), instColor));
            }

            // Bottom row: 8 OTOC Beat Dots + compact hint
            card.Items.Add(new TextElement("OTOC:", new PointF(10f, 57f), 0.24f, Color.FromArgb(255, 200, 185, 240), Font.ChaletLondon, Alignment.Left, false, true));
            for (int i = 0; i < 8; i++)
            {
                double v = (otocSteps != null && i < otocSteps.Length) ? otocSteps[i] : 0.0;
                bool active = effectActive && i == activeOtocStep;
                float dx = 54f + i * 16f;
                float dy = 60f;

                Color dotCol = active
                    ? Color.FromArgb(255, 255, 230, 80)
                    : (v >= 0 ? Color.FromArgb(220, 60, 235, 255) : Color.FromArgb(220, 255, 75, 205));

                if (active)
                {
                    card.Items.Add(new ContainerElement(new PointF(dx - 1.5f, dy - 1.5f), new SizeF(13f, 13f), Color.White));
                }
                card.Items.Add(new ContainerElement(new PointF(dx, dy), new SizeF(10f, 10f), dotCol));
            }

            card.Items.Add(new TextElement("F9:Telemetry | X:Twin", new PointF(w - 10f, 57f), 0.23f, Color.FromArgb(200, 200, 215, 235), Font.ChaletLondon, Alignment.Right, false, true));
            card.ScaledDraw();
        }

        private void DrawTelemetryAndHeatmap(
            int now,
            bool isFetching,
            string lastFetchReason,
            bool autoCollapseEnabled,
            bool effectActive,
            int effectEndTime,
            string activeEffectLabel,
            float instabilityPercent,
            bool overdriveActive,
            bool breachActive,
            TelemetrySnapshot t)
        {
            float leftWidth = Math.Max(620f, Math.Min(776f, Screen.ScaledWidth - 390f));
            var box = new ContainerElement(
                new PointF(16f, 12f),
                new SizeF(leftWidth, 196f),
                Color.FromArgb(192, 10, 14, 28)
            );

            string autoTag = autoCollapseEnabled ? "ON" : "OFF";
            string remainTag = effectActive
                ? $"{Math.Max(0, (effectEndTime - now) / 1000)}s"
                : "Idle";
            string instTag = $"Inst:{instabilityPercent:F0}%" + (overdriveActive ? " [OD]" : "") + (breachActive ? " [BREACH]" : "");

            var lines = new List<(string text, Color color)>();

            if (isFetching)
            {
                lines.Add(($"ATLAS V4 [F9] | CHARGING ({lastFetchReason}) | {instTag}", Color.FromArgb(255, 255, 210, 90)));
                lines.Add(("POST /engines/blur-core-v1 (1D Mode + 2D 4x4 Street Grid)", Color.FromArgb(255, 165, 225, 255)));
                lines.Add(("POST /engines/coin-toss-v1 + /engines/otoc-echo-v1 (depth=8)", Color.FromArgb(255, 165, 225, 255)));
                lines.Add(("POST /engines/entanglement-shader-v1 -> R_lut/T_lut ZIP -> PNG", Color.FromArgb(255, 220, 180, 255)));
                lines.Add(("Polling async on background thread (60 FPS unblocked)...", Color.FromArgb(255, 200, 205, 220)));
            }
            else if (t == null)
            {
                lines.Add(($"ATLAS V4 [F9] | Auto[F7]:{autoTag} | {instTag}", Color.FromArgb(255, 195, 170, 255)));
                lines.Add(("F5:Menu | F6:Collapse | F8:Reset | F9:HUD Mode | F10:Chemist | X:Throw Car", Color.FromArgb(255, 220, 230, 240)));
                lines.Add(("Engines: blur-core-v1 + coin-toss-v1 + otoc-echo-v1 + shader", Color.FromArgb(255, 150, 220, 255)));
                lines.Add(("Active mode grants Infinite Health + Super Jump + Linked Car Throw.", Color.FromArgb(255, 150, 245, 190)));
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

                lines.Add(($"ATLAS V4 [F9] | {t.SourceLabel} ({t.LatencySec:F2}s) | {remainTag} | {instTag}", statusColor));
                lines.Add(($"INPUT ({t.BiasLabel}): Chaos={t.Chaos.Total:F2} (Stars:{t.Chaos.WantedStars} Peds:{t.Chaos.NearbyPeds} Cars:{t.Chaos.NearbyVehicles}) Str={t.BlurStrength:F2}", Color.FromArgb(255, 235, 235, 245)));
                lines.Add(($"BLUR 1D [{t.WeatherJobId}]: [{FormatArray(t.SentWeatherPriors)}] -> #{t.ChosenWeatherIndex} {_weatherTypes[t.ChosenWeatherIndex]}", Color.FromArgb(255, 145, 215, 255)));
                lines.Add(($"BLUR 2D [{t.SpatialJobId}, sum={t.GridDensitySum:F1}] -> Zones: {FormatHotspots(t.Hotspots)}", Color.FromArgb(255, 255, 220, 130)));
                lines.Add(($"COIN [{t.CoinJobId}]: {t.CoinHeads}/{t.CoinShots} -> Flash={t.FlashMs}ms LinkedCar={(t.Surge ? "ON" : "OFF")}", Color.FromArgb(255, 165, 245, 220)));
                lines.Add(($"OTOC [{t.OtocJobId}]: Step {t.ActiveOtocStep + 1}/8 F_re={activeF:+0.00;-0.00;0.00} ({echoMode}) -> [{FormatArray(t.OtocEchoSteps)}]", Color.FromArgb(255, 255, 175, 245)));
                lines.Add(($"SHADER [{t.ShaderJobId}]: style={t.ShaderStyle} meanR={t.ShaderMeanR:F2} meanT={t.ShaderMeanT:F2}", Color.FromArgb(255, 150, 255, 235)));
                lines.Add(($"MODE: {activeEffectLabel} | F5:Menu F8:Reset X:Throw", Color.FromArgb(255, 255, 255, 255)));
            }

            float y = 7f;
            foreach (var (text, color) in lines)
            {
                string safeText = text.Length > 88 ? text.Substring(0, 85) + "..." : text;
                box.Items.Add(new TextElement(
                    safeText,
                    new PointF(10f, y),
                    0.285f,
                    color,
                    Font.ChaletLondon,
                    Alignment.Left,
                    false,
                    true
                ));
                y += 22f;
            }

            float panelHeight = Math.Max(110f, y + 7f);
            box.Size = new SizeF(leftWidth, panelHeight);
            box.ScaledDraw();

            DrawRightVisualizerPanel(t, panelHeight, leftWidth);
        }

        private void DrawRightVisualizerPanel(TelemetrySnapshot t, float leftPanelHeight, float leftWidth)
        {
            const float panelWidth = 340f;
            float panelX = Math.Min(16f + leftWidth + 10f, Screen.ScaledWidth - panelWidth - 44f);
            const float panelY = 12f;
            float panelHeight = Math.Max(292f, leftPanelHeight);

            var heatBox = new ContainerElement(
                new PointF(panelX, panelY),
                new SizeF(panelWidth, panelHeight),
                Color.FromArgb(192, 10, 14, 28)
            );

            heatBox.Items.Add(new TextElement("4x4 QUANTUM INTERFERENCE HEATMAP", new PointF(panelWidth / 2f, 6f), 0.28f, Color.FromArgb(255, 130, 235, 255), Font.ChaletLondon, Alignment.Center, false, true));
            heatBox.Items.Add(new TextElement("INPUT DENSITY", new PointF(84f, 26f), 0.24f, Color.FromArgb(255, 180, 215, 255), Font.ChaletLondon, Alignment.Center, false, true));
            heatBox.Items.Add(new TextElement("ATLAS BLURRED", new PointF(256f, 26f), 0.24f, Color.FromArgb(255, 240, 165, 255), Font.ChaletLondon, Alignment.Center, false, true));

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

            const float cellSize = 24f;
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
                        new PointF(30f + c * (cellSize + gap), startY + r * (cellSize + gap)),
                        new SizeF(cellSize, cellSize),
                        inColor
                    ));

                    double outVal = (blurred != null && r < blurred.Length && blurred[r] != null && c < blurred[r].Length) ? blurred[r][c] : 0.05;
                    float outNorm = (float)Math.Max(0.0, Math.Min(1.0, outVal / Math.Max(0.25, blurMax)));
                    bool isHotspot = hotspotCells.Contains((r, c));

                    Color outColor = isHotspot
                        ? Color.FromArgb(245, 255, 70, 210)
                        : Color.FromArgb(55 + (int)(outNorm * 185), (int)(110 + outNorm * 135), 55, 225);

                    float cellX = 200f + c * (cellSize + gap);
                    float cellY = startY + r * (cellSize + gap);

                    if (isHotspot)
                    {
                        heatBox.Items.Add(new ContainerElement(
                            new PointF(cellX - 1.5f, cellY - 1.5f),
                            new SizeF(cellSize + 3f, cellSize + 3f),
                            Color.FromArgb(255, 255, 225, 80)
                        ));
                    }

                    heatBox.Items.Add(new ContainerElement(new PointF(cellX, cellY), new SizeF(cellSize, cellSize), outColor));
                }
            }

            heatBox.Items.Add(new TextElement("OTOC ECHO (t=1..8)", new PointF(108f, 166f), 0.24f, Color.FromArgb(255, 255, 190, 245), Font.ChaletLondon, Alignment.Center, false, true));

            const float waveX = 16f;
            const float zeroY = 234f;
            const float maxBarH = 40f;
            heatBox.Items.Add(new ContainerElement(new PointF(waveX, zeroY), new SizeF(184f, 1.5f), Color.FromArgb(140, 200, 220, 255)));

            double[] steps = t?.OtocEchoSteps;
            int activeStep = t != null ? t.ActiveOtocStep : -1;
            for (int i = 0; i < 8; i++)
            {
                double val = (steps != null && i < steps.Length) ? steps[i] : 0.0;
                float barH = Math.Max(4f, (float)(Math.Min(1.0, Math.Abs(val)) * maxBarH));
                float bx = waveX + 4f + i * 22f;
                float by = val >= 0 ? (zeroY - barH) : (zeroY + 2f);
                bool isActive = i == activeStep;

                Color barCol = isActive
                    ? Color.FromArgb(255, 255, 230, 80)
                    : (val >= 0 ? Color.FromArgb(220, 60, 235, 255) : Color.FromArgb(220, 255, 75, 205));

                if (isActive)
                {
                    heatBox.Items.Add(new ContainerElement(new PointF(bx - 1.5f, by - 1.5f), new SizeF(19f, barH + 3f), Color.White));
                }

                heatBox.Items.Add(new ContainerElement(new PointF(bx, by), new SizeF(16f, barH), barCol));
            }

            heatBox.Items.Add(new TextElement("SHADER LUT", new PointF(276f, 166f), 0.24f, Color.FromArgb(255, 145, 255, 230), Font.ChaletLondon, Alignment.Center, false, true));
            heatBox.Items.Add(new ContainerElement(new PointF(228f, 186f), new SizeF(96f, 96f), Color.FromArgb(220, 20, 32, 54)));

            heatBox.ScaledDraw();

            if (t != null && !string.IsNullOrEmpty(t.ShaderPngPath))
            {
                EnsureShaderSprites(t.ShaderPngPath);
                if (_hudShaderSprite != null)
                {
                    _hudShaderSprite.Position = new PointF(panelX + 231f, panelY + 189f);
                    _hudShaderSprite.Size = new SizeF(90f, 90f);
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

            bool imminent = (nextPulseTime - now) <= 1050;
            bool scrambled = activeEchoVal < 0;

            Color innerColor = weatherIndex <= 1
                ? Color.FromArgb(200, 90, 255, 165)
                : (weatherIndex >= 7 && !scrambled
                    ? Color.FromArgb(190, 170, 95, 255)
                    : (scrambled ? Color.FromArgb(210, 255, 75, 200) : Color.FromArgb(185, 65, 235, 255)));

            Color ringColor = weatherIndex <= 1
                ? (imminent ? Color.FromArgb(235, 255, 225, 85) : Color.FromArgb(210, 70, 255, 150))
                : (imminent
                    ? Color.FromArgb(235, 255, 215, 65)
                    : (scrambled ? Color.FromArgb(200, 255, 80, 220) : Color.FromArgb(190, 0, 240, 255)));

            float ampBoost = (float)(0.85 + Math.Min(1.0, Math.Abs(activeEchoVal)) * 0.45);
            float pulseScale = (imminent ? 11.0f : 8.5f) * ampBoost;

            foreach (var h in hotspots)
            {
                Vector3 spot = GetHotspotWorldPos(playerPos, h);
                if (spot.DistanceTo(playerPos) > 190f) continue;

                // Ground circles only (clean street rings, no distracting tall beam blocking buildings/cars)
                World.DrawMarker(
                    MarkerType.HorizontalCircleSkinny,
                    spot + new Vector3(0f, 0f, 0.12f),
                    Vector3.Zero,
                    Vector3.Zero,
                    new Vector3(pulseScale, pulseScale, 1.0f),
                    ringColor
                );

                World.DrawMarker(
                    MarkerType.HorizontalCircleSkinny,
                    spot + new Vector3(0f, 0f, 0.16f),
                    Vector3.Zero,
                    Vector3.Zero,
                    new Vector3(2.8f, 2.8f, 1.0f),
                    innerColor
                );
            }
        }

        public Vector3 GetHotspotWorldPos(Vector3 playerPos, HotspotTarget h)
        {
            if (h.FollowPlayer)
            {
                float sx = playerPos.X + h.OffsetX * 0.22f;
                float sy = playerPos.Y + h.OffsetY * 0.22f;
                return new Vector3(sx, sy, playerPos.Z - 0.9f);
            }
            return new Vector3(h.WorldX, h.WorldY, h.GroundZ);
        }

        public void DrawWorldEntityTagsAndTether(
            Ped playerPed,
            Vehicle entangledTwin,
            List<HotspotTarget> hotspots,
            int weatherIndex,
            TelemetrySnapshot t,
            HudDisplayMode hudMode,
            double lastOtocVal)
        {
            Vector3 playerPos = playerPed.Position;
            string jobTag = t != null ? t.SpatialJobId : "sim";
            _isFullTelemetryFrame = hudMode == HudDisplayMode.FullTelemetry;
            float minY = _isFullTelemetryFrame ? 218f : (hudMode == HudDisplayMode.Compact ? 105f : 25f);
            var placedCards = new List<PointF>();

            // 1. Entangled Twin tether line + bright non-overlapping tag
            if (entangledTwin != null && entangledTwin.Exists() && entangledTwin.IsAlive)
            {
                Vector3 from = (playerPed.CurrentVehicle != null && playerPed.CurrentVehicle.Exists())
                    ? playerPed.CurrentVehicle.Position + new Vector3(0f, 0f, 1.0f)
                    : playerPos + new Vector3(0f, 0f, 1.0f);
                Vector3 to = entangledTwin.Position + new Vector3(0f, 0f, 1.1f);

                World.DrawLine(from, to, Color.FromArgb(230, 255, 60, 220));

                if (entangledTwin.IsOnScreen && entangledTwin.Position.DistanceTo(playerPos) < 90f)
                {
                    TryDrawFloatingCard(
                        entangledTwin.Position + new Vector3(0f, 0f, 2.1f),
                        "LINKED CAR · X / Horn to Throw",
                        $"Copies Moves · {entangledTwin.Speed:F1} m/s",
                        Color.FromArgb(255, 255, 95, 235),
                        minY,
                        placedCards
                    );
                }
            }

            // 2. Label nearest 3D Pillar within 42m so players immediately know what the pillar does
            if (hotspots != null)
            {
                string pillarTitle = weatherIndex <= 1
                    ? "OVERCLOCK BUFF ZONE"
                    : (weatherIndex >= 7
                        ? (lastOtocVal >= 0 ? "SINGULARITY VORTEX" : "SINGULARITY BLAST")
                        : (weatherIndex >= 5 ? "EMP STRIKE ZONE" : "WARP ANCHOR"));
                string pillarSub = weatherIndex <= 1
                    ? "Enter: Armor + Repair + Ammo"
                    : $"Atlas 2D Peak [{jobTag}]";

                foreach (var h in hotspots)
                {
                    Vector3 spot = GetHotspotWorldPos(playerPos, h);
                    if (spot.DistanceTo(playerPos) <= 45f)
                    {
                        if (TryDrawFloatingCard(spot + new Vector3(0f, 0f, 3.0f), pillarTitle, pillarSub, Color.FromArgb(255, 90, 240, 255), minY, placedCards))
                            break; // Show 1 crisp pillar label at a time
                    }
                }
            }

            if (weatherIndex <= 1) return;

            // 3. Up to 2 well-spaced nearby affected vehicles (never overlapping)
            int drawnVeh = 0;
            foreach (Vehicle veh in World.GetNearbyVehicles(playerPos, 45f))
            {
                if (drawnVeh >= 2) break;
                if (veh == null || !veh.Exists() || veh == playerPed.CurrentVehicle || veh == entangledTwin) continue;
                if (!veh.IsOnScreen) continue;

                string statusText = !veh.IsEngineRunning
                    ? "EMP STALLED"
                    : (weatherIndex >= 7
                        ? (lastOtocVal >= 0 ? "VORTEX PULL" : "ANTI-GRAV BLAST")
                        : (weatherIndex >= 5 ? "ICY TIRES (0%)" : "TIME WARP"));

                float dist = veh.Position.DistanceTo(playerPos);
                if (TryDrawFloatingCard(
                    veh.Position + new Vector3(0f, 0f, 1.85f),
                    $"{statusText} · {dist:F0}m",
                    $"Atlas blur-core [{jobTag}]",
                    Color.FromArgb(255, 80, 245, 255),
                    minY,
                    placedCards
                ))
                {
                    drawnVeh++;
                }
            }

            // 4. Up to 1 well-spaced nearby NPC tag (never overlapping vehicles/Twin/pillar)
            string pedState = weatherIndex >= 7
                ? (lastOtocVal >= 0 ? "VORTEX PULL" : "RAGDOLL BLAST")
                : (weatherIndex >= 5 ? "PANICKING" : "BLIND TO PLAYER");

            foreach (Ped p in World.GetNearbyPeds(playerPos, 28f))
            {
                if (p == null || !p.Exists() || p.IsPlayer || !p.IsAlive || !p.IsOnScreen) continue;

                string tagState = p.IsRagdoll ? "COLLAPSED" : pedState;
                if (TryDrawFloatingCard(
                    p.Position + new Vector3(0f, 0f, 1.25f),
                    $"{tagState} · {p.Health} HP",
                    $"Atlas blur-core [{jobTag}]",
                    Color.FromArgb(255, 130, 255, 195),
                    minY,
                    placedCards
                ))
                {
                    break;
                }
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

        private bool TryDrawFloatingCard(Vector3 worldPos, string line1, string line2, Color accentColor, float minY, List<PointF> placedCards)
        {
            PointF pt = Screen.WorldToScreen(worldPos, true);
            if (pt.IsEmpty || pt.X < 25f || pt.X > Screen.ScaledWidth - 225f || pt.Y < minY || pt.Y > 650f)
                return false;

            float cardX = Math.Max(8f, Math.Min(Screen.ScaledWidth - 203f, pt.X - 95f));
            float cardY = pt.Y - 36f;

            // Keep cards clear of the Right Heatmap / OTOC / Shader box in Full Telemetry mode
            if (_isFullTelemetryFrame && cardX + 195f >= 770f && cardY < 312f)
                return false;

            // Prevent overlapping cards on screen
            foreach (PointF other in placedCards)
            {
                if (Math.Abs(cardX - other.X) < 205f && Math.Abs(cardY - other.Y) < 46f)
                    return false;
            }
            placedCards.Add(new PointF(cardX, cardY));

            var card = new ContainerElement(
                new PointF(cardX, cardY),
                new SizeF(195f, 38f),
                Color.FromArgb(185, 8, 22, 34)
            );

            card.Items.Add(new ContainerElement(new PointF(0f, 0f), new SizeF(195f, 2.5f), accentColor));
            card.Items.Add(new TextElement(line1, new PointF(6f, 4f), 0.25f, accentColor, Font.ChaletLondon, Alignment.Left, false, true));
            card.Items.Add(new TextElement(line2, new PointF(6f, 19f), 0.22f, Color.FromArgb(255, 220, 235, 250), Font.ChaletLondon, Alignment.Left, false, true));
            card.ScaledDraw();
            return true;
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
