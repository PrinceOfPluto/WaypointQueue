using Game;
using MessagePack;
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
    [MessagePackObject(false)]
    public class ManagedWaypoint
    {
        // Used for JSON deserialization
        public ManagedWaypoint() { }

        [Key(51)]
        [JsonProperty]
        public int? Version { get; set; }

        public ManagedWaypoint(BaseLocomotive locomotive, Location location, string coupleToCarId = "")
        {
            Version = 1;
            LocomotiveId = locomotive?.id ?? null;
            LocationString = Graph.Shared.LocationToString(location);
            CoupleToCarId = coupleToCarId;
            ConnectAirOnCouple = Loader.Settings.ConnectAirByDefault;
            ReleaseHandbrakesOnCouple = Loader.Settings.ReleaseHandbrakesByDefault;
            ApplyHandbrakesOnUncouple = Loader.Settings.ApplyHandbrakesByDefault;
            BleedAirOnUncouple = Loader.Settings.BleedAirByDefault;
            WillLimitPassingSpeed = !Loader.Settings.DoNotLimitPassingSpeedDefault;

            if (Loader.Settings.AdvancedSettings.EnableThenUncoupleByDefault)
            {
                UncouplingMode = (UncoupleMode)Loader.Settings.DefaultUncouplingMode;
            }

            if (Loader.Settings.AdvancedSettings.ShowPostCouplingCutByDefault && !String.IsNullOrEmpty(coupleToCarId))
            {
                SetDefaultPostCouplingCut();
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
        [Key(0)]
        public string Id { get; private set; } = Guid.NewGuid().ToString();

        [JsonProperty]
        [Key(1)]
        public string LocomotiveId { get; private set; }

        [JsonIgnore]
        [IgnoreMember]
        public virtual BaseLocomotive Locomotive
        {
            get
            {
                TrainController.Shared.TryGetCarForId(LocomotiveId, out Car carLoco);
                if (carLoco is BaseLocomotive loco)
                {
                    return loco;
                }
                Loader.LogError($"Failed to resolve locomotive {LocomotiveId} for waypoint state entry");
                return null;
            }
        }

        [JsonProperty]
        [Key(2)]
        public string LocationString { get; private set; }

        [IgnoreMember]
        private Location _location;

        [JsonIgnore]
        [IgnoreMember]
        public virtual Location Location
        {
            get
            {
                if (_location != Location.Invalid)
                {
                    return _location;
                }
                try
                {
                    Location loc = Graph.Shared.ResolveLocationString(LocationString);
                    _location = loc.Clamped();
                    return _location;
                }
                catch (Exception e)
                {
                    Loader.LogError($"Failed to resolve location string {LocationString}: {e}");
                    _location = Location.Invalid;
                    return _location;
                }
            }
        }

        [JsonProperty]
        [Key(3)]
        public string CoupleToCarId { get; internal set; }

        [JsonIgnore]
        [IgnoreMember]
        public Car CoupleToCar { get; internal set; }

        [JsonIgnore]
        [IgnoreMember]
        public bool IsCoupling
        {
            get
            {
                return !string.IsNullOrEmpty(CoupleToCarId);
            }
        }

        [Key(4)]
        public bool ConnectAirOnCouple { get; set; }
        [Key(5)]
        public bool ReleaseHandbrakesOnCouple { get; set; }
        [Key(6)]
        public bool HasResolvedBrakeSystemOnCouple { get; set; }
        [Key(7)]
        public bool ApplyHandbrakesOnUncouple { get; set; }
        [Key(8)]
        public bool BleedAirOnUncouple { get; set; }
        [Key(9)]
        public virtual int NumberOfCarsToCut { get; set; }
        [Key(10)]
        public virtual bool CountUncoupledFromNearestToWaypoint { get; set; } = true;

        [JsonProperty("TakeOrLeaveCut")]
        [Key(11)]
        private PostCoupleCutType _postCouplingCutMode = PostCoupleCutType.None;

        [JsonIgnore]
        [IgnoreMember]
        public virtual PostCoupleCutType PostCouplingCutMode
        {
            get { return _postCouplingCutMode; }
            set
            {
                _postCouplingCutMode = value;

                if (value == PostCoupleCutType.None)
                {
                    UncouplingMode = UncoupleMode.None;
                }
            }
        }

        [Key(12)]
        public bool TakeUncoupledCarsAsActiveCut { get; set; }

        [JsonIgnore]
        [IgnoreMember]
        public bool CanRefuelNearby
        {
            get { return RefuelPoint != null && RefuelLoadName != null && RefuelLoadName.Length > 0; }
        }

        [JsonIgnore]
        [IgnoreMember]
        public Vector3 RefuelPoint
        {
            get
            {
                return SerializableRefuelPoint.ToVector3();
            }
        }

        [JsonProperty]
        [Key(13)]
        public SerializableVector3 SerializableRefuelPoint { get; set; }

        [Key(14)]
        public string RefuelIndustryId { get; set; }
        [Key(15)]
        public string RefuelLoadName { get; set; }
        [Key(16)]
        public float RefuelMaxCapacity { get; set; }
        [Key(17)]
        public bool WillRefuel { get; set; }
        [Key(18)]
        public bool CurrentlyRefueling { get; set; }
        [Key(19)]
        public int RefuelingSpeedLimit { get; set; } = 5;
        [Key(20)]
        public int MaxSpeedAfterRefueling { get; set; }
        [Key(21)]
        public bool RefuelLoaderAnimated { get; set; }

        [IgnoreMember]
        private string _areaName = string.Empty;
        [IgnoreMember]
        public string AreaName
        {
            get
            {
                if (_areaName != string.Empty)
                {
                    return _areaName;
                }
                _areaName = OpsController.Shared.ClosestAreaForGamePosition(Location.GetPosition()).name;
                return _areaName;
            }
        }
        [Key(22)]
        public string TimetableSymbol { get; set; }

        [Key(23)]
        public bool WillWait { get; set; }
        [Key(24)]
        public bool CurrentlyWaiting { get; set; }
        [Key(52)]
        public bool WaitingWasSkipped { get; set; } = false;
        [Key(25)]
        public WaitType DurationOrSpecificTime { get; set; } = WaitType.Duration;
        [Key(26)]
        public string WaitUntilTimeString { get; set; }
        [Key(27)]
        public TodayOrTomorrow WaitUntilDay { get; set; } = TodayOrTomorrow.Today;
        [Key(28)]
        public int WaitForDurationMinutes { get; set; }
        [Key(29)]
        public double WaitUntilGameTotalSeconds { get; set; }
        [Key(30)]
        public bool StopAtWaypoint { get; set; } = true;
        [Key(31)]
        public bool WillLimitPassingSpeed { get; set; } = true;
        [Key(32)]
        public int WaypointTargetSpeed { get; set; } = 0;
        [Key(33)]
        public bool WillChangeMaxSpeed { get; set; } = false;
        [Key(34)]
        public int MaxSpeedForChange { get; set; }


        [JsonProperty("CouplingSearchMode")]
        [Key(35)]
        private CoupleSearchMode _couplingSearchMode = CoupleSearchMode.None;

        [JsonIgnore]
        [IgnoreMember]
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

                if (value != CoupleSearchMode.None && PostCouplingCutMode == PostCoupleCutType.None && Loader.Settings.AdvancedSettings.ShowPostCouplingCutByDefault)
                {
                    SetDefaultPostCouplingCut();
                }
            }
        }

        public void RemovePostCouplingCut()
        {
            PostCouplingCutMode = PostCoupleCutType.None;
            UncouplingMode = UncoupleMode.None;
        }

        [JsonProperty("UncouplingMode")]
        [Key(36)]
        private UncoupleMode _uncouplingMode = UncoupleMode.None;

        [JsonIgnore]
        [IgnoreMember]
        public virtual UncoupleMode UncouplingMode
        {
            get { return _uncouplingMode; }
            set
            {
                _uncouplingMode = value;
            }
        }

        [JsonIgnore]
        [IgnoreMember]
        public bool WillUncoupleByCount { get { return UncouplingMode == UncoupleMode.ByCount; } }
        [JsonIgnore]
        [IgnoreMember]
        public bool WillUncoupleByDestination { get { return WillUncoupleByDestinationTrack || WillUncoupleByDestinationIndustry || WillUncoupleByDestinationArea; } }
        [JsonIgnore]
        [IgnoreMember]
        public virtual bool WillUncoupleByNoDestination => UncoupleDestinationId == WaypointResolver.NoDestinationString;
        [JsonIgnore]
        [IgnoreMember]
        public virtual bool WillUncoupleByDestinationTrack { get { return UncouplingMode == UncoupleMode.ByDestinationTrack; } }
        [JsonIgnore]
        [IgnoreMember]
        public virtual bool WillUncoupleByDestinationIndustry { get { return UncouplingMode == UncoupleMode.ByDestinationIndustry; } }
        [JsonIgnore]
        [IgnoreMember]
        public virtual bool WillUncoupleByDestinationArea { get { return UncouplingMode == UncoupleMode.ByDestinationArea; } }
        [JsonIgnore]
        [IgnoreMember]
        public bool WillUncoupleBySpecificCar { get { return UncouplingMode == UncoupleMode.BySpecificCar; } }
        [JsonIgnore]
        [IgnoreMember]
        public bool WillUncoupleAllExceptLocomotives { get { return UncouplingMode == UncoupleMode.AllExceptLocomotives; } }

        [JsonIgnore]
        [IgnoreMember]
        public bool WillSeekNearestCoupling { get { return CouplingSearchMode == CoupleSearchMode.Nearest; } }
        [JsonIgnore]
        [IgnoreMember]
        public bool WillSeekSpecificCarCoupling { get { return CouplingSearchMode == CoupleSearchMode.SpecificCar; } }

        [JsonIgnore]
        [IgnoreMember]
        public bool WillPostCoupleCutPickup => HasAnyCouplingOrders && PostCouplingCutMode == PostCoupleCutType.Pickup;
        [JsonIgnore]
        [IgnoreMember]
        public bool WillPostCoupleCutDropoff => HasAnyCouplingOrders && PostCouplingCutMode == PostCoupleCutType.Dropoff;

        [Key(37)]
        public bool CurrentlyCouplingNearby { get; set; }
        [Key(38)]
        public bool CurrentlyCouplingSpecificCar { get; set; }

        [JsonIgnore]
        [IgnoreMember]
        public bool HasAnyCouplingOrders { get { return IsCoupling || WillSeekNearestCoupling || WillSeekSpecificCarCoupling; } }
        [JsonIgnore]
        [IgnoreMember]
        public bool HasAnyUncouplingOrders { get { return UncouplingMode != UncoupleMode.None; } }
        [JsonIgnore]
        [IgnoreMember]
        public bool HasAnyCutOrders => HasAnyUncouplingOrders || (HasAnyCouplingOrders && HasAnyPostCouplingCutOrders);
        [JsonIgnore]
        [IgnoreMember]
        public bool HasAnyPostCouplingCutOrders => HasAnyCouplingOrders && PostCouplingCutMode != PostCoupleCutType.None;

        [Obsolete("Use CouplingSearchMode instead")]
        [JsonProperty]
        [Key(39)]
        private bool SeekNearbyCoupling { get; set; } = false;
        [Key(40)]
        public bool OnlySeekNearbyOnTrackAhead { get; set; } = true;

        [Key(41)]
        public string CouplingSearchText { get; set; } = "";
        [JsonIgnore]
        [IgnoreMember]
        public Car CouplingSearchResultCar { get; set; }

        [Key(42)]
        public string UncouplingSearchText { get; set; } = "";
        [JsonIgnore]
        [IgnoreMember]
        public Car UncouplingSearchResultCar { get; set; }
        [Key(53)]
        public string DestinationSearchText { get; set; } = "";
        [Key(43)]
        public virtual string UncoupleDestinationId { get; set; } = "";
        [Key(44)]
        public virtual bool ExcludeMatchingCarsFromCut { get; set; }

        [Key(45)]
        public bool MoveTrainPastWaypoint { get; set; }
        [Key(46)]
        public bool CurrentlyWaitingBeforeCutting { get; set; }

        [Key(47)]
        public string StatusLabel { get; set; } = "Inactive";
        [Key(48)]
        public string Name { get; set; } = string.Empty;
        [Key(49)]
        public string Notes { get; set; } = string.Empty;

        [Key(50)]
        public List<WaypointError> Errors { get; set; } = [];

        private void SetDefaultPostCouplingCut()
        {
            PostCouplingCutMode = (PostCoupleCutType)Loader.Settings.DefaultPostCouplingCutMode;
            UncouplingMode = (UncoupleMode)Loader.Settings.DefaultPostCouplingCutUncouplingMode;
        }

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

        internal void HandleMigration()
        {
            if (!Version.HasValue)
            {
                MigrateFromVersion0To1();
            }
        }

        private void MigrateFromVersion0To1()
        {
            Loader.LogDebug($"Migrating waypoint {Id} from version 0 to version 1");
            Version = 1;
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
            if (CurrentlyWaiting)
            {
                WaitingWasSkipped = true;
            }
            Loader.LogDebug($"Clear waiting for {Locomotive.Ident} at {LocationString}");
            WillWait = false;
            CurrentlyWaiting = false;
            DurationOrSpecificTime = WaitType.Duration;
            WaitUntilTimeString = "";
            WaitUntilDay = TodayOrTomorrow.Today;
            WaitForDurationMinutes = 0;
            WaitUntilGameTotalSeconds = 0;
        }

        public void ClearRefueling()
        {
            WillRefuel = false;
            RefuelLoadName = null;
            RefuelIndustryId = null;
        }

        public void ClearCoupling()
        {
            CoupleToCarId = null;
            CoupleToCar = null;
            CouplingSearchMode = CoupleSearchMode.None;
            CouplingSearchText = String.Empty;
            PostCouplingCutMode = PostCoupleCutType.None;
        }

        public void OverwriteLocation(Location loc)
        {
            Location clampedLocation = loc.Clamped();
            _location = clampedLocation;
            LocationString = Graph.Shared.LocationToString(clampedLocation);
        }

        public bool TryCopyForRoute(out ManagedWaypoint copy, BaseLocomotive loco = null)
        {
            if (IsValid())
            {
                string serializedWaypoint = JsonConvert.SerializeObject(this);
                copy = JsonConvert.DeserializeObject<ManagedWaypoint>(serializedWaypoint);
                copy.Id = Guid.NewGuid().ToString();
                copy.LocomotiveId = loco?.id ?? null;
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

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
            {
                return false;
            }

            var other = (ManagedWaypoint)obj;

            return Id == other.Id &&
                   LocomotiveId == other.LocomotiveId &&
                   LocationString == other.LocationString &&
                   CoupleToCarId == other.CoupleToCarId &&
                   ConnectAirOnCouple == other.ConnectAirOnCouple &&
                   ReleaseHandbrakesOnCouple == other.ReleaseHandbrakesOnCouple &&
                   HasResolvedBrakeSystemOnCouple == other.HasResolvedBrakeSystemOnCouple &&
                   ApplyHandbrakesOnUncouple == other.ApplyHandbrakesOnUncouple &&
                   BleedAirOnUncouple == other.BleedAirOnUncouple &&
                   NumberOfCarsToCut == other.NumberOfCarsToCut &&
                   CountUncoupledFromNearestToWaypoint == other.CountUncoupledFromNearestToWaypoint &&
                   PostCouplingCutMode == other.PostCouplingCutMode &&
                   TakeUncoupledCarsAsActiveCut == other.TakeUncoupledCarsAsActiveCut &&
                   SerializableRefuelPoint.Equals(other.SerializableRefuelPoint) &&
                   RefuelIndustryId == other.RefuelIndustryId &&
                   RefuelLoadName == other.RefuelLoadName &&
                   RefuelMaxCapacity.Equals(other.RefuelMaxCapacity) &&
                   WillRefuel == other.WillRefuel &&
                   CurrentlyRefueling == other.CurrentlyRefueling &&
                   RefuelingSpeedLimit == other.RefuelingSpeedLimit &&
                   MaxSpeedAfterRefueling == other.MaxSpeedAfterRefueling &&
                   RefuelLoaderAnimated == other.RefuelLoaderAnimated &&
                   TimetableSymbol == other.TimetableSymbol &&
                   WillWait == other.WillWait &&
                   CurrentlyWaiting == other.CurrentlyWaiting &&
                   WaitingWasSkipped == other.WaitingWasSkipped &&
                   DurationOrSpecificTime == other.DurationOrSpecificTime &&
                   WaitUntilTimeString == other.WaitUntilTimeString &&
                   WaitUntilDay == other.WaitUntilDay &&
                   WaitForDurationMinutes == other.WaitForDurationMinutes &&
                   WaitUntilGameTotalSeconds == other.WaitUntilGameTotalSeconds &&
                   StopAtWaypoint == other.StopAtWaypoint &&
                   WillLimitPassingSpeed == other.WillLimitPassingSpeed &&
                   WaypointTargetSpeed == other.WaypointTargetSpeed &&
                   WillChangeMaxSpeed == other.WillChangeMaxSpeed &&
                   MaxSpeedForChange == other.MaxSpeedForChange &&
                   CouplingSearchMode == other.CouplingSearchMode &&
                   UncouplingMode == other.UncouplingMode &&
                   CurrentlyCouplingNearby == other.CurrentlyCouplingNearby &&
                   CurrentlyCouplingSpecificCar == other.CurrentlyCouplingSpecificCar &&
                   OnlySeekNearbyOnTrackAhead == other.OnlySeekNearbyOnTrackAhead &&
                   CouplingSearchText == other.CouplingSearchText &&
                   UncouplingSearchText == other.UncouplingSearchText &&
                   DestinationSearchText == other.DestinationSearchText &&
                   UncoupleDestinationId == other.UncoupleDestinationId &&
                   ExcludeMatchingCarsFromCut == other.ExcludeMatchingCarsFromCut &&
                   MoveTrainPastWaypoint == other.MoveTrainPastWaypoint &&
                   CurrentlyWaitingBeforeCutting == other.CurrentlyWaitingBeforeCutting &&
                   StatusLabel == other.StatusLabel &&
                   Name == other.Name &&
                   Notes == other.Notes &&
                   Errors.SequenceEqual(other.Errors);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }

    [Serializable]
    [MessagePackObject]
    public struct SerializableVector3
    {
        public SerializableVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        [Key(0)]
        public float X { get; set; }
        [Key(1)]
        public float Y { get; set; }
        [Key(2)]
        public float Z { get; set; }

        public Vector3 ToVector3()
        {
            return new Vector3(X, Y, Z);
        }
    }
}
