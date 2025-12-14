using Model;
using System;
using System.Collections;
using UI;
using UI.Common;
using UnityEngine;

namespace WaypointQueue
{
    internal class WaypointCarPicker : MonoBehaviour
    {
        private ManagedWaypoint _waypoint;
        private Action<ManagedWaypoint> _onWaypointChange;
        private Coroutine _coroutine;
        private bool _carWasPicked;

        private static WaypointCarPicker _shared;
        public static WaypointCarPicker Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = UnityEngine.Object.FindObjectOfType<WaypointCarPicker>();
                }

                return _shared;
            }
        }

        public bool IsListeningForCarClick
        {
            get
            {
                return _waypoint != null;
            }
        }

        public void StartPickingCar(ManagedWaypoint waypoint, Action<ManagedWaypoint> onWaypointChange)
        {
            _waypoint = waypoint;
            _onWaypointChange = onWaypointChange;

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
            }

            _coroutine = StartCoroutine(Loop());
            ShowMessage("Click a car to set coupling target");

            GameInput.RegisterEscapeHandler(GameInput.EscapeHandler.Transient, DidEscape);
        }

        public void PickCar(Car car)
        {
            _carWasPicked = true;
            _waypoint.CouplingSearchResultCar = car;
            _waypoint.CouplingSearchText = car.Ident.ToString();
            _onWaypointChange(_waypoint);
        }

        public void Cancel()
        {
            if (_coroutine != null)
            {
                _waypoint = null;
                _onWaypointChange = null;
                _carWasPicked = false;
                ShowMessage("Cancelled coupling target selection");
                StopLoop();
            }
        }

        private bool DidEscape()
        {
            Cancel();
            return true;
        }

        private void StopLoop()
        {
            _waypoint = null;
            _onWaypointChange = null;
            _carWasPicked = false;

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
                _coroutine = null;
            }

            GameInput.UnregisterEscapeHandler(GameInput.EscapeHandler.Transient);
        }

        private void ShowMessage(string message)
        {
            Toast.Present(message, ToastPosition.Bottom);
        }

        private IEnumerator Loop()
        {
            while (!_carWasPicked)
            {
                yield return null;
            }

            _carWasPicked = false;

            StopLoop();
        }
    }
}
