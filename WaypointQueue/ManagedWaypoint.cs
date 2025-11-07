using Model;
using Model.Ops;
using Newtonsoft.Json;
using System;
using Track;
using UnityEngine;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    public class ManagedWaypoint
    {
        public enum PostCoupleCutType
        {
            Take,
            Leave
        }
        [JsonProperty]
        public string Id { get; private set; }

        [JsonProperty]
        public string LocomotiveId { get; private set; }

        [JsonIgnore]
        public Car Locomotive { get; private set; }

        [JsonProperty]
        public string LocationString { get; private set; }

        [JsonIgnore]
        public Location Location { get; private set; }

        [JsonProperty]
        public string CoupleToCarId { get; private set; }

        [JsonIgnore]
        public bool IsCoupling
        {
            get
            {
                return CoupleToCarId != null && CoupleToCarId.Length > 0;
            }
        }

        [JsonIgnore]
        public bool IsUncoupling
        {
            get
            {
                return !IsCoupling && NumberOfCarsToCut > 0;
            }
        }

        public bool ConnectAirOnCouple { get; set; }
        public bool ReleaseHandbrakesOnCouple { get; set; }
        public bool ApplyHandbrakesOnUncouple { get; set; }
        public bool BleedAirOnUncouple { get; set; }

        public int NumberOfCarsToCut { get; set; }
        public bool CountUncoupledFromNearestToWaypoint { get; set; }
        public PostCoupleCutType TakeOrLeaveCut { get; set; }
        public bool TakeUncoupledCarsAsActiveCut { get; set; }

        [JsonIgnore]
        public bool CanRefuelNearby
        {
            get { return RefuelPoint != null && RefuelLoadName != null && RefuelLoadName.Length > 0; }
        }

        [JsonIgnore]
        public Vector3 RefuelPoint
        {
            get
            {
                return SerializableRefuelPoint.ToVector3();
            }
        }

        [JsonProperty]
        public SerializableVector3 SerializableRefuelPoint { get; set; }

        public string RefuelIndustryId { get; set; }
        public string RefuelLoadName { get; set; }
        public float RefuelMaxCapacity { get; set; }
        public bool WillRefuel { get; set; }
        public bool CurrentlyRefueling { get; set; }
        public int MaxSpeedAfterRefueling { get; set; }
        public string AreaName { get; set; }

        public void Load()
        {
            if (TrainController.Shared.TryGetCarForId(LocomotiveId, out Car locomotive))
            {
                Loader.LogDebug($"Loaded locomotive {locomotive.Ident} for ManagedWaypoint");
                Locomotive = locomotive;
            }
            else
            {
                throw new InvalidOperationException($"Could not find car for {LocomotiveId}");
            }

            Location = Graph.Shared.ResolveLocationString(LocationString);
            Loader.LogDebug($"Loaded location {Location} for {locomotive.Ident} ManagedWaypoint");

            AreaName = OpsController.Shared.ClosestAreaForGamePosition(Location.GetPosition()).name;
        }

        public ManagedWaypoint(Car locomotive, Location location, string coupleToCarId = "", bool connectAirOnCouple = true, bool releaseHandbrakesOnCouple = true, bool applyHandbrakeOnUncouple = true, int numberOfCarsToCut = 0, bool countUncoupledFromNearestToWaypoint = true, bool bleedAirOnUncouple = true, PostCoupleCutType takeOrLeaveCut = PostCoupleCutType.Take)
        {
            Id = Guid.NewGuid().ToString();
            Locomotive = locomotive;
            LocomotiveId = locomotive.id;
            Location = location;
            LocationString = Graph.Shared.LocationToString(location);
            CoupleToCarId = coupleToCarId;
            ConnectAirOnCouple = connectAirOnCouple;
            ReleaseHandbrakesOnCouple = releaseHandbrakesOnCouple;
            ApplyHandbrakesOnUncouple = applyHandbrakeOnUncouple;
            NumberOfCarsToCut = numberOfCarsToCut;
            CountUncoupledFromNearestToWaypoint = countUncoupledFromNearestToWaypoint;
            BleedAirOnUncouple = bleedAirOnUncouple;
            TakeOrLeaveCut = takeOrLeaveCut;
            WillRefuel = false;
            CurrentlyRefueling = false;
            AreaName = OpsController.Shared.ClosestAreaForGamePosition(Location.GetPosition()).name;
            TakeUncoupledCarsAsActiveCut = false;
        }

        [JsonConstructor]
        public ManagedWaypoint(string id, string locomotiveId, string locationString, string coupleToCarId, bool connectAirOnCouple, bool releaseHandbrakesOnCouple, bool applyHandbrakesOnUncouple, bool bleedAirOnUncouple, int numberOfCarsToCut, bool countUncoupledFromNearestToWaypoint, PostCoupleCutType takeOrLeaveCut, SerializableVector3 serializableRefuelPoint, string refuelIndustryId, string refuelLoadName, float refuelMaxCapacity, bool willRefuel, bool currentlyRefueling, string areaName, bool takeUncoupledCarsAsActiveCut, int maxSpeedAfterRefueling)
        {
            Id = id;
            LocomotiveId = locomotiveId;
            LocationString = locationString;
            CoupleToCarId = coupleToCarId;
            ConnectAirOnCouple = connectAirOnCouple;
            ReleaseHandbrakesOnCouple = releaseHandbrakesOnCouple;
            ApplyHandbrakesOnUncouple = applyHandbrakesOnUncouple;
            BleedAirOnUncouple = bleedAirOnUncouple;
            NumberOfCarsToCut = numberOfCarsToCut;
            CountUncoupledFromNearestToWaypoint = countUncoupledFromNearestToWaypoint;
            TakeOrLeaveCut = takeOrLeaveCut;
            SerializableRefuelPoint = serializableRefuelPoint;
            RefuelIndustryId = refuelIndustryId;
            RefuelLoadName = refuelLoadName;
            RefuelMaxCapacity = refuelMaxCapacity;
            WillRefuel = willRefuel;
            CurrentlyRefueling = currentlyRefueling;
            AreaName = areaName;
            TakeUncoupledCarsAsActiveCut = takeUncoupledCarsAsActiveCut;
            MaxSpeedAfterRefueling = maxSpeedAfterRefueling;
        }
    }

    [Serializable]
    public struct SerializableVector3
    {
        public SerializableVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3 ToVector3()
        {
            return new Vector3(X, Y, Z);
        }
    }
}
