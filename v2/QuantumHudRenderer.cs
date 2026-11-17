using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.UI;
using Font = GTA.UI.Font;

namespace QuantumRealityShiftV2
{
    /// <summary>
    /// Renders all Phase 2 visual elements:
    ///   1. Top-left Moth Atlas Telemetry Log (F9 toggle)
    ///   2. Top-right Live 4x4 Input vs. Quantum-Blurred Heatmap (F9 toggle)
    ///   3. 3D Glowing Quantum Strike Pillars & Ground Rings at the 3 blur-core-v1 hotspots
    ///   4. 3D Floating Cyan Sci-Fi Target Tags over affected cars & the Entangled Twin Vehicle
    /// </summary>
    public class QuantumHudRenderer
    {
        private readonly string[] _weatherTypes;

        public QuantumHudRenderer(string[] weatherTypes)
        {
            _weatherTypes = weatherTypes;
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
            // ── Left Panel: Telemetry Log ────────────────────────────────────
            var box = new ContainerElement(
                new PointF(16f, 12f),
                new SizeF(885f, 178f),
                Color.FromArgb(190, 10, 14, 28)
            );

            string autoTag = autoCollapseEnabled ? "ON" : "OFF";
            string remainTag = effectActive
                ? $"{Math.Max(0, (effectEndTime - now) / 1000)}s left"
                : "Inactive";

            var lines = new List<(string text, Color color)>();

            if (isFetching)
            {
                lines.Add(($"MOTH ATLAS TELEMETRY [F9 Hide] | SENDING LIVE REQUEST ({lastFetchReason})...", Color.FromArgb(255, 255, 210, 90)));
                lines.Add(("POST /api/v1/engines/blur-core-v1/process (1D Weather + 2D 4x4 Grid)", Color.FromArgb(255, 165, 225, 255)));
                lines.Add(("POST /api/v1/engines/coin-toss-v1/process (16 shots, mode=emu)", Color.FromArgb(255, 165, 225, 255)));
                lines.Add(("Polling GET /jobs/{id}/status async on background thread (60 FPS unblocked)...", Color.FromArgb(255, 200, 205, 220)));
            }
            else if (t == null)
            {
                lines.Add(($"QUANTUM REALITY SHIFT V2 [F9 Hide] | Auto[F7]: {autoTag} | Ready", Color.FromArgb(255, 195, 170, 255)));
                lines.Add(("F5:Menu | F6:Collapse | F7:Auto | F8:Reset | F10:Quantum Elixir Vendor", Color.FromArgb(255, 220, 230, 240)));
                lines.Add(("Sends city chaos + 4x4 entity grid to Atlas blur-core-v1 & coin-toss-v1.", Color.FromArgb(255, 150, 220, 255)));
                lines.Add(("3D pillars, floating car/NPC tags, and twin tether activate on collapse.", Color.FromArgb(255, 150, 245, 190)));
            }
            else
            {
                Color statusColor = t.SourceLabel.StartsWith("LIVE")
                    ? Color.FromArgb(255, 100, 255, 165)
                    : Color.FromArgb(255, 255, 185, 90);

                lines.Add(($"ATLAS V2 [F9] | {t.SourceLabel} ({t.LatencySec:F2}s) | {t.TriggerReason} | {remainTag}", statusColor));
                lines.Add(($"INPUT ({t.BiasLabel}): Chaos={t.Chaos.Total:F2} (Stars:{t.Chaos.WantedStars} Peds:{t.Chaos.NearbyPeds} Cars:{t.Chaos.NearbyVehicles}) Str={t.BlurStrength:F2}", Color.FromArgb(255, 235, 235, 245)));
                lines.Add(($"SEND 1D [{t.WeatherJobId}]: [{FormatArray(t.SentWeatherPriors)}] (priors)", Color.FromArgb(255, 145, 215, 255)));
                lines.Add(($"RECV 1D: [{FormatArray(t.RecvWeatherProbs)}] -> #{t.ChosenWeatherIndex} {_weatherTypes[t.ChosenWeatherIndex]}", Color.FromArgb(255, 215, 175, 255)));
                lines.Add(($"2D 4x4 [{t.SpatialJobId}, sum={t.GridDensitySum:F1}] -> Strike Zones: {FormatHotspots(t.Hotspots)}", Color.FromArgb(255, 255, 220, 130)));
                lines.Add(($"COIN [{t.CoinJobId}]: {t.CoinHeads}/{t.CoinShots} heads -> Flash={t.FlashMs}ms Surge={(t.Surge ? "ON (Twin)" : "OFF")}", Color.FromArgb(255, 165, 245, 220)));
                lines.Add(($"EFFECT: {activeEffectLabel} | F5:Menu F6:Collapse F8:Reset F10:Elixir", Color.FromArgb(255, 255, 255, 255)));
            }

            float y = 8f;
            foreach (var (text, color) in lines)
            {
                string safeText = text.Length > 92 ? text.Substring(0, 89) + "..." : text;
                box.Items.Add(new TextElement(
                    safeText,
                    new PointF(12f, y),
                    0.32f,
                    color,
                    Font.ChaletLondon,
                    Alignment.Left,
                    false,
                    true
                ));
                y += 23f;
            }

            float panelHeight = Math.Max(110f, y + 8f);
            box.Size = new SizeF(885f, panelHeight);
            box.ScaledDraw();

            // ── Right Panel: Live 4x4 Quantum Interference Heatmap ───────────
            DrawHeatmapPanel(t, panelHeight);
        }

