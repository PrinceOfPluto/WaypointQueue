using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace WaypointQueue
{
    [Serializable]
    public class RouteDefinition
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "New Route";
        public List<RouteWaypoint> Waypoints { get; set; } = new List<RouteWaypoint>();

        [JsonIgnore] public string FilePath { get; set; }  // not serialized
    }

    // If you already created RouteWaypoint earlier, reuse it.
    [Serializable]
    public class RouteWaypoint
    {
        public string LocationString { get; set; }
        public string CoupleToCarId { get; set; } = "";
        public bool ConnectAirOnCouple { get; set; }
        public bool ReleaseHandbrakesOnCouple { get; set; }
        public bool ApplyHandbrakesOnUncouple { get; set; }
        public bool BleedAirOnUncouple { get; set; }
        public int NumberOfCarsToCut { get; set; }
        public bool CountUncoupledFromNearestToWaypoint { get; set; } = true;
        public ManagedWaypoint.PostCoupleCutType TakeOrLeaveCut { get; set; } = ManagedWaypoint.PostCoupleCutType.Take;
        public bool TakeUncoupledCarsAsActiveCut { get; set; }

        public SerializableVector3 SerializableRefuelPoint { get; set; }
        public string RefuelIndustryId { get; set; }
        public string RefuelLoadName { get; set; }
        public float RefuelMaxCapacity { get; set; }
        public bool WillRefuel { get; set; }

        public ManagedWaypoint ToManagedWaypoint(Model.Car locomotive)
        {
            var location = Track.Graph.Shared.ResolveLocationString(LocationString);
            var mw = new ManagedWaypoint(
                locomotive, location, CoupleToCarId,
                connectAirOnCouple: ConnectAirOnCouple,
                releaseHandbrakesOnCouple: ReleaseHandbrakesOnCouple,
                applyHandbrakeOnUncouple: ApplyHandbrakesOnUncouple,
                numberOfCarsToCut: NumberOfCarsToCut,
                countUncoupledFromNearestToWaypoint: CountUncoupledFromNearestToWaypoint,
                bleedAirOnUncouple: BleedAirOnUncouple,
                takeOrLeaveCut: TakeOrLeaveCut
            );
            mw.TakeUncoupledCarsAsActiveCut = TakeUncoupledCarsAsActiveCut;

            mw.SerializableRefuelPoint = SerializableRefuelPoint;
            mw.RefuelIndustryId = RefuelIndustryId;
            mw.RefuelLoadName = RefuelLoadName;
            mw.RefuelMaxCapacity = RefuelMaxCapacity;
            mw.WillRefuel = WillRefuel;
            return mw;
        }

        public static RouteWaypoint FromManagedWaypoint(ManagedWaypoint mw)
        {
            return new RouteWaypoint
            {
                LocationString = mw.LocationString,
                CoupleToCarId = mw.CoupleToCarId,
                ConnectAirOnCouple = mw.ConnectAirOnCouple,
                ReleaseHandbrakesOnCouple = mw.ReleaseHandbrakesOnCouple,
                ApplyHandbrakesOnUncouple = mw.ApplyHandbrakesOnUncouple,
                BleedAirOnUncouple = mw.BleedAirOnUncouple,
                NumberOfCarsToCut = mw.NumberOfCarsToCut,
                CountUncoupledFromNearestToWaypoint = mw.CountUncoupledFromNearestToWaypoint,
                TakeOrLeaveCut = mw.TakeOrLeaveCut,
                TakeUncoupledCarsAsActiveCut = mw.TakeUncoupledCarsAsActiveCut,
                SerializableRefuelPoint = mw.SerializableRefuelPoint,
                RefuelIndustryId = mw.RefuelIndustryId,
                RefuelLoadName = mw.RefuelLoadName,
                RefuelMaxCapacity = mw.RefuelMaxCapacity,
                WillRefuel = mw.WillRefuel
            };
        }
    }
}
