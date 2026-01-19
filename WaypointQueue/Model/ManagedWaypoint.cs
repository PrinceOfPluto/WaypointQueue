using Game;
using Model;
using Model.Ops;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Track;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.UUM;
using static Model.Ops.OpsController;

namespace WaypointQueue
{
    public class ManagedWaypoint
    {
        // Used for JSON deserialization
        public ManagedWaypoint() { }

        public int? Version { get; set; }

        public ManagedWaypoint(Car locomotive, Location location, string coupleToCarId = "")
        {
            Version = 1;
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
            WillLimitPassingSpeed = !Loader.Settings.DoNotLimitPassingSpeedDefault;

            if (Loader.Settings.ShowPostCouplingCutByDefault)
            {
                PostCouplingCutMode = (PostCoupleCutType)Loader.Settings.DefaultPostCouplingCutMode;
            }

            if (Loader.Settings.EnableThenUncoupleByDefault)
            {
                UncouplingMode = (UncoupleMode)Loader.Settings.DefaultUncouplingMode;
            }
        }

        public enum WaitType
        {
            Duration,
            SpecificTime
        }
        public enum PostCoupleCutType
        {
            Pickup,
            Dropoff,
            None
        }
        public enum TodayOrTomorrow
        {
            Today,
            Tomorrow
        }

        public enum CoupleSearchMode
        {
            None,
            Nearest,
            SpecificCar
        }

        public enum UncoupleMode
        {
            None,
            ByCount,
            ByDestinationArea,
            ByDestinationIndustry,
            ByDestinationTrack,
            BySpecificCar,
            AllExceptLocomotives
        }

        [JsonProperty]
        public string Id { get; private set; } = Guid.NewGuid().ToString();

        [JsonProperty]
        public string LocomotiveId { get; private set; }

        [JsonIgnore]
        public virtual Car Locomotive { get; private set; }

        [JsonProperty]
        public string LocationString { get; private set; }

        [JsonIgnore]
        public virtual Location Location { get; internal set; }

        [JsonProperty]
        public string CoupleToCarId { get; internal set; }

        [JsonIgnore]
        public Car CoupleToCar { get; internal set; }

        [JsonIgnore]
        public bool IsCoupling
        {
            get
            {
                return !string.IsNullOrEmpty(CoupleToCarId);
            }
        }

        public bool ConnectAirOnCouple { get; set; }
        public bool ReleaseHandbrakesOnCouple { get; set; }
        public bool HasResolvedBrakeSystemOnCouple { get; set; }
        public bool ApplyHandbrakesOnUncouple { get; set; }
        public bool BleedAirOnUncouple { get; set; }

        public virtual int NumberOfCarsToCut { get; set; }
        public virtual bool CountUncoupledFromNearestToWaypoint { get; set; } = true;

        [JsonProperty("TakeOrLeaveCut")]
        public virtual PostCoupleCutType PostCouplingCutMode { get; set; } = PostCoupleCutType.None;
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
        public bool WillLimitPassingSpeed { get; set; } = true;
        public int WaypointTargetSpeed { get; set; } = 0;
        public bool WillChangeMaxSpeed { get; set; } = false;
        public int MaxSpeedForChange { get; set; }


        [JsonProperty("CouplingSearchMode")]
        private CoupleSearchMode _couplingSearchMode = CoupleSearchMode.None;

        [JsonIgnore]
        public virtual CoupleSearchMode CouplingSearchMode
        {
            get { return _couplingSearchMode; }
            set
            {
                _couplingSearchMode = value;
                CoupleToCar = null;
                CoupleToCarId = null;
                CouplingSearchText = "";
                CouplingSearchResultCar = null;
            }
        }

        [JsonProperty("UncouplingMode")]
        private UncoupleMode _uncouplingMode = UncoupleMode.None;

        [JsonIgnore]
        public virtual UncoupleMode UncouplingMode
        {
            get { return _uncouplingMode; }
            set
            {
                _uncouplingMode = value;
            }
        }

        [JsonIgnore]
        public bool WillUncoupleByCount { get { return UncouplingMode == UncoupleMode.ByCount; } }
        [JsonIgnore]
        public bool WillUncoupleByDestination { get { return WillUncoupleByDestinationTrack || WillUncoupleByDestinationIndustry || WillUncoupleByDestinationArea; } }
        [JsonIgnore]
        public virtual bool WillUncoupleByNoDestination => UncoupleDestinationId == WaypointResolver.NoDestinationString;
        [JsonIgnore]
        public virtual bool WillUncoupleByDestinationTrack { get { return UncouplingMode == UncoupleMode.ByDestinationTrack; } }
        [JsonIgnore]
        public virtual bool WillUncoupleByDestinationIndustry { get { return UncouplingMode == UncoupleMode.ByDestinationIndustry; } }
        [JsonIgnore]
        public virtual bool WillUncoupleByDestinationArea { get { return UncouplingMode == UncoupleMode.ByDestinationArea; } }
        [JsonIgnore]
        public bool WillUncoupleBySpecificCar { get { return UncouplingMode == UncoupleMode.BySpecificCar; } }
        [JsonIgnore]
        public bool WillUncoupleAllExceptLocomotives { get { return UncouplingMode == UncoupleMode.AllExceptLocomotives; } }

