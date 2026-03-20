using HarmonyLib;
using Helpers;
using Model;
using System;
using System.Collections;
using System.Collections.Generic;
using Track;
using UI;
using UI.Common;
using UnityEngine;
using UnityEngine.Pool;
using WaypointQueue.Services;
using WaypointQueue.UUM;

namespace WaypointQueue.UI
{
    internal class WaypointPicker : MonoBehaviour
    {
        private struct Hit(Location location, (Car car, Car.End end)? carInfo)
        {
            public Location Location = location;

            public (Car car, Car.End end)? CarInfo = carInfo;
        }

        private ManagedWaypoint _waypoint;
        private Car _locomotive;
        private HashSet<Car> _dontSnapToCars = [];
        private Action<ManagedWaypoint> _onWaypointChange;
        private Coroutine _coroutine;

        private Camera _camera;
        private Transform _waypointMarker;

        private Action<Location, string> _onWaypointSelected;
        private Action<ManagedWaypoint, string> _onWaypointInsert;
        private string _routeId;
        private bool _forRoute;

        private string _cancelMessage = String.Empty;

        private RefuelService _refuelService;

        private static WaypointPicker _shared;
        public static WaypointPicker Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = FindObjectOfType<WaypointPicker>();
                }

                return _shared;
            }
        }

        private void Awake()
        {
            _refuelService = Loader.ServiceProvider.GetService<RefuelService>();
        }

        private void InitWaypointMarker()
        {
            Loader.LogDebug($"Waypoint adjuster InitWaypointMarker");
            Transform destinationMarker = Traverse.Create(AutoEngineerDestinationPicker.Shared).Field<Transform>("destinationMarker").Value;
            _waypointMarker = destinationMarker;
        }

        public bool MouseClicked
        {
            get
            {
                if (GameInput.IsMouseOverUI(out var _, out var _))
                {
                    return false;
                }

                return GameInput.shared.PrimaryPressEndedThisFrame;
            }
        }

        public void StartAdjustingWaypoint(ManagedWaypoint waypoint, Action<ManagedWaypoint> onWaypointChange, bool forRoute = false)
        {
            if (_waypointMarker == null)
            {
                InitWaypointMarker();
            }

            AutoEngineerDestinationPicker.Shared.Cancel();
            Cancel();

            _waypoint = waypoint;
            _onWaypointChange = onWaypointChange;
            _onWaypointSelected = AdjustWaypoint;
            _forRoute = forRoute;
            _cancelMessage = "Cancelled waypoint adjustment";

            _dontSnapToCars = [];
            if (!forRoute)
            {
                _dontSnapToCars = [.. waypoint.Locomotive.EnumerateCoupled()];
            }

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
            }

            _coroutine = StartCoroutine(Loop());

            string message = "Click to move waypoint for route";
            if (!forRoute)
            {
                message = "Click to move waypoint for " + waypoint.Locomotive.Ident;
            }
            ShowMessage(message);

            GameInput.RegisterEscapeHandler(GameInput.EscapeHandler.Transient, DidEscape);
        }

        public void StartInsertingWaypoint(ManagedWaypoint beforeWaypoint, Action<ManagedWaypoint, string> onWaypointInsert, bool forRoute = false)
        {
            if (_waypointMarker == null)
            {
                InitWaypointMarker();
            }

            AutoEngineerDestinationPicker.Shared.Cancel();
            Cancel();

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

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
            }

            _coroutine = StartCoroutine(Loop());

            string message = "Click to insert a waypoint for route";
            if (!forRoute)
            {
                message = "Click to insert a waypoint for " + beforeWaypoint.Locomotive.Ident;
            }
            ShowMessage(message);

            GameInput.RegisterEscapeHandler(GameInput.EscapeHandler.Transient, DidEscape);
        }

        public void StartPickingWaypointForRoute(Action<ManagedWaypoint, string> onWaypointInsert, string routeId)
        {
            if (_waypointMarker == null)
            {
                InitWaypointMarker();
            }

            AutoEngineerDestinationPicker.Shared.Cancel();
            Cancel();

            _onWaypointSelected = HandleAddWaypointToRoute;
            _onWaypointInsert = onWaypointInsert;
            _routeId = routeId;
            _cancelMessage = "Cancelled adding waypoint";
            _dontSnapToCars = [];

            _coroutine = StartCoroutine(Loop());

            ShowMessage("Click to add a waypoint for route");

            GameInput.RegisterEscapeHandler(GameInput.EscapeHandler.Transient, DidEscape);
        }

        private void ShowMessage(string message)
        {
            Toast.Present(message, ToastPosition.Bottom);
        }

        private void AdjustWaypoint(Location newLocation, string newCoupleToCarId)
        {
            if (!String.IsNullOrEmpty(newCoupleToCarId))
            {
                _waypoint.ClearCoupling();
            }

            _waypoint.OverwriteLocation(newLocation);
            _waypoint.CoupleToCarId = newCoupleToCarId;

            _waypoint.ClearRefueling();

            if (!_forRoute)
            {
                _refuelService.CheckNearbyFuelLoaders(_waypoint);
            }

            _onWaypointChange(_waypoint);
        }

        private void HandleInsertNewWaypoint(Location location, string coupleToCarId)
        {
            ManagedWaypoint insertedWaypoint = new ManagedWaypoint((BaseLocomotive)_locomotive, location, coupleToCarId);
            _onWaypointInsert(insertedWaypoint, _waypoint.Id);
        }

        private void HandleAddWaypointToRoute(Location location, string coupleToCarId)
        {
            ManagedWaypoint addedWaypoint = new ManagedWaypoint(null, location, coupleToCarId);
            _onWaypointInsert(addedWaypoint, _routeId);
        }

        public void Cancel()
        {
            _waypoint = null;
            _locomotive = null;
            _forRoute = false;
            _routeId = String.Empty;
            _onWaypointChange = null;
            _onWaypointSelected = null;
            _onWaypointInsert = null;
            _waypointMarker.gameObject.SetActive(value: false);
            _dontSnapToCars = [];
            StopLoop();
        }

        private bool DidEscape()
        {
            ShowMessage(_cancelMessage);
            Cancel();
            return true;
        }

        private void StopLoop()
        {
            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
                _coroutine = null;
            }

            GameInput.UnregisterEscapeHandler(GameInput.EscapeHandler.Transient);
        }

        private IEnumerator Loop()
        {
            Hit valueOrDefault;
            Location location;
            while (true)
            {
                Hit? hit = HitLocation();
                if (hit.HasValue)
                {
                    valueOrDefault = hit.GetValueOrDefault();
                    location = valueOrDefault.Location;
                    Graph.PositionRotation positionRotation = Graph.Shared.GetPositionRotation(location);
                    _waypointMarker.position = WorldTransformer.GameToWorld(positionRotation.Position);
                    _waypointMarker.rotation = positionRotation.Rotation;
                    _waypointMarker.gameObject.SetActive(value: true);
                    if (MouseClicked)
                    {
                        break;
                    }
                }
                else
                {
                    _waypointMarker.gameObject.SetActive(value: false);
                }

                yield return null;
            }

            _waypointMarker.gameObject.SetActive(value: false);
            Loader.LogDebug($"WaypointPicker Hit: {valueOrDefault.Location} {valueOrDefault.CarInfo?.car} {valueOrDefault.CarInfo?.end}");
            _onWaypointSelected(location, valueOrDefault.CarInfo?.car?.id ?? "");
            Cancel();
        }

        private Hit? HitLocation()
        {
            if (!MainCameraHelper.TryGetIfNeeded(ref _camera))
            {
                return null;
            }

            Location? location = Graph.Shared.LocationFromMouse(_camera);
            if (location.HasValue)
            {
                Location valueOrDefault = location.GetValueOrDefault();
                TrainController shared = TrainController.Shared;
                Vector3 position = Graph.Shared.GetPosition(valueOrDefault);
                float num = 2f;
                Hit? result = new Hit(valueOrDefault, null);
                HashSet<Car> value;
                using (CollectionPool<HashSet<Car>, Car>.Get(out value))
                {
                    shared.CheckForCarsAtPoint(position, 2f, value, valueOrDefault);
                    foreach (Car item in value)
                    {
                        if (_dontSnapToCars.Contains(item))
                        {
                            continue;
                        }

                        if (!item[item.EndToLogical(Car.End.F)].IsCoupled)
                        {
                            Location location2 = Graph.Shared.LocationByMoving(item.LocationF, 0.5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true);
                            float distanceBetweenClose = Graph.Shared.GetDistanceBetweenClose(valueOrDefault, location2);
                            if (distanceBetweenClose < num)
                            {
                                num = distanceBetweenClose;
                                result = new Hit(location2, (item, Car.End.F));
                            }
                        }

                        if (!item[item.EndToLogical(Car.End.R)].IsCoupled)
                        {
                            Location location3 = Graph.Shared.LocationByMoving(item.LocationR, -0.5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true).Flipped();
                            float distanceBetweenClose2 = Graph.Shared.GetDistanceBetweenClose(valueOrDefault, location3);
                            if (distanceBetweenClose2 < num)
                            {
                                num = distanceBetweenClose2;
                                result = new Hit(location3, (item, Car.End.R));
                            }
                        }
                    }

                    if (value.Count > 0)
                    {
                        return result;
                    }
                }

                return new Hit(valueOrDefault, null);
            }

            return null;
        }
    }
}
