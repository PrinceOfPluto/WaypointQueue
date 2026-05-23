using Game;
using KeyValue.Runtime;
using Model;
using Model.Ops;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Track;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.UUM;
using static Model.Ops.OpsController;

namespace WaypointQueue
{
    public class ManagedWaypoint : IStorableProperty
    {
        // Used for JSON deserialization
        public ManagedWaypoint() { }

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
            BottleAirOnUncouple = !Loader.Settings.DoNotBottleAir;

            WillLimitPassingSpeed = !Loader.Settings.DoNotLimitPassingSpeedDefault;

            if (Input.GetKey(Loader.Settings.CoupleToNearestShortcutHotkey.keyCode) && String.IsNullOrEmpty(coupleToCarId))
            {
                CouplingSearchMode = (ManagedWaypoint.CoupleSearchMode)Loader.Settings.DefaultCouplingSearchMode;
            }
            else if (Input.GetKey(Loader.Settings.UncoupleShortcutHotkey.keyCode))
            {
                UncouplingMode = (UncoupleMode)Loader.Settings.DefaultUncouplingMode;
            }
            else if (Input.GetKey(Loader.Settings.KickingShortcutHotkey.keyCode))
            {
                UncouplingMode = (UncoupleMode)Loader.Settings.DefaultUncouplingMode;
                ConfigureForKicking();
            }

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
        public string Id { get; private set; } = Guid.NewGuid().ToString();

        [JsonProperty]
        public string LocomotiveId { get; private set; }

        [JsonIgnore]
        public virtual BaseLocomotive Locomotive
        {
            get
            {
                TrainController.Shared.TryGetCarForId(LocomotiveId, out Car carLoco);
                if (carLoco is BaseLocomotive loco)
                {
                    return loco;
                }
                Loader.LogError($"Failed to resolve locomotive {LocomotiveId} for waypoint");
                return null;
            }
        }

        [JsonProperty]
        public string LocationString { get; private set; }

        private Location _location;

        [JsonIgnore]
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
        public bool BottleAirOnUncouple { get; set; }
        public virtual int NumberOfCarsToCut { get; set; }
        public virtual bool CountUncoupledFromNearestToWaypoint { get; set; } = true;

        [JsonProperty("TakeOrLeaveCut")]
        private PostCoupleCutType _postCouplingCutMode = PostCoupleCutType.None;

        [JsonIgnore]
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
        public List<string> RefuelLocoIdsQueue { get; set; } = [];
        public bool EnableMultipleRefueling { get; set; } = true;
        public string RefuelLoaderRegisteredId { get; set; }

        [JsonIgnore]
        private string _areaName = string.Empty;
        [JsonIgnore]
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
        public string TimetableSymbol { get; set; }

        public bool WillWait { get; set; }
        public bool CurrentlyWaiting { get; set; }
        public bool WaitingWasSkipped { get; set; } = false;
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

        public string CouplingSearchResultCarId { get; set; } = "";

        [JsonIgnore]
        public Car CouplingSearchResultCar
        {
            get
            {
                // O(1) lookup by id
                if (!String.IsNullOrEmpty(CouplingSearchResultCarId) && TrainController.Shared.TryGetCarForId(CouplingSearchResultCarId, out Car car))
                {
                    return car;
                }

                if (!String.IsNullOrEmpty(CouplingSearchText))
                {
                    // O(n) worst case for searching all cars by Ident text
                    car = TrainController.Shared.CarForString(CouplingSearchText);
                    CouplingSearchResultCarId = car?.id ?? "";
                    return car;
                }
                return null;
            }
        }

        public string UncouplingSearchText { get; set; } = "";
        public string UncouplingSearchResultCarId { get; set; } = "";

        [JsonIgnore]
        public Car UncouplingSearchResultCar
        {
            get
            {
                // O(1) lookup by id
                if (!String.IsNullOrEmpty(UncouplingSearchResultCarId) && TrainController.Shared.TryGetCarForId(UncouplingSearchResultCarId, out Car car))
                {
                    return car;
                }

                if (!String.IsNullOrEmpty(UncouplingSearchText))
                {
                    // O(n) worst case for searching all cars by Ident text
                    car = TrainController.Shared.CarForString(UncouplingSearchText);
                    UncouplingSearchResultCarId = car?.id ?? "";
                    return car;
                }
                return null;
            }
        }
        public string DestinationSearchText { get; set; } = "";
        public virtual string UncoupleDestinationId { get; set; } = "";
        public virtual bool ExcludeMatchingCarsFromCut { get; set; }

