using Game;
using Model;
using Model.Ops;
using Newtonsoft.Json;
using System;
using Track;
using UI.EngineControls;
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
            ShowPostCouplingCut = Loader.Settings.ShowPostCouplingCutByDefault;
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
        public enum UncoupleMode
        {
            All = 0,
            ByCount = 1,
            ByDestination = 2,
            None = 3
        }

        public enum UncoupleAllDirection
        {
            Aft = 0,
            Fore = 1
        }
        [JsonProperty]
        public string Id { get; private set; } = Guid.NewGuid().ToString();

        [JsonProperty]
        public string LocomotiveId { get; private set; }

        [JsonIgnore]
        public Car Locomotive { get; private set; }

        [JsonProperty]
        public string LocationString { get; private set; }

        [JsonIgnore]
        public Location Location { get; internal set; }

        [JsonProperty]
        public string CoupleToCarId { get; internal set; }
        [JsonIgnore]
        public Car CoupleToCar { get; internal set; }

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
                if (IsCoupling) return false;

                switch (UncoupleByMode)
                {
                    case UncoupleMode.ByCount:
                        // only uncouple if count > 0
                        return NumberOfCarsToCut > 0;

                    case UncoupleMode.ByDestination:
                        // Only uncouple if a destination is chosen
                        return !string.IsNullOrEmpty(UncoupleDestinationId);

                    case UncoupleMode.All:
                        // "All" mode does not depend on a car count; selecting this mode
                        // means we intend to uncouple at this waypoint.
                        return true;

                    case UncoupleMode.None:
                    default:
                        return false;
                }
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
        public bool ShowPostCouplingCut { get; set; }
        public UncoupleMode UncoupleByMode { get; set; } = UncoupleMode.None;
        public UncoupleAllDirection UncoupleAllDirectionSide { get; set; } = UncoupleAllDirection.Aft;

        public string UncoupleDestinationId { get; set; }
        public bool KeepDestinationString { get; set; } = false;

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
        public int RefuelingSpeedLimit { get; set; } = 5;
        public int MaxSpeedAfterRefueling { get; set; }
        public bool RefuelLoaderAnimated { get; set; }
        public string AreaName { get; set; }
        public string TimetableSymbol { get; set; }

        public bool WillWait { get; set; }
        public bool CurrentlyWaiting { get; set; }
        public WaitType DurationOrSpecificTime { get; set; } = WaitType.Duration;
        public string WaitUntilTimeString { get; set; }
        public TodayOrTomorrow WaitUntilDay { get; set; } = TodayOrTomorrow.Today;
        public int WaitForDurationMinutes { get; set; }
        public double WaitUntilGameTotalSeconds { get; set; }
        public bool StopAtWaypoint { get; set; } = true;
        public int WaypointTargetSpeed { get; set; } = 0;
        public bool SeekNearbyCoupling { get; set; }
        public bool CurrentlyCouplingNearby { get; set; }
        public bool MoveTrainPastWaypoint { get; set; }
        public bool CurrentlyWaitingBeforeCutting { get; set; }

        [JsonIgnore]
        public float SecondsSpentWaitingBeforeCut { get; set; }
        public string StatusLabel { get; set; } = "Inactive";

        public bool IsValid()
        {
            return TryResolveLocation(out Location loc);
        }

        public bool IsValidWithLoco()
        {
            return TryResolveLocation(out Location loc) && TryResolveLocomotive(out Car loco);
        }

        public void Load()
        {
            TryResolveLocation(out Location loc);
            TryResolveLocomotive(out Car loco);

            if (IsCoupling && NumberOfCarsToCut > 0)
            {
                ShowPostCouplingCut = true;
            }
        }

        public bool TryResolveLocomotive(out Car loco)
        {
            
            if (TrainController.Shared.TryGetCarForId(LocomotiveId, out loco))
            {
                Loader.LogDebug($"Loaded locomotive {loco.Ident} for ManagedWaypoint");
                Locomotive = loco;
            }
            else
            {
                Loader.LogError($"Failed to resolve locomotive {LocomotiveId} for waypoint {Id}");
            }
            return loco != null;
        }

        public bool TryResolveLocation(out Location loc)
        {
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
                Loader.LogError($"Failed to resolve location string {LocationString}: {e}");
                loc = default;
                return false;
            }
        }

        public bool TryResolveCoupleToCar(out Car car)
        {
            if (String.IsNullOrEmpty(CoupleToCarId))
            {
                car = null;
                return false;
            }

            try
            {
                TrainController.Shared.TryGetCarForId(CoupleToCarId, out car);
                CoupleToCar = car;
                return true;
            }
            catch (Exception e)
            {
                Loader.LogError($"Failed to resolve car id {CoupleToCarId}: {e}");
            }
            car = null;
            return false;
        }
        public void SetTargetSpeedToOrdersMax()
        {
            if (Locomotive != null)
            {
                AutoEngineerOrdersHelper ordersHelper = WaypointQueueController.Shared.GetOrdersHelper(Locomotive);
                WaypointTargetSpeed = ordersHelper.Orders.MaxSpeedMph;
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
            Loader.LogDebug($"Clear waiting for {Locomotive.Ident} at {LocationString}");
            WillWait = false;
            CurrentlyWaiting = false;
            DurationOrSpecificTime = WaitType.Duration;
            WaitUntilTimeString = "";
            WaitUntilDay = TodayOrTomorrow.Today;
            WaitForDurationMinutes = 0;
            WaitUntilGameTotalSeconds = 0;
        }

        public void OverwriteLocation(Location loc)
        {
            Location = loc;
            LocationString = Graph.Shared.LocationToString(loc);
        }

        public bool TryCopyForRoute(out ManagedWaypoint copy, Car loco = null)
        {
            if (IsValid())
            {
                string serializedWaypoint = JsonConvert.SerializeObject(this);
                copy = JsonConvert.DeserializeObject<ManagedWaypoint>(serializedWaypoint);
                copy.Id = Guid.NewGuid().ToString();
                copy.Locomotive = loco;
                copy.LocomotiveId = loco?.id ?? null;
                copy.Location = Location;
                return true;
            }
            else
            {
                copy = null;
                return false;
            }
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
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
