using HarmonyLib;
using Helpers;
using Model;
using Model.AI;
using Model.AI.Waypoints;
using System;
using System.Collections.Generic;
using Track;
using Track.Search;
using UI;
using UI.Common;
using UI.Tags;
using UI.Waypoints;
using UnityEngine;
using UnityEngine.Pool;
using WaypointQueue.Services;

namespace WaypointQueue.UI
{
    internal class WaypointPicker : MonoBehaviour
    {
        private struct Hit(Location location, (Car car, Car.End end)? carInfo)
        {
            public Location Location = location;

            public (Car car, Car.End end)? CarInfo = carInfo;
        }

        private readonly struct RoutePlacement
        {
            public readonly Location Mark;

            public readonly int Pose;

            public readonly IReadOnlyList<JunctionTurn> Turns;

            public RoutePlacement(Location mark, int pose, IReadOnlyList<JunctionTurn> turns)
            {
                Mark = mark;
                Pose = pose;
                Turns = turns;
            }
        }

        private static WaypointPicker _shared;

        private RefuelService _refuelService;
        private AutoEngineerService _aeService;
        private RouteOverlayController _routeOverlayController;
        private IGraphView _graph;

        private Transform _waypointMarker;

        private ManagedWaypoint _waypoint;
        private Action<ManagedWaypoint> _onWaypointChange;

        private Action<ManagedWaypoint> _onWaypointSelected;
        private Action<ManagedWaypoint, string> _onWaypointInsert;
        private string _routeId;
        private bool _forRoute;

        private string _cancelMessage = String.Empty;

        private BaseLocomotive _locomotive;
        private HashSet<Car> _dontSnapToCars = [];
        private float _trainLength;
        private (Car car, Car.End end)? _currentCarInfo;

        private string _routeSummary;
        private bool _isPicking;

        private int _poseIndex;
        private WaypointPoseOption _naturalPose;

        private Vector3 _answerPosition;
        private bool _answerValid;
        private bool _answerNamedPose;

        private bool _routeRefused;
        private RoutePlacement? _routePlacement;

        private readonly List<float> _carLengths = new List<float>();
        private readonly List<List<Location>> _carFootprints = new List<List<Location>>();
        private readonly List<string> _footprintTokens = new List<string>();

        private bool _poseFits = true;
        private bool _poseChosen;
        private bool _poseKnown;
        private PlacementOutcome _poseOutcome;

        private int _footprintBuiltForPose = -1;
        private PlacementOutcome _footprintBuiltForOutcome;
        private Location _footprintBuiltFor;

        private bool _nearSideResolved;
        private TrackSegment.End _nearSide;
        private Location _nearSideMark;
        private Vector3 _nearSideMarkPosition;
        private Vector3 _nearDirection;
        private TrackSegment.End? _coupleBodySide;

        private Location _routePreviewTarget;
        private float _routePreviewTargetMovedAt;
        private string _routePreviewCoupleToCarId;
        private Location _routePreviewDisplayed;
        private float _routePreviewLastCost;
        private float _routePreviewLastRebuildEndedAt;

        private Location? _hoverLocation;

        private Vector2 _cursorPosition;
        private float _cursorMovedAt;

        public static WaypointPicker Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = FindAnyObjectByType<WaypointPicker>();
                }