        [JsonIgnore]
        public bool WillSeekNearestCoupling { get { return CouplingSearchMode == CoupleSearchMode.Nearest; } }
        [JsonIgnore]
        public bool WillSeekSpecificCarCoupling { get { return CouplingSearchMode == CoupleSearchMode.SpecificCar; } }

        [JsonIgnore]
        public bool WillPostCoupleCutPickup => HasAnyCouplingOrders && PostCouplingCutMode == PostCoupleCutType.Pickup;
        [JsonIgnore]
        public bool WillPostCoupleCutDropoff => HasAnyCouplingOrders && PostCouplingCutMode == PostCoupleCutType.Dropoff;

        public bool CurrentlyCouplingNearby { get; set; }
        public bool CurrentlyCouplingSpecificCar { get; set; }

        [JsonIgnore]
        public bool HasAnyCouplingOrders { get { return IsCoupling || WillSeekNearestCoupling || WillSeekSpecificCarCoupling; } }
        [JsonIgnore]
        public bool HasAnyUncouplingOrders { get { return UncouplingMode != UncoupleMode.None; } }
        [JsonIgnore]
        public bool HasAnyCutOrders => HasAnyUncouplingOrders || (HasAnyCouplingOrders && HasAnyPostCouplingCutOrders);
        [JsonIgnore]
        public bool HasAnyPostCouplingCutOrders => HasAnyCouplingOrders && PostCouplingCutMode != PostCoupleCutType.None;

        [Obsolete("Use CouplingSearchMode instead")]
        [JsonProperty]
        private bool SeekNearbyCoupling { get; set; } = false;
        public bool OnlySeekNearbyOnTrackAhead { get; set; } = true;

        public string CouplingSearchText { get; set; } = "";
        [JsonIgnore]
        public Car CouplingSearchResultCar { get; set; }

        public string UncouplingSearchText { get; set; } = "";
        [JsonIgnore]
        public Car UncouplingSearchResultCar { get; set; }

        [JsonIgnore]
        public string DestinationSearchText { get; set; } = "";
        public virtual string UncoupleDestinationId { get; set; } = "";
        public virtual bool ExcludeMatchingCarsFromCut { get; set; }

        public bool MoveTrainPastWaypoint { get; set; }
        public bool CurrentlyWaitingBeforeCutting { get; set; }

        [JsonIgnore]
        public float SecondsSpentWaitingBeforeCut { get; set; }
        public string StatusLabel { get; set; } = "Inactive";
        public string Name { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        public List<WaypointError> Errors { get; set; } = [];

        public bool IsValid()
        {
            return TryResolveLocation(out Location loc);
        }

        public bool IsValidWithLoco()
        {
            return TryResolveLocation(out Location loc) && TryResolveLocomotive(out Car loco);
        }

        public void LoadForRoute()
        {
            TryResolveLocation(out Location _);

            TryResolveCouplingSearchText(out Car _);
        }

        [OnDeserialized]
        internal void OnDeserialized(StreamingContext _)
        {
            if (!Version.HasValue)
            {
                MigrateFromVersion0To1();
            }
        }

        private void MigrateFromVersion0To1()
        {
            Loader.LogDebug($"Migrating waypoint {Id} from version 0 to version 1");
#pragma warning disable 0618
            // below is the only area where this obsolete field should be used
            if (SeekNearbyCoupling)
            {
                Loader.LogDebug($"Setting waypoint id {Id} CouplingSearchMode to Nearest as migration from SeekNearbyCoupling");
                CouplingSearchMode = CoupleSearchMode.Nearest;
                SeekNearbyCoupling = false; // set to false so it doesn't get saved as true in the future
            }
#pragma warning restore 0618

            if (HasAnyCouplingOrders && NumberOfCarsToCut == 0 && (PostCouplingCutMode == PostCoupleCutType.Pickup || PostCouplingCutMode == PostCoupleCutType.Dropoff))
            {
                Loader.LogDebug($"Setting waypoint id {Id} PostCouplingCutMode to none since there are coupling orders with zero cars to cut for a pickup or dropoff");
                PostCouplingCutMode = PostCoupleCutType.None;
            }

            if (!HasAnyCouplingOrders && UncouplingMode == UncoupleMode.None && NumberOfCarsToCut > 0)
            {
                Loader.LogDebug($"Setting waypoint id {Id} UncouplingMode to ByCount since there are {NumberOfCarsToCut} cars to cut");
                UncouplingMode = UncoupleMode.ByCount;
            }
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
                Loader.LogError($"Failed to resolve locomotive {LocomotiveId} for waypoint {Id}");
            }
            return loco != null;
        }

