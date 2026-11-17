using System.Collections.Generic;

namespace QuantumRealityShiftV2
{
    public enum QuantumBiasMode
    {
        AutoChaos = 0,
        FavorOverclock = 1,
        FavorStealth = 2,
        FavorStorm = 3,
        FavorSingularity = 4
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
        public string WeatherJobId;
        public string SpatialJobId;
        public string CoinJobId;
    }

    public class PendingCollapse
    {
        public int WeatherIndex;
        public float FlashIntensity;
        public bool Surge;
        public float ChaosLevel;
        public bool UsedFallback;
        public int DurationMs;
        public List<HotspotTarget> Hotspots;
        public TelemetrySnapshot Telemetry;
    }
}
