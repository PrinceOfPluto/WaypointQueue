using Game;
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
        // Used for JSON deserialization
        public ManagedWaypoint() { }

        public ManagedWaypoint(Car locomotive, Location location, string coupleToCarId = "")
        {
            Locomotive = locomotive;
            LocomotiveId = locomotive.id;
            Location = location;
            LocationString = Graph.Shared.LocationToString(location);
            CoupleToCarId = coupleToCarId;
            ConnectAirOnCouple = Loader.Settings.ConnectAirByDefault;
            ReleaseHandbrakesOnCouple = Loader.Settings.ReleaseHandbrakesByDefault;
            ApplyHandbrakesOnUncouple = Loader.Settings.ApplyHandbrakesByDefault;
            BleedAirOnUncouple = Loader.Settings.BleedAirByDefault;
            AreaName = OpsController.Shared.ClosestAreaForGamePosition(Location.GetPosition()).name;
        }

        public enum WaitType
        {
            Duration,
            SpecificTime
        }
        public enum PostCoupleCutType
        {
            Take,
            Leave
        }
        public enum TodayOrTomorrow
        {
            Today,
            Tomorrow
        }
        [JsonProperty]
        public string Id { get; private set; } = Guid.NewGuid().ToString();

        [JsonProperty]
        public string LocomotiveId { get; private set; }

        [JsonIgnore]
        public Car Locomotive
        {
            get
            {
                return Locomotive;
            }
            set
            {
                Locomotive = value;
                LocomotiveId = value?.id ?? "";
            }
        }

        [JsonProperty]
        public string LocationString { get; private set; }

        [JsonIgnore]
        public Location Location { get; internal set; }

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
        public bool CountUncoupledFromNearestToWaypoint { get; set; } = true;
        public PostCoupleCutType TakeOrLeaveCut { get; set; } = PostCoupleCutType.Leave;
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
        public string TimetableSymbol { get; set; }

        public bool WillWait { get; set; }
        public bool CurrentlyWaiting { get; set; }
        public WaitType DurationOrSpecificTime { get; set; } = WaitType.Duration;
        public string WaitUntilTimeString { get; set; }
        public TodayOrTomorrow WaitUntilDay { get; set; } = TodayOrTomorrow.Today;
        public int WaitForDurationMinutes { get; set; }
        public double WaitUntilGameTotalSeconds { get; set; }

        public bool IsValid()
        {
            return TryResolveLocation(out Location loc);
        }

        public void Load()
        {
            TryResolveLocation(out Location loc);
            TryResolveLocomotive(out Car loco);
        }

        public bool TryResolveLocomotive(out Car loco)
        {
            // loco is null if false
            if (TrainController.Shared.TryGetCarForId(LocomotiveId, out loco))
            {
                Loader.LogDebug($"Loaded locomotive {loco.Ident} for ManagedWaypoint");
                Locomotive = loco;
            }
            else
            {
                Loader.LogDebug($"Failed to resolve locomotive {LocomotiveId} for waypoint {Id}");
            }
            return loco != null;
        }

        public bool TryResolveLocation(out Location loc)
        {
            if (Location != null)
            {
                loc = Location;
                return true;
            }
            try
            {
                loc = Graph.Shared.ResolveLocationString(LocationString);
                Location = loc;
                AreaName = OpsController.Shared.ClosestAreaForGamePosition(loc.GetPosition()).name;
                Loader.LogDebug($"Loaded location {Location} with area {AreaName} for waypoint {Id}");
                return true;
            }
            catch (Exception e)
            {
                Loader.LogDebug($"Failed to resolve location string {LocationString}: {e}");
                loc = default;
                return false;
            }
        }

        public void SetWaitUntilByMinutes(int inputMinutesAfterMidnight, out GameDateTime waitUntilTime)
        {
            GameDateTime currentTime = TimeWeather.Now;
            int day = WaitUntilDay == TodayOrTomorrow.Today ? currentTime.Day : currentTime.Day + 1;
            waitUntilTime = new GameDateTime(day, 0).AddingMinutes(inputMinutesAfterMidnight);
            WaitUntilGameTotalSeconds = waitUntilTime.TotalSeconds;
        }



        public void ClearWaiting()
        {
            WillWait = false;
            CurrentlyWaiting = false;
            DurationOrSpecificTime = WaitType.Duration;
            WaitUntilTimeString = "";
            WaitUntilDay = TodayOrTomorrow.Today;
            WaitForDurationMinutes = 0;
            WaitUntilGameTotalSeconds = 0;
        }

        public ManagedWaypoint CopyForRoute(Car loco = null)
        {
            string serializedWaypoint = JsonConvert.SerializeObject(this);
            ManagedWaypoint copy = JsonConvert.DeserializeObject<ManagedWaypoint>(serializedWaypoint);
            copy.Id = Guid.NewGuid().ToString();
            copy.Locomotive = loco;
            copy.Location = Location;
            return copy;
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
