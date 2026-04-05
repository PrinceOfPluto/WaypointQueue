using System.Collections.Generic;

namespace WaypointQueue.Model
{
    internal struct DelayedBleedAirCutEntry(double delayBleedUntilGameTotalSeconds, List<string> carIds)
    {
        public double DelayBleedUntilGameTotalSeconds = delayBleedUntilGameTotalSeconds;
        public List<string> CarIds = carIds;
    }
}