        public bool MoveTrainPastWaypoint { get; set; }
        public bool CurrentlyWaitingBeforeCutting { get; set; }

        public string StatusLabel { get; set; } = "Inactive";
        public string Name { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        public List<WaypointError> Errors { get; set; } = [];

        private void SetDefaultPostCouplingCut()
        {
            PostCouplingCutMode = (PostCoupleCutType)Loader.Settings.DefaultPostCouplingCutMode;
            UncouplingMode = (UncoupleMode)Loader.Settings.DefaultPostCouplingCutUncouplingMode;
        }

        public bool IsValidForRoute()
        {
            bool validLocation = TryResolveLocation(out Location loc);
            bool validCoupleToCar = String.IsNullOrEmpty(CoupleToCarId) || TryResolveCoupleToCar(out Car coupleToCar);

            return validLocation && validCoupleToCar;
        }

        public bool IsValidForQueue()
        {
            return IsValidForRoute() && Locomotive != null;
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

        public void ConfigureForKicking()
        {
            StopAtWaypoint = false;
            WillLimitPassingSpeed = true;
            WaypointTargetSpeed = Loader.Settings.PassingSpeedForKickingCars;
            if (Loader.Settings.UncheckAirAndBrakesForKick)
            {
                ApplyHandbrakesOnUncouple = false;
                BleedAirOnUncouple = false;
            }
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
            if (IsValidForRoute())
            {
                copy = ManagedWaypoint.FromPropertyValue(this.ToPropertyValue());
                copy.Id = Guid.NewGuid().ToString();
                copy.LocomotiveId = loco?.id ?? null;
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
                   BottleAirOnUncouple == other.BottleAirOnUncouple &&
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
                   RefuelLocoIdsQueue.SequenceEqual(other.RefuelLocoIdsQueue) &&
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

        public Value ToPropertyValue()
        {
            var dictionary = new Dictionary<string, Value>
            {
                [ValueKeys.Id] = Value.String(Id),
                [ValueKeys.Name] = Value.String(Name),
                [ValueKeys.Version] = Value.Int(Version ?? 0),
                [ValueKeys.LocomotiveId] = Value.String(LocomotiveId),
                [ValueKeys.LocationString] = Value.String(LocationString),
                [ValueKeys.CoupleToCarId] = Value.String(CoupleToCarId),
                [ValueKeys.ConnectAirOnCouple] = Value.Bool(ConnectAirOnCouple),
                [ValueKeys.ReleaseHandbrakesOnCouple] = Value.Bool(ReleaseHandbrakesOnCouple),
                [ValueKeys.HasResolvedBrakeSystemOnCouple] = Value.Bool(HasResolvedBrakeSystemOnCouple),
                [ValueKeys.ApplyHandbrakesOnUncouple] = Value.Bool(ApplyHandbrakesOnUncouple),
                [ValueKeys.BleedAirOnUncouple] = Value.Bool(BleedAirOnUncouple),
                [ValueKeys.BottleAirOnUncouple] = Value.Bool(BottleAirOnUncouple),
                [ValueKeys.NumberOfCarsToCut] = Value.Int(NumberOfCarsToCut),
                [ValueKeys.CountUncoupledFromNearestToWaypoint] = Value.Bool(CountUncoupledFromNearestToWaypoint),
                [ValueKeys.PostCouplingCutMode] = Value.Int((int)_postCouplingCutMode),
                [ValueKeys.TakeUncoupledCarsAsActiveCut] = Value.Bool(TakeUncoupledCarsAsActiveCut),
                [ValueKeys.SerializableRefuelPoint] = SerializableRefuelPoint.ToPropertyValue(),
                [ValueKeys.RefuelIndustryId] = Value.String(RefuelIndustryId),
                [ValueKeys.RefuelLoadName] = Value.String(RefuelLoadName),
                [ValueKeys.RefuelMaxCapacity] = Value.Float(RefuelMaxCapacity),
                [ValueKeys.WillRefuel] = Value.Bool(WillRefuel),
                [ValueKeys.CurrentlyRefueling] = Value.Bool(CurrentlyRefueling),
                [ValueKeys.RefuelingSpeedLimit] = Value.Int(RefuelingSpeedLimit),
                [ValueKeys.MaxSpeedAfterRefueling] = Value.Int(MaxSpeedAfterRefueling),
                [ValueKeys.RefuelLoaderAnimated] = Value.Bool(RefuelLoaderAnimated),
                [ValueKeys.RefuelLocoIdsQueue] = Value.Array(RefuelLocoIdsQueue.Select(x => Value.String(x)).ToList()),
                [ValueKeys.EnableMultipleRefueling] = Value.Bool(EnableMultipleRefueling),
                [ValueKeys.RefuelLoaderRegisteredId] = Value.String(RefuelLoaderRegisteredId),
                [ValueKeys.TimetableSymbol] = Value.String(TimetableSymbol),
                [ValueKeys.WillWait] = Value.Bool(WillWait),
                [ValueKeys.CurrentlyWaiting] = Value.Bool(CurrentlyWaiting),
                [ValueKeys.WaitingWasSkipped] = Value.Bool(WaitingWasSkipped),
                [ValueKeys.DurationOrSpecificTime] = Value.Int((int)DurationOrSpecificTime),
                [ValueKeys.WaitUntilTimeString] = Value.String(WaitUntilTimeString),
                [ValueKeys.WaitUntilDay] = Value.Int((int)WaitUntilDay),
                [ValueKeys.WaitForDurationMinutes] = Value.Int(WaitForDurationMinutes),
                [ValueKeys.WaitUntilGameTotalSeconds] = Value.String(WaitUntilGameTotalSeconds.ToString()),
                [ValueKeys.StopAtWaypoint] = Value.Bool(StopAtWaypoint),
                [ValueKeys.WillLimitPassingSpeed] = Value.Bool(WillLimitPassingSpeed),
                [ValueKeys.WaypointTargetSpeed] = Value.Int(WaypointTargetSpeed),
                [ValueKeys.WillChangeMaxSpeed] = Value.Bool(WillChangeMaxSpeed),
                [ValueKeys.MaxSpeedForChange] = Value.Int(MaxSpeedForChange),
                [ValueKeys.CouplingSearchMode] = Value.Int((int)_couplingSearchMode),
                [ValueKeys.UncouplingMode] = Value.Int((int)_uncouplingMode),
                [ValueKeys.CurrentlyCouplingNearby] = Value.Bool(CurrentlyCouplingNearby),
                [ValueKeys.CurrentlyCouplingSpecificCar] = Value.Bool(CurrentlyCouplingSpecificCar),
                [ValueKeys.OnlySeekNearbyOnTrackAhead] = Value.Bool(OnlySeekNearbyOnTrackAhead),
                [ValueKeys.CouplingSearchText] = Value.String(CouplingSearchText),
                [ValueKeys.CouplingSearchResultCarId] = Value.String(CouplingSearchResultCarId),
                [ValueKeys.UncouplingSearchText] = Value.String(UncouplingSearchText),
                [ValueKeys.UncouplingSearchResultCarId] = Value.String(UncouplingSearchResultCarId),
                [ValueKeys.DestinationSearchText] = Value.String(DestinationSearchText),
                [ValueKeys.UncoupleDestinationId] = Value.String(UncoupleDestinationId),
                [ValueKeys.ExcludeMatchingCarsFromCut] = Value.Bool(ExcludeMatchingCarsFromCut),
                [ValueKeys.MoveTrainPastWaypoint] = Value.Bool(MoveTrainPastWaypoint),
                [ValueKeys.CurrentlyWaitingBeforeCutting] = Value.Bool(CurrentlyWaitingBeforeCutting),
                [ValueKeys.StatusLabel] = Value.String(StatusLabel),
                [ValueKeys.Errors] = Value.Array(Errors.Select(e => e.ToPropertyValue()).ToList()),
            };
            return Value.Dictionary(dictionary.ToDictionary(k => k.Key, v => v.Value));
        }

        public static ManagedWaypoint FromPropertyValue(Value value)
        {
            if (value.IsNull || value.Type != KeyValue.Runtime.ValueType.Dictionary) return null;

            var dict = value.DictionaryValue;

            ManagedWaypoint waypoint = new ManagedWaypoint();

            waypoint.Id = dict[ValueKeys.Id].StringValue;
            waypoint.Name = dict[ValueKeys.Name].StringValue;
            waypoint.Version = dict[ValueKeys.Version].IntValue;
            waypoint.LocomotiveId = dict[ValueKeys.LocomotiveId].StringValue;
            waypoint.LocationString = dict[ValueKeys.LocationString].StringValue;
            waypoint.CoupleToCarId = dict[ValueKeys.CoupleToCarId].StringValue;
            waypoint.ConnectAirOnCouple = dict[ValueKeys.ConnectAirOnCouple].BoolValue;
            waypoint.ReleaseHandbrakesOnCouple = dict[ValueKeys.ReleaseHandbrakesOnCouple].BoolValue;
            waypoint.HasResolvedBrakeSystemOnCouple = dict[ValueKeys.HasResolvedBrakeSystemOnCouple].BoolValue;
            waypoint.ApplyHandbrakesOnUncouple = dict[ValueKeys.ApplyHandbrakesOnUncouple].BoolValue;
            waypoint.BleedAirOnUncouple = dict[ValueKeys.BleedAirOnUncouple].BoolValue;
            waypoint.NumberOfCarsToCut = dict[ValueKeys.NumberOfCarsToCut].IntValue;
            waypoint.CountUncoupledFromNearestToWaypoint = dict[ValueKeys.CountUncoupledFromNearestToWaypoint].BoolValue;
            waypoint._postCouplingCutMode = (PostCoupleCutType)dict[ValueKeys.PostCouplingCutMode].IntValue;
            waypoint.TakeUncoupledCarsAsActiveCut = dict[ValueKeys.TakeUncoupledCarsAsActiveCut].BoolValue;
            waypoint.SerializableRefuelPoint = SerializableVector3.FromPropertyValue(dict[ValueKeys.SerializableRefuelPoint]);
            waypoint.RefuelIndustryId = dict[ValueKeys.RefuelIndustryId].StringValue;
            waypoint.RefuelLoadName = dict[ValueKeys.RefuelLoadName].StringValue;
            waypoint.RefuelMaxCapacity = dict[ValueKeys.RefuelMaxCapacity].FloatValue;
            waypoint.WillRefuel = dict[ValueKeys.WillRefuel].BoolValue;
            waypoint.CurrentlyRefueling = dict[ValueKeys.CurrentlyRefueling].BoolValue;
            waypoint.RefuelingSpeedLimit = dict[ValueKeys.RefuelingSpeedLimit].IntValue;
            waypoint.MaxSpeedAfterRefueling = dict[ValueKeys.MaxSpeedAfterRefueling].IntValue;
            waypoint.RefuelLoaderAnimated = dict[ValueKeys.RefuelLoaderAnimated].BoolValue;
            waypoint.TimetableSymbol = dict[ValueKeys.TimetableSymbol].StringValue;
            waypoint.WillWait = dict[ValueKeys.WillWait].BoolValue;
            waypoint.CurrentlyWaiting = dict[ValueKeys.CurrentlyWaiting].BoolValue;
            waypoint.WaitingWasSkipped = dict[ValueKeys.WaitingWasSkipped].BoolValue;
            waypoint.DurationOrSpecificTime = (WaitType)dict[ValueKeys.DurationOrSpecificTime].IntValue;
            waypoint.WaitUntilTimeString = dict[ValueKeys.WaitUntilTimeString].StringValue;
            waypoint.WaitUntilDay = (TodayOrTomorrow)dict[ValueKeys.WaitUntilDay].IntValue;
            waypoint.WaitForDurationMinutes = dict[ValueKeys.WaitForDurationMinutes].IntValue;
            waypoint.WaitUntilGameTotalSeconds = Convert.ToDouble(dict[ValueKeys.WaitUntilGameTotalSeconds].StringValue);
            waypoint.StopAtWaypoint = dict[ValueKeys.StopAtWaypoint].BoolValue;
            waypoint.WillLimitPassingSpeed = dict[ValueKeys.WillLimitPassingSpeed].BoolValue;
            waypoint.WaypointTargetSpeed = dict[ValueKeys.WaypointTargetSpeed].IntValue;
            waypoint.WillChangeMaxSpeed = dict[ValueKeys.WillChangeMaxSpeed].BoolValue;
            waypoint.MaxSpeedForChange = dict[ValueKeys.MaxSpeedForChange].IntValue;
            waypoint._couplingSearchMode = (CoupleSearchMode)dict[ValueKeys.CouplingSearchMode].IntValue;
            waypoint._uncouplingMode = (UncoupleMode)dict[ValueKeys.UncouplingMode].IntValue;
            waypoint.CurrentlyCouplingNearby = dict[ValueKeys.CurrentlyCouplingNearby].BoolValue;
            waypoint.CurrentlyCouplingSpecificCar = dict[ValueKeys.CurrentlyCouplingSpecificCar].BoolValue;
            waypoint.OnlySeekNearbyOnTrackAhead = dict[ValueKeys.OnlySeekNearbyOnTrackAhead].BoolValue;
            waypoint.CouplingSearchText = dict[ValueKeys.CouplingSearchText].StringValue;
            waypoint.CouplingSearchResultCarId = dict[ValueKeys.CouplingSearchResultCarId].StringValue;
            waypoint.UncouplingSearchText = dict[ValueKeys.UncouplingSearchText].StringValue;
            waypoint.UncouplingSearchResultCarId = dict[ValueKeys.UncouplingSearchResultCarId].StringValue;
            waypoint.DestinationSearchText = dict[ValueKeys.DestinationSearchText].StringValue;
            waypoint.UncoupleDestinationId = dict[ValueKeys.UncoupleDestinationId].StringValue;
            waypoint.ExcludeMatchingCarsFromCut = dict[ValueKeys.ExcludeMatchingCarsFromCut].BoolValue; ;
            waypoint.MoveTrainPastWaypoint = dict[ValueKeys.MoveTrainPastWaypoint].BoolValue;
            waypoint.CurrentlyWaitingBeforeCutting = dict[ValueKeys.CurrentlyWaitingBeforeCutting].BoolValue; ;
            waypoint.StatusLabel = dict[ValueKeys.StatusLabel].StringValue;
            waypoint.Errors = [.. dict[ValueKeys.Errors].ArrayValue.ToList().Select(e => WaypointError.FromPropertyValue(e))];

            if (dict.ContainsKey(ValueKeys.EnableMultipleRefueling))
            {
                waypoint.EnableMultipleRefueling = dict[ValueKeys.EnableMultipleRefueling].BoolValue;
            }

            if (dict.ContainsKey(ValueKeys.RefuelLocoIdsQueue))
            {
                waypoint.RefuelLocoIdsQueue = [.. dict[ValueKeys.RefuelLocoIdsQueue].ArrayValue.ToList().Select(v => v.StringValue)];
            }

            if (dict.ContainsKey(ValueKeys.BottleAirOnUncouple))
            {
                waypoint.BottleAirOnUncouple = dict[ValueKeys.BottleAirOnUncouple].BoolValue;
            }
            else
            {
                waypoint.BottleAirOnUncouple = !Loader.Settings.DoNotBottleAir;
            }

            if (dict.ContainsKey(ValueKeys.RefuelLoaderRegisteredId))
            {
                waypoint.RefuelLoaderRegisteredId = dict[ValueKeys.RefuelLoaderRegisteredId].StringValue;
            }

            return waypoint;
        }

        private static class ValueKeys
        {
            internal static string Id = "id";
            internal static string Name = "name";
            internal static string Version = "version";
            internal static string LocomotiveId = "locomotive_id";
            internal static string LocationString = "location_string";
            internal static string CoupleToCarId = "couple_to_car_id";
            internal static string ConnectAirOnCouple = "connect_air_on_couple";
            internal static string ReleaseHandbrakesOnCouple = "release_handbrakes_on_couple";
            internal static string HasResolvedBrakeSystemOnCouple = "has_resolved_brake_system_on_couple";
            internal static string ApplyHandbrakesOnUncouple = "apply_handbrakes_on_uncouple";
            internal static string BleedAirOnUncouple = "bleed_air_on_uncouple";
            internal static string BottleAirOnUncouple = "bottle_air_on_uncouple";
            internal static string NumberOfCarsToCut = "number_of_cars_to_cut";
            internal static string CountUncoupledFromNearestToWaypoint = "count_uncoupled_from_nearest_to_waypoint";
            internal static string PostCouplingCutMode = "post_coupling_cut_mode";
            internal static string TakeUncoupledCarsAsActiveCut = "take_uncoupled_cars_as_active_cut";
            internal static string SerializableRefuelPoint = "serializable_refuel_point";
            internal static string RefuelIndustryId = "refuel_industry_id";
            internal static string RefuelLoadName = "refuel_load_name";
            internal static string RefuelMaxCapacity = "refuel_max_capacity";
            internal static string WillRefuel = "will_refuel";
            internal static string CurrentlyRefueling = "currently_refueling";
            internal static string RefuelingSpeedLimit = "refueling_speed_limit";
            internal static string MaxSpeedAfterRefueling = "max_speed_after_refueling";
            internal static string RefuelLoaderAnimated = "refuel_loader_animated";
            internal static string RefuelLocoIdsQueue = "refuel_loco_ids_queue";
            internal static string EnableMultipleRefueling = "enable_multiple_refueling";
            internal static string RefuelLoaderRegisteredId = "refuel_loader_registered_id";
            internal static string TimetableSymbol = "timetable_symbol";
            internal static string WillWait = "will_wait";
            internal static string CurrentlyWaiting = "currently_waiting";
            internal static string WaitingWasSkipped = "waiting_was_skipped";
            internal static string DurationOrSpecificTime = "duration_or_specific_time";
            internal static string WaitUntilTimeString = "wait_until_time_string";
            internal static string WaitUntilDay = "wait_until_day";
            internal static string WaitForDurationMinutes = "wait_for_duration_minutes";
            internal static string WaitUntilGameTotalSeconds = "wait_until_game_total_seconds";
            internal static string StopAtWaypoint = "stop_at_waypoint";
            internal static string WillLimitPassingSpeed = "will_limit_passing_speed";
            internal static string WaypointTargetSpeed = "waypoint_target_speed";
            internal static string WillChangeMaxSpeed = "will_change_max_speed";
            internal static string MaxSpeedForChange = "max_speed_for_change";
            internal static string CouplingSearchMode = "coupling_search_mode";
            internal static string UncouplingMode = "uncoupling_mode";
            internal static string CurrentlyCouplingNearby = "currently_coupling_nearby";
            internal static string CurrentlyCouplingSpecificCar = "currently_coupling_specific_car";
            internal static string OnlySeekNearbyOnTrackAhead = "only_seek_nearby_on_track_ahead";
            internal static string CouplingSearchText = "coupling_search_text";
            internal static string CouplingSearchResultCarId = "coupling_search_result_car_id";
            internal static string UncouplingSearchText = "uncoupling_search_text";
            internal static string UncouplingSearchResultCarId = "uncoupling_search_result_car_id";
            internal static string DestinationSearchText = "destination_search_text";
            internal static string UncoupleDestinationId = "uncouple_destination_id";
            internal static string ExcludeMatchingCarsFromCut = "exclude_matching_cars_from_cut";
            internal static string MoveTrainPastWaypoint = "move_train_past_waypoint";
            internal static string CurrentlyWaitingBeforeCutting = "currently_waiting_before_cutting";
            internal static string StatusLabel = "status_label";
            internal static string Errors = "errors";
        }
    }

    [Serializable]
    public struct SerializableVector3 : IStorableProperty
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

        public Value ToPropertyValue()
        {
            var dictionary = new Dictionary<string, Value>
            {
                { "x", Value.Float(X) },
                { "y", Value.Float(Y) },
                { "z", Value.Float(Z) },
            };

            return Value.Dictionary(dictionary.ToDictionary(k => k.Key, v => v.Value));
        }

        public static SerializableVector3 FromPropertyValue(Value value)
        {
            var dict = value.DictionaryValue;

            return new SerializableVector3(dict["x"], dict["y"], dict["z"]);
        }
    }
}