        private void DrawHeatmapPanel(TelemetrySnapshot t, float panelHeight)
        {
            var heatBox = new ContainerElement(
                new PointF(912f, 12f),
                new SizeF(352f, Math.Max(176f, panelHeight)),
                Color.FromArgb(190, 10, 14, 28)
            );

            heatBox.Items.Add(new TextElement(
                "4x4 QUANTUM INTERFERENCE HEATMAP",
                new PointF(176f, 7f),
                0.30f,
                Color.FromArgb(255, 130, 235, 255),
                Font.ChaletLondon,
                Alignment.Center,
                false,
                true
            ));

            heatBox.Items.Add(new TextElement(
                "INPUT DENSITY",
                new PointF(88f, 29f),
                0.26f,
                Color.FromArgb(255, 180, 215, 255),
                Font.ChaletLondon,
                Alignment.Center,
                false,
                true
            ));

            heatBox.Items.Add(new TextElement(
                "ATLAS BLURRED",
                new PointF(264f, 29f),
                0.26f,
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

            const float cellSize = 26f;
            const float gap = 4f;
            const float startY = 48f;

            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    // Left grid: raw entity density (cyan scale)
                    double inVal = (input != null && r < input.Length && input[r] != null && c < input[r].Length) ? input[r][c] : 0.05;
                    float inNorm = (float)Math.Max(0.0, Math.Min(1.0, inVal / Math.Max(0.25, inputMax)));
                    int inAlpha = 55 + (int)(inNorm * 195);
                    Color inColor = Color.FromArgb(inAlpha, 30, (int)(130 + inNorm * 125), 255);

                    heatBox.Items.Add(new ContainerElement(
                        new PointF(30f + c * (cellSize + gap), startY + r * (cellSize + gap)),
                        new SizeF(cellSize, cellSize),
                        inColor
                    ));

                    // Right grid: blur-core-v1 output (magenta/gold for top 3 hotspots)
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
                        // Bright gold 1px border frame around selected strike cells
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

            heatBox.ScaledDraw();
        }

        public void DrawHotspotMarkers(Vector3 playerPos, List<HotspotTarget> hotspots, int weatherIndex, int now, int nextPulseTime)
        {
            if (hotspots == null || hotspots.Count == 0) return;

            bool imminent = (nextPulseTime - now) <= 1200;
            Color beamColor = weatherIndex <= 1
                ? Color.FromArgb(115, 80, 255, 175)
                : (imminent ? Color.FromArgb(180, 255, 65, 195) : Color.FromArgb(135, 45, 225, 255));

            Color ringColor = imminent
                ? Color.FromArgb(220, 255, 210, 65)
                : Color.FromArgb(175, 0, 240, 255);

            float pulseScale = imminent ? 10.5f : 8.5f;

            foreach (var h in hotspots)
            {
                float sx = playerPos.X + h.OffsetX;
                float sy = playerPos.Y + h.OffsetY;
                float groundZ = World.GetGroundHeight(new Vector3(sx, sy, playerPos.Z + 6f));
                if (groundZ <= -200f || Math.Abs(groundZ) <= 0.01f)
                    groundZ = World.GetGroundHeight(new Vector2(sx, sy));
                float sz = groundZ > -200f && Math.Abs(groundZ) > 0.01f ? groundZ : playerPos.Z;
                Vector3 spot = new Vector3(sx, sy, sz);

                // Tall glowing quantum pillar
                World.DrawMarker(
                    MarkerType.VerticalCylinder,
                    spot,
                    Vector3.Zero,
                    Vector3.Zero,
                    new Vector3(2.2f, 2.2f, 18.0f),
                    beamColor
                );

                // Ground target ring
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
            if (weatherIndex <= 1) return; // Calm mode doesn't disrupt traffic/peds

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

        private void DrawFloatingCard(Vector3 worldPos, string line1, string line2, Color accentColor)
        {
            PointF pt = Screen.WorldToScreen(worldPos, true);
            if (pt.IsEmpty || pt.X < 25f || pt.X > Screen.ScaledWidth - 225f || pt.Y < 195f || pt.Y > 650f)
                return;

            float cardX = Math.Max(8f, Math.Min(Screen.ScaledWidth - 203f, pt.X - 95f));
            var card = new ContainerElement(
                new PointF(cardX, pt.Y - 36f),
                new SizeF(195f, 38f),
                Color.FromArgb(180, 8, 22, 34)
            );

            // Top cyan/magenta accent bar
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
