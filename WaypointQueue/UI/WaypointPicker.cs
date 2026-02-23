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
        private Action<ManagedWaypoint> _onWaypointChange;
        private Coroutine _coroutine;

        private Camera _camera;
        private Transform _waypointMarker;

        private Action<Location, string> _onWaypointSelected;

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

        public void StartAdjustingWaypoint(ManagedWaypoint waypoint, Action<ManagedWaypoint> onWaypointChange)
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

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
            }

            _coroutine = StartCoroutine(Loop());
            ShowMessage("Click to move waypoint for " + waypoint.Locomotive.Ident);

            GameInput.RegisterEscapeHandler(GameInput.EscapeHandler.Transient, DidEscape);
        }

        public void StartInsertingWaypoint(ManagedWaypoint beforeWaypoint)
        {
            if (_waypointMarker == null)
            {
                InitWaypointMarker();
            }

            AutoEngineerDestinationPicker.Shared.Cancel();
            Cancel();

            _waypoint = beforeWaypoint;
            _locomotive = beforeWaypoint.Locomotive;
            _onWaypointSelected = HandleAddNewWaypoint;

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
            }

            _coroutine = StartCoroutine(Loop());
            ShowMessage("Click to insert a waypoint for " + beforeWaypoint.Locomotive.Ident);

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
            _refuelService.CheckNearbyFuelLoaders(_waypoint);

            _onWaypointChange(_waypoint);
        }

        private void HandleAddNewWaypoint(Location location, string coupleToCarId)
        {
            WaypointQueueController.Shared.InsertWaypoint(_locomotive, location, coupleToCarId, _waypoint.Id);
        }

        public void Cancel()
        {
            _waypoint = null;
            _locomotive = null;
            _onWaypointChange = null;
            _onWaypointSelected = null;
            _waypointMarker.gameObject.SetActive(value: false);
            StopLoop();
        }

        private bool DidEscape()
        {
            if (_onWaypointChange != null)
            {
                ShowMessage("Cancelled waypoint adjustment");
            }
            else
            {
                ShowMessage("Cancelled waypoint insert");
            }
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
                Hit? result = null;
                HashSet<Car> value;
                using (CollectionPool<HashSet<Car>, Car>.Get(out value))
                {
                    shared.CheckForCarsAtPoint(position, 2f, value, valueOrDefault);
                    foreach (Car item in value)
                    {
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