                return _shared;
            }
        }

        private WaypointPoseOption CurrentPose => WaypointPoseCycle.Options[_poseIndex];

        private void Awake()
        {
            _refuelService = ModLocator.ServiceProvider.GetService<RefuelService>();
            _aeService = ModLocator.ServiceProvider.GetService<AutoEngineerService>();
        }

        private void OnDisable()
        {
            DestroyPreviewOverlay();
            ClearFootprint();
        }

        private void InitWaypointMarker()
        {
            ModLocator.Loader.LogDebug($"Waypoint adjuster InitWaypointMarker");
            Transform destinationMarker = Traverse.Create(AutoEngineerDestinationPicker.Shared).Field<Transform>("destinationMarker").Value;
            _waypointMarker = destinationMarker;
        }

        private bool PreviewIsCurrent
        {
            get
            {
                if (_routeSummary != null)
                {
                    return _routePreviewTarget.Equals(_routePreviewDisplayed);
                }

                return false;
            }
        }


        public void StartAdjustingWaypoint(ManagedWaypoint waypoint, Action<ManagedWaypoint> onWaypointChange, bool forRoute = false)
        {
            if (_isPicking)
            {
                return;
            }

            _waypoint = waypoint;
            _onWaypointChange = onWaypointChange;
            _onWaypointSelected = AdjustWaypoint;
            _forRoute = forRoute;
            _cancelMessage = "Cancelled waypoint adjustment";

            _dontSnapToCars = [];
            if (!forRoute)
            {
                _locomotive = waypoint.Locomotive;
                _dontSnapToCars = [.. waypoint.Locomotive.EnumerateCoupled()];
            }

            StartGenericPicking();
        }

        public void StartInsertingWaypoint(ManagedWaypoint beforeWaypoint, Action<ManagedWaypoint, string> onWaypointInsert, bool forRoute = false)
        {
            if (_isPicking)
            {
                return;
            }

            _waypoint = beforeWaypoint;
            _onWaypointSelected = HandleInsertNewWaypoint;
            _onWaypointInsert = onWaypointInsert;
            _forRoute = forRoute;
            _cancelMessage = "Cancelled inserting waypoint";

            if (!forRoute)
            {
                _locomotive = beforeWaypoint.Locomotive;
                _dontSnapToCars = [.. beforeWaypoint.Locomotive.EnumerateCoupled()];
            }

            StartGenericPicking();
        }

        public void StartPickingWaypointForRoute(Action<ManagedWaypoint, string> onWaypointInsert, string routeId)
        {
            if (_isPicking)
            {
                return;
            }

            _onWaypointSelected = HandleAddWaypointToRoute;
            _onWaypointInsert = onWaypointInsert;
            _routeId = routeId;
            _cancelMessage = "Cancelled adding waypoint";

            StartGenericPicking();
        }

        private void StartGenericPicking()
        {
            if (_waypointMarker == null)
            {
                InitWaypointMarker();
            }

            _graph = Graph.Shared.Current;

            AutoEngineerDestinationPicker.Shared.Cancel();
            Cancel();

            _dontSnapToCars = [];
            if (_locomotive != null)
            {
                _dontSnapToCars = [.. _locomotive.EnumerateCoupled()];
            }
            _currentCarInfo = null;
            _poseIndex = 0;
            _poseChosen = true;
            _poseKnown = true;
            _routePreviewLastCost = 0f;
            _routePreviewTarget = Location.Invalid;
            _routePreviewDisplayed = Location.Invalid;
            _answerValid = false;
            _answerNamedPose = false;
            _hoverLocation = null;
            _coupleBodySide = null;
            _nearSideResolved = false;
            _routeSummary = null;
            _routeRefused = false;
            _poseFits = true;
            _poseOutcome = PlacementOutcome.Complete;

            TrackLocationPickRequest request = new TrackLocationPickRequest
            {
                Prompt = PromptText(),
                CanSelect = CanSelectLocation,
                OnLocationSelected = OnLocationSelected,
                OnCancelled = OnCancelled,
                OnHoverLocationChanged = OnHoverLocationChanged,
                OnCycleRequested = CyclePose,
                LocationProcessor = ProcessLocation
            };
            if (!TrackLocationPicker.Shared.StartPicking(request))
            {
                ShowMessage("Cannot start waypoint selection");
                return;
            }

            _isPicking = true;

            GameInput.RegisterEscapeHandler(GameInput.EscapeHandler.Transient, DidEscape);
        }

        private string PromptText()
        {
            return "<sprite name=\"MouseLeft\"> Track to Set Waypoint  ·  [Space] Change Orientation";
        }

        private bool CanSelectLocation(Location _)
        {
            return true;
        }

        private void OnLocationSelected(Location location)
        {
            ModLocator.Loader.Log($"Location selected with current car {_currentCarInfo?.car.id} with end {_currentCarInfo?.end}");
            TryDerivePlacementForCommit(location, out var placement, out var refusal);

            ManagedWaypoint waypoint;
            if (_locomotive != null)
            {
                var (waypointEnd, bodyToward) = CurrentPose.ToOrderFields(_locomotive, NearSideFor(location));
                var orderWaypoint = PosedWaypoint.FromPlacement(location, waypointEnd, bodyToward, placement, [], _currentCarInfo?.car.id, _currentCarInfo?.end);
                waypoint = new ManagedWaypoint(_locomotive, orderWaypoint);
                waypoint.PoseIndex = _poseIndex;
            }
            else
            {
                var orderWaypoint = PosedWaypoint.FromPlacement(location, WaypointEnd.Nearest, BodyToward.Unspecified, placement, [], _currentCarInfo?.car.id, _currentCarInfo?.end);
                waypoint = new ManagedWaypoint(_locomotive, orderWaypoint);
                waypoint.PoseIndex = _poseIndex;
            }

            _onWaypointSelected(waypoint);

            Cleanup();
        }

        private bool CursorHeldStill(float now)
        {
            if (!Input.mousePresent)
            {
                return true;
            }

            Vector2 vector = Input.mousePosition;
            if ((vector - _cursorPosition).sqrMagnitude > 4f)
            {
                _cursorPosition = vector;
                _cursorMovedAt = now;
            }

            return now - _cursorMovedAt >= 0.15f;
        }

        private void Update()
        {
            float realtimeSinceStartup = Time.realtimeSinceStartup;
            bool flag = CursorHeldStill(realtimeSinceStartup);
            if (!_routePreviewTarget.IsValid || PreviewIsCurrent)
            {
                return;
            }

            bool flag2 = realtimeSinceStartup - _routePreviewTargetMovedAt >= 0.15f;
            if (!flag && !flag2)
            {
                return;
            }

            float num = Mathf.Clamp(_routePreviewLastCost * 10f, 0.1f, 2f);
            if (!(realtimeSinceStartup - _routePreviewLastRebuildEndedAt < num))
            {
                Location routePreviewTarget = _routePreviewTarget;
                float realtimeSinceStartup2 = Time.realtimeSinceStartup;
                try
                {
                    RebuildRoutePreview(routePreviewTarget, _routePreviewCoupleToCarId);
                }
                catch (Exception ex)
                {
                    ModLocator.Loader.LogError(ex, $"Waypoint route preview failed for {routePreviewTarget}");
                    RefuseRoutePreview();
                }

                _routePreviewLastRebuildEndedAt = Time.realtimeSinceStartup;
                _routePreviewLastCost = _routePreviewLastRebuildEndedAt - realtimeSinceStartup2;
                _routePreviewDisplayed = routePreviewTarget;
            }
        }


        private bool TryDerivePlacementForCommit(Location location, out PlacementResult placement, out string refusal)
        {
            placement = default(PlacementResult);
            if (_poseKnown)
            {
                placement = CurrentPose.DerivePlacement(_graph, location, NearSideFor(location), _trainLength, null, [], null, null, []);
            }

            bool routeRefused = _routeRefused && _routePreviewDisplayed.Equals(location, 1f);
            return WaypointPoseCycle.CanCommit(_poseKnown, placement.Outcome, routeRefused, _routeSummary, out refusal);
        }


        private void OnHoverLocationChanged(Location? location)
        {
            _hoverLocation = location;
            if (location.HasValue)
            {
                Graph.PositionRotation positionRotation = _graph.GetPositionRotation(location.Value);
                _waypointMarker.position = WorldTransformer.GameToWorld(positionRotation.Position);
                _waypointMarker.rotation = positionRotation.Rotation;
                _waypointMarker.gameObject.SetActive(value: true);
                UpdatePoseForMark(location.Value);
                if (_poseKnown)
                {
                    UpdateFootprint(location.Value);
                }
                else
                {
                    ClearFootprint();
                }

                UpdatePrompt();
                PreviewRoute(location.Value, _currentCarInfo?.car.id);
            }
            else
            {
                _waypointMarker.gameObject.SetActive(value: false);
                ClearFootprint();
                StopPreview();
                UpdatePrompt();
            }
        }

        private void PreviewRoute(Location targetLocation, string coupleToCarId)
        {
            if (!_routePreviewTarget.Equals(targetLocation, 1f) || !(_routePreviewCoupleToCarId == coupleToCarId))
            {
                _routePreviewTarget = targetLocation;
                _routePreviewCoupleToCarId = coupleToCarId;
                _routePreviewTargetMovedAt = Time.realtimeSinceStartup;
            }
        }

        private void RefuseRoutePreview()
        {
            _routeSummary = "No route.";
            _routeRefused = true;
            _routePlacement = null;
            DestroyPreviewOverlay();
            ApplyFootprintColor();
            UpdatePrompt();
        }

        private void RebuildRoutePreview(Location targetLocation, string coupleToCarId)
        {
            TrackSegment.End nearSide = NearSideFor(targetLocation);
            List<JunctionTurn> list = new List<JunctionTurn>();
            RecordAnswer(targetLocation, DeriveNaturalPose(targetLocation, coupleToCarId, nearSide, list));
            _poseKnown = _poseChosen || _answerNamedPose;
            bool isPlanned = false;

            if (_locomotive != null)
            {
                (WaypointEnd end, BodyToward side) tuple = CurrentPose.ToOrderFields(_locomotive, nearSide);
                WaypointEnd item = tuple.end;
                BodyToward item2 = tuple.side;

                PlacementPlan placementPlan = PosedWaypoint.Plan(_locomotive, _graph, targetLocation, coupleToCarId, item, item2, list);
                isPlanned = placementPlan.IsPlanned;
                _routePlacement = ((isPlanned && placementPlan.FollowsRoute) ? new RoutePlacement?(new RoutePlacement(targetLocation, _poseIndex, placementPlan.Waypoint.Value.RequiredTurns ?? Array.Empty<JunctionTurn>())) : null);
            }
            else
            {
                _routePlacement = new RoutePlacement(targetLocation, _poseIndex, []);
                isPlanned = true;
            }

            if (_poseKnown)
            {
                UpdateFootprint(targetLocation);
            }
            else
            {
                ClearFootprint();
            }

            _routeRefused = !isPlanned;
            ApplyFootprintColor();
            _routeSummary = "No route.";
            UpdatePrompt();
            if (isPlanned)
            {
                CreatePreviewOverlayIfNeeded();
                //_routeOverlayController.SetRoute(placementPlan.Route.Select((RouteSearch.Step step) => step.Location).ToList());
            }
            else
            {
                DestroyPreviewOverlay();
            }
        }

        private bool DeriveNaturalPose(Location targetLocation, string coupleToCarId, TrackSegment.End nearSide, List<JunctionTurn> arrivalTurnsOut)
        {
            if (_locomotive == null || !PosedWaypoint.PlanNaturalArrival((BaseLocomotive)_locomotive, _graph, targetLocation, coupleToCarId, arrivalTurnsOut, out var route) || route.Count == 0)
            {
                return false;
            }

            List<RouteSearch.Step> list = route;
            RouteSearch.Step step = list[list.Count - 1];
            if (_locomotive != null && WaypointPoseCycle.TryDescribeArrival(_graph, (BaseLocomotive)_locomotive, targetLocation, nearSide, _trainLength, step.Location, step.LeadingEnd, arrivalTurnsOut, out var option))
            {
                SetNaturalPose(option, nearSide);
                return true;
            }

            return false;
        }

        private void SetNaturalPose(WaypointPoseOption natural, TrackSegment.End nearSide)
        {
            _naturalPose = natural;
            if (_poseChosen)
            {
                return;
            }

            int num = WaypointPoseCycle.IndexOf(natural);
            if (!PoseIsAllowed(WaypointPoseCycle.Options[num], nearSide))
            {
                num = WaypointPoseCycle.Next(num, (WaypointPoseOption option) => PoseIsAllowed(option, nearSide));
            }

            _poseIndex = num;
        }

        private void CreatePreviewOverlayIfNeeded()
        {
            Traverse.Create(AutoEngineerDestinationPicker.Shared).Method("CreatePreviewOverlayIfNeeded").GetValue();
            _routeOverlayController = (RouteOverlayController)Traverse.Create(AutoEngineerDestinationPicker.Shared).Field("_routeOverlayController").GetValue();
        }

        private void DestroyPreviewOverlay()
        {
            if (!(_routeOverlayController == null))
            {
                UnityEngine.Object.Destroy(_routeOverlayController.gameObject);
                _routeOverlayController = null;
            }
        }

        private void StopPreview()
        {
            _routePreviewTarget = Location.Invalid;
            DestroyPreviewOverlay();
        }

        private void UpdatePrompt()
        {
            ModalInteractionManager.Shared.UpdatePrompt(PromptText());
        }

        private void UpdatePoseForMark(Location location)
        {
            bool flag = AnswerApplies(location);
            if (!flag)
            {
                DropRouteAnswer();
            }

            _coupleBodySide = (_currentCarInfo.HasValue ? new TrackSegment.End?(PosedWaypoint.CouplerBodySide(location)) : null);
            TrackSegment.End nearSide = NearSideFor(location);
            _poseKnown = _poseChosen || (flag && _answerNamedPose);
            if (!PoseIsAllowed(CurrentPose, nearSide))
            {
                _poseIndex = WaypointPoseCycle.Next(_poseIndex, (WaypointPoseOption option) => PoseIsAllowed(option, nearSide));
            }
        }

        private void RecordAnswer(Location mark, bool namedPose)
        {
            _answerValid = true;
            _answerNamedPose = namedPose;
            _answerPosition = _graph.GetPosition(mark);
        }

        private bool AnswerApplies(Location location)
        {
            if (!_answerValid)
            {
                return false;
            }

            return (_graph.GetPosition(location) - _answerPosition).sqrMagnitude <= 400f;
        }


        private void DropRouteAnswer()
        {
            _routeSummary = null;
            _routeRefused = false;
            _routePlacement = null;
            DestroyPreviewOverlay();
        }

        private Location? GetCurrentOrdersGotoLocation()
        {
            if (_locomotive != null)
            {
                (Location location, string _) = _aeService.GetCurrentOrderWaypoint((BaseLocomotive)_locomotive);
                if (location.IsValid)
                {
                    return location;
                }
            }

            return null;
        }

        private Location? ProcessLocation(Location rawLocation)
        {
            _currentCarInfo = null;
            //RefreshConsist();
            if (_locomotive != null && GetCurrentOrdersGotoLocation().Equals(rawLocation))
            {
                return null;
            }

            TrainController shared = TrainController.Shared;
            Vector3 position = _graph.GetPosition(rawLocation);
            float num = 2f;
            Location? result = null;
            HashSet<Car> value;
            using (CollectionPool<HashSet<Car>, Car>.Get(out value))
            {
                shared.CheckForCarsAtPoint(position, 2f, value, rawLocation);
                foreach (Car item in value)
                {
                    if (_dontSnapToCars.Contains(item))
                    {
                        continue;
                    }

                    if (!item[item.EndToLogical(Car.End.F)].IsCoupled)
                    {
                        Location location = PosedWaypoint.CouplerMark(_graph, item, Car.End.F);
                        float distanceBetweenClose = _graph.GetDistanceBetweenClose(rawLocation, location);
                        if (distanceBetweenClose < num)
                        {
                            num = distanceBetweenClose;
                            result = location;
                            _currentCarInfo = (item, Car.End.F);
                        }
                    }

                    if (!item[item.EndToLogical(Car.End.R)].IsCoupled)
                    {
                        Location location2 = PosedWaypoint.CouplerMark(_graph, item, Car.End.R);
                        float distanceBetweenClose2 = _graph.GetDistanceBetweenClose(rawLocation, location2);
                        if (distanceBetweenClose2 < num)
                        {
                            num = distanceBetweenClose2;
                            result = location2;
                            _currentCarInfo = (item, Car.End.R);
                        }
                    }
                }

                if (value.Count > 0 && result.HasValue)
                {
                    return result;
                }
            }

            return rawLocation;
        }



        private void ShowMessage(string message)
        {
            Toast.Present(message, ToastPosition.Bottom);
        }

        private void AdjustWaypoint(ManagedWaypoint waypoint)
        {
            var newCoupleToCarId = waypoint.CoupleToCarId;
            if (!String.IsNullOrEmpty(newCoupleToCarId))
            {
                _waypoint.ClearCoupling();
            }

            _waypoint.OverwriteLocation(waypoint.Location);
            _waypoint.CoupleToCarId = newCoupleToCarId;
            _waypoint.CoupleToCarEnd = waypoint.CoupleToCarEnd;
            _waypoint.ArrivingEnd = waypoint.ArrivingEnd;
            _waypoint.LocomotiveFrontEnd = null;
            _waypoint.PoseIndex = waypoint.PoseIndex;
            _waypoint.BodyToward = waypoint.BodyToward;

            _waypoint.ClearRefueling();

            _refuelService.CheckNearbyFuelLoaders(_waypoint);

            _onWaypointChange(_waypoint);
        }

        private void HandleInsertNewWaypoint(ManagedWaypoint insertedWaypoint)
        {
            _onWaypointInsert(insertedWaypoint, _waypoint.Id);
        }

        private void HandleAddWaypointToRoute(ManagedWaypoint addedWaypoint)
        {
            _refuelService.CheckNearbyFuelLoaders(addedWaypoint);
            _onWaypointInsert(addedWaypoint, _routeId);
        }

        public void Cancel()
        {
            if (_isPicking)
            {
                TrackLocationPicker.Shared.Cancel();
            }
        }

        private void OnCancelled()
        {
            ShowMessage(_cancelMessage);
            Cleanup();
        }

        private bool DidEscape()
        {
            OnCancelled();
            return true;
        }

        private void Cleanup()
        {
            _waypoint = null;
            _locomotive = null;
            _forRoute = false;
            _routeId = String.Empty;
            _onWaypointChange = null;
            _onWaypointSelected = null;
            _onWaypointInsert = null;
            _isPicking = false;
            ClearFootprint();
            StopPreview();
            _currentCarInfo = null;
            _hoverLocation = null;
            _nearSideResolved = false;
            _waypointMarker.gameObject.SetActive(value: false);
            _dontSnapToCars = [];

            GameInput.UnregisterEscapeHandler(GameInput.EscapeHandler.Transient);
        }

        private void DetermineTrainLength()
        {
            if (_locomotive == null)
            {
                _trainLength = 18f;
                _carLengths.Clear();
                _carLengths.Add(9f);
                _carLengths.Add(9f);
            }
            else
            {
                List<Car> adjacentLocos = GetAdjacentLocomotivesOrTenders(_locomotive);
                foreach (var car in adjacentLocos)
                {
                    _carLengths.Add(car.carLength);
                }
                _trainLength = AutoEngineerPlanner.CalculateTotalLength(adjacentLocos);
            }
        }

        private static List<Car> GetAdjacentLocomotivesOrTenders(Car locomotive)
        {
            List<Car> adjacentLocos = [];

            Car currentCar = locomotive;
            while (currentCar.TryGetAdjacentCar(Car.LogicalEnd.A, out Car nextCar))
            {
                if (nextCar.CarType.IsLocomotive || nextCar.CarType.IsTender)
                {
                    adjacentLocos.Add(currentCar);
                    currentCar = nextCar;
                }
                else
                {
                    break;
                }
            }

            adjacentLocos.Add(locomotive);

            while (currentCar.TryGetAdjacentCar(Car.LogicalEnd.B, out Car nextCar))
            {
                if (nextCar.CarType.IsLocomotive || nextCar.CarType.IsTender)
                {
                    adjacentLocos.Add(currentCar);
                    currentCar = nextCar;
                }
                else
                {
                    break;
                }
            }

            return adjacentLocos;
        }


        private void CyclePose()
        {
            Location? hoverLocation = _hoverLocation;
            if (hoverLocation.HasValue)
            {
                Location valueOrDefault = hoverLocation.GetValueOrDefault();
                TrackSegment.End nearSide = NearSideFor(valueOrDefault);
                _poseIndex = WaypointPoseCycle.Next(_poseIndex, (WaypointPoseOption option) => PoseIsAllowed(option, nearSide));
                _poseChosen = true;
                _poseKnown = true;
                _poseFits = true;
                UpdateFootprint(valueOrDefault);
            }
        }

        private bool PoseIsAllowed(WaypointPoseOption option, TrackSegment.End nearSide)
        {
            TrackSegment.End? coupleBodySide = _coupleBodySide;
            if (coupleBodySide.HasValue)
            {
                TrackSegment.End valueOrDefault = coupleBodySide.GetValueOrDefault();
                return option.BodySide(nearSide) == valueOrDefault;
            }

            return true;
        }

        private void UpdateFootprint(Location location)
        {
            SegmentIndicatorController shared = SegmentIndicatorController.Shared;
            if (shared == null)
            {
                return;
            }

            DetermineTrainLength();
            IReadOnlyList<JunctionTurn> readOnlyList = [];
            PlacementResult placementResult = CurrentPose.DerivePlacement(Graph.Shared, location, NearSideFor(location), _trainLength, null, null, _carLengths, _carFootprints, readOnlyList);
            _poseFits = placementResult.IsComplete;
            _poseOutcome = placementResult.Outcome;
            if (_footprintTokens.Count != _carLengths.Count || _footprintBuiltForPose != _poseIndex || _footprintBuiltForOutcome != placementResult.Outcome || _graph.GetDistanceBetweenClose(_footprintBuiltFor, location) >= 0.5f)
            {
                _footprintBuiltFor = location;
                _footprintBuiltForPose = _poseIndex;
                _footprintBuiltForOutcome = placementResult.Outcome;
                //RefreshCarColors();
                SyncFootprintTokens(shared);
                for (int i = 0; i < _footprintTokens.Count; i++)
                {
                    shared.UpdatePath(_footprintTokens[i], _carFootprints[i]);
                    shared.SetDirectional(_footprintTokens[i], true);
                }
            }

            for (int i = 0; i < _footprintTokens.Count; i++)
            {
                shared.SetColor(_footprintTokens[i], CarTagColor.NoDestination);
            }
        }

        private void ApplyFootprintColor()
        {
            SegmentIndicatorController shared = SegmentIndicatorController.Shared;
            if (shared == null)
            {
                return;
            }
            for (int i = 0; i < _footprintTokens.Count; i++)
            {
                shared.SetColor(_footprintTokens[i], CarTagColor.NoDestination);
            }
        }

        private void ClearFootprint()
        {
            if (_footprintTokens.Count == 0)
            {
                return;
            }

            SegmentIndicatorController shared = SegmentIndicatorController.Shared;
            if (shared != null)
            {
                foreach (string footprintToken in _footprintTokens)
                {
                    shared.Remove(footprintToken);
                }
            }

            _footprintTokens.Clear();
            _carLengths.Clear();
            _footprintBuiltForPose = -1;
        }

        private void SyncFootprintTokens(SegmentIndicatorController indicators)
        {
            while (_footprintTokens.Count > _carLengths.Count)
            {
                List<string> footprintTokens = _footprintTokens;
                indicators.Remove(footprintTokens[footprintTokens.Count - 1]);
                _footprintTokens.RemoveAt(_footprintTokens.Count - 1);
            }

            while (_footprintTokens.Count < _carLengths.Count)
            {
                _footprintTokens.Add(indicators.Add(Array.Empty<Location>(), CarTagColor.NoDestination));
            }
        }

        private TrackSegment.End NearSideFor(Location location)
        {
            if (_nearSideResolved && _nearSideMark.Equals(location))
            {
                return _nearSide;
            }

            Vector3 position = _graph.GetPosition(location);
            _nearSide = WaypointPoseCycle.CarryNearSide(_graph, location, _nearDirection, out _nearDirection);
            _nearSideMark = location;
            _nearSideMarkPosition = position;
            _nearSideResolved = true;
            return _nearSide;
        }


    }
}
