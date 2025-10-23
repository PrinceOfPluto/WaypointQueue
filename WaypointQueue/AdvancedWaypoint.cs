using Model;
using System;
using Track;

namespace WaypointQueue
{
    public class AdvancedWaypoint
    {
        public string Id { get; private set; }

        public BaseLocomotive Locomotive { get; private set; }

        public Location Location { get; private set; }

        public string CoupleToCarId { get; private set; }

        public bool IsCoupling
        {
            get
            {
                return CoupleToCarId != null && CoupleToCarId.Length > 0;
            }
        }

        public bool IsUncoupling
        {
            get
            {
                return NumberOfCarsToUncouple > 0;
            }
        }

        public bool ConnectAirOnCouple { get; set; }
        public bool ReleaseHandbrakesOnCouple { get; set; }
        public bool ApplyHandbrakesOnUncouple { get; set; }
        public bool BleedAirOnUncouple { get; set; }

        public int NumberOfCarsToUncouple { get; set; }
        public bool UncoupleNearestToWaypoint { get; set; }

        public AdvancedWaypoint(BaseLocomotive locomotive, Location location, string coupleToCarId = "", bool connectAirOnCouple = true, bool releaseHandbrakesOnCouple = true, bool applyHandbrakeOnUncouple = true, int numberOfCarsToUncouple = 0, bool uncoupleNearestToWaypoint = true, bool bleedAirOnUncouple = true)
        {
            Id = Guid.NewGuid().ToString();
            Locomotive = locomotive;
            Location = location;
            CoupleToCarId = coupleToCarId;
            ConnectAirOnCouple = connectAirOnCouple;
            ReleaseHandbrakesOnCouple = releaseHandbrakesOnCouple;
            ApplyHandbrakesOnUncouple = applyHandbrakeOnUncouple;
            NumberOfCarsToUncouple = numberOfCarsToUncouple;
            UncoupleNearestToWaypoint = uncoupleNearestToWaypoint;
            BleedAirOnUncouple = bleedAirOnUncouple;
        }
    }
}