        public bool TryResolveLocation(out Location loc)
        {
            try
            {
                loc = Graph.Shared.ResolveLocationString(LocationString);
                Location = loc.Clamped();
                AreaName = OpsController.Shared.ClosestAreaForGamePosition(loc.GetPosition()).name;
                Loader.LogDebug($"Loaded clamped location {Location} with area {AreaName} for waypoint {Id}");
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

        public bool TryResolveCouplingSearchText(out Car car)
        {
            if (String.IsNullOrEmpty(CouplingSearchText))
            {
                //Loader.LogDebug($"Coupling search text is empty");
                car = null;
                return false;
            }

            // Check if we already have it
            if (CouplingSearchResultCar != null && CouplingSearchResultCar.Ident.ToString() == CouplingSearchText)
            {
                //Loader.LogDebug($"Coupling search result car already cached");
                car = CouplingSearchResultCar;
                return true;
            }

            CouplingSearchResultCar = TrainController.Shared.CarForString(CouplingSearchText);
            car = CouplingSearchResultCar;

            if (CouplingSearchResultCar != null)
            {
                CouplingSearchText = CouplingSearchResultCar.Ident.ToString();
            }

            return car != null;
        }

        public bool TryResolveUncouplingSearchText(out Car car)
        {
            if (String.IsNullOrEmpty(UncouplingSearchText))
            {
                //Loader.LogDebug($"Uncoupling search text is empty");
                car = null;
                return false;
            }

            // Check if we already have it
            if (UncouplingSearchResultCar != null && UncouplingSearchResultCar.Ident.ToString() == UncouplingSearchText)
            {
                //Loader.LogDebug($"Uncoupling search result car already cached");
                car = UncouplingSearchResultCar;
                return true;
            }

            UncouplingSearchResultCar = TrainController.Shared.CarForString(UncouplingSearchText);
            car = UncouplingSearchResultCar;

            if (UncouplingSearchResultCar != null)
            {
                UncouplingSearchText = UncouplingSearchResultCar.Ident.ToString();
            }

            return car != null;
        }

        public bool CheckValidUncoupleDestinationId()
        {
            if (string.IsNullOrEmpty(UncoupleDestinationId))
            {
                return true;
            }

            if (WillUncoupleByDestinationTrack)
            {
                return HasValidTrackDestinationId();
            }

            if (WillUncoupleByDestinationIndustry)
            {
                return HasValidIndustryDestinationId();
            }

            if (WillUncoupleByDestinationArea)
            {
                return HasValidAreaDestinationId();
            }
            return true;
        }

        public bool HasValidTrackDestinationId()
        {
            try
            {
                OpsCarPosition destinationMatch = OpsController.Shared.ResolveOpsCarPosition(UncoupleDestinationId);
                return true;
            }
            catch (InvalidOpsCarPositionException)
            {
                Loader.LogError($"Failed to resolve track destination by id {UncoupleDestinationId}  for waypoint id {Id}");
                return false;
            }
        }

        public bool HasValidIndustryDestinationId()
        {
            Industry industryMatch = OpsController.Shared.AllIndustries.Where(i => i.identifier == UncoupleDestinationId).FirstOrDefault();
            if (industryMatch == null)
            {
                Loader.LogError($"Failed to resolve industry by id {UncoupleDestinationId} for waypoint id {Id}");
                return false;
            }
            return true;
        }

        public bool HasValidAreaDestinationId()
        {
            Area areaMatch = OpsController.Shared.Areas.Where(i => i.identifier == UncoupleDestinationId).FirstOrDefault();
            if (areaMatch == null)
            {
                Loader.LogError($"Failed to resolve area by id {UncoupleDestinationId} for waypoint id {Id}");
                return false;
            }
            return true;
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
            Location clampedLocation = loc.Clamped();
            Location = clampedLocation;
            LocationString = Graph.Shared.LocationToString(clampedLocation);
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
                copy.TryResolveCouplingSearchText(out _);
                copy.TryResolveUncouplingSearchText(out _);
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
