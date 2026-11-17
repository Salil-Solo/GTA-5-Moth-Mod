using System.Collections.Generic;

namespace QuantumRealityShiftV4
{
    public enum QuantumBiasMode
    {
        AutoChaos = 0,
        FavorOverclock = 1,
        FavorStealth = 2,
        FavorStorm = 3,
        FavorSingularity = 4
    }

    public enum HudDisplayMode
    {
        Compact = 0,
        FullTelemetry = 1,
        Off = 2
    }

    public enum ElixirTier
    {
        None = 0,
        MildDose = 1,
        UnstableBrew = 2,
        SingularityOverdose = 3
    }

    public class ChaosSnapshot
    {
        public float Total;
        public int WantedStars;
        public int NearbyPeds;
        public int PlayerHp;
        public int NearbyVehicles;
    }

    public class HotspotTarget
    {
        public int Row;
        public int Col;
        public float OffsetX;
        public float OffsetY;
        public double Score;

        // V4: World-anchored strike coordinates locked at collapse time
        public float WorldX;
        public float WorldY;
        public float GroundZ;
        public bool FollowPlayer;
    }

    public class TelemetrySnapshot
    {
        public string TriggerReason;
        public string BiasLabel;
        public string SourceLabel;
        public double LatencySec;
        public ChaosSnapshot Chaos;
        public double BlurStrength;
        public double[] SentWeatherPriors;
        public double[] RecvWeatherProbs;
        public int ChosenWeatherIndex;
        public double[][] InputGrid;
        public double[][] BlurredGrid;
        public double GridDensitySum;
        public List<HotspotTarget> Hotspots;
        public int CoinHeads;
        public int CoinShots;
        public int FlashMs;
        public bool Surge;

        // V3/V4: OTOC Echo (otoc-echo-v1) & Entanglement Shader (entanglement-shader-v1)
        public double[] OtocEchoSteps;
        public int ActiveOtocStep;
        public string ShaderStyle;
        public double ShaderMeanR;
        public double ShaderMeanT;
        public string ShaderPngPath;

        // V4 Gameplay State Telemetry
        public float InstabilityPercent;
        public bool OverdriveActive;
        public bool ContainmentBreachActive;
        public string ActiveModeName;

        public string WeatherJobId;
        public string SpatialJobId;
        public string CoinJobId;
        public string OtocJobId;
        public string ShaderJobId;
        public int CollapseId;
    }

    public class PendingShaderUpdate
    {
        public int CollapseId;
        public string ShaderJobId;
        public string ShaderPngPath;
        public double MeanR;
        public double MeanT;
        public string Error;
    }

    public class PendingCollapse
    {
        public int WeatherIndex;
        public float FlashIntensity;
        public bool Surge;
        public float ChaosLevel;
        public bool UsedFallback;
        public int DurationMs;
        public ElixirTier ElixirTier;
        public float OriginX;
        public float OriginY;
        public float OriginZ;
        public List<HotspotTarget> Hotspots;
        public double[] OtocEchoSteps;
        public string ShaderPngPath;
        public TelemetrySnapshot Telemetry;
    }
}
