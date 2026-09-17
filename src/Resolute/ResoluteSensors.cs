using UnityEngine;

namespace Resolute
{
    internal static class ResoluteSensors
    {
        internal const float NominalRadarRange = 75000f;

        // Called only on Resolute's cloned prefab. RadarParams is a value type;
        // the Dynamo donor and its signal/clutter/jamming rules stay native.
        internal static void Configure(Ship ship)
        {
            Radar radar = ship != null ? ship.radar as Radar : null;
            if (radar == null) return;
            RadarParams parameters = radar.RadarParameters;
            parameters.maxRange = NominalRadarRange;
            radar.RadarParameters = parameters;
        }
    }
}
