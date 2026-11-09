using Game.Events;
using Model.AI.Waypoints;
using System.Collections;
using System.Collections.Generic;
using Track;
using Track.Search;
using UI;
using UI.Tags;
using UI.Waypoints;
using UnityEngine;

namespace WaypointQueue.UI
{
    internal class WaypointPreviewer : MonoBehaviour
    {
        private ManagedWaypoint _waypoint;
        private WaypointPoseOption _waypointPoseOption;
        private TrackSegment.End _nearSide;

        private readonly float _genericLocoLength = 13f;
        private readonly List<string> _footprintTokens = [];
        private readonly List<List<Location>> _carFootprints = [];

        private readonly float KeepAliveSeconds = 60f;

        private Coroutine _coroutine;


        private void OnEnable()
        {
            EventBus.Subscribe<MapDidUnloadEvent>(this, OnMapDidUnload);
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe(this);
        }

        private void OnMapDidUnload(MapDidUnloadEvent @event)
        {
            EndPreview();
        }

        public bool IsDisplayingPreview(ManagedWaypoint waypoint)
        {
            if (_waypoint == null || waypoint == null) return false;

            return _waypoint.Id == waypoint.Id;
        }

        public void ShowWaypointPreview(ManagedWaypoint waypoint)
        {
            try
            {
                ClearFootprint();
                _waypoint = waypoint;

                if (waypoint.PoseIndex != -1)
                {
                    _waypointPoseOption = WaypointPoseCycle.Options[waypoint.PoseIndex];
                    _nearSide = TrackSegment.End.A;
                }
                else if (waypoint.Locomotive != null && waypoint.LocomotiveFrontEnd.HasValue)
                {
                    var consistEnd = PosedWaypoint.FrontEnd(waypoint.Locomotive);
                    bool frontOnMark = waypoint.LocomotiveFrontEnd.Value == consistEnd;

                    var bodySide = waypoint.BodyToward == BodyToward.SegmentEndA ? TrackSegment.End.A : TrackSegment.End.B;
                    _nearSide = WaypointPoseCycle.NearSideOf(Graph.Shared.Current, waypoint.Location, waypoint.Locomotive, out _);

                    bool bodyOnNearSide = _nearSide == bodySide;

                    _waypointPoseOption = new WaypointPoseOption(frontOnMark, bodyOnNearSide);
                }
                else
                {
                    ModLocator.Loader.LogError($"Cannot determine waypoint preview direction for waypoint: {waypoint}");
                    return;
                }

                ShowFootprint(waypoint.Location);

                if (_coroutine != null)
                {
                    StopCoroutine(_coroutine);
                }
                _coroutine = StartCoroutine(FootprintTimeoutLoop());
            }
            catch (System.Exception e)
            {
                ModLocator.Loader.LogError($"ShowWaypointPreview encountered an exception: {e}");
            }
        }

        public void EndPreview()
        {
            try
            {
                Cleanup();
            }
            catch (System.Exception e)
            {
                ModLocator.Loader.LogError($"Failed WaypointPreviewer cleanup: {e}");
            }
        }

        private void ShowFootprint(Track.Location location)
        {
            SegmentIndicatorController shared = SegmentIndicatorController.Shared;
            if (shared == null)
            {
                ModLocator.Loader.LogDebug($"Segment indicator controller was null");
                return;
            }

            var trainLength = _genericLocoLength;
            List<float> carLengths = [_genericLocoLength];

            IReadOnlyList<JunctionTurn> readOnlyList = [];
            // used to fetch locations for car footprints
            PlacementResult _ = _waypointPoseOption.DerivePlacement(Graph.Shared, location, _nearSide, trainLength, null, null, carLengths, _carFootprints, readOnlyList);

            _footprintTokens.Add(shared.Add([], CarTagColor.NoDestination));

            shared.UpdatePath(_footprintTokens[0], _carFootprints[0]);
            shared.SetDirectional(_footprintTokens[0], true);

            shared.SetColor(_footprintTokens[0], CarTagColor.NoDestination);
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

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        private void Cleanup()
        {
            _waypoint = null;
            ClearFootprint();
        }

        private IEnumerator FootprintTimeoutLoop()
        {
            yield return new WaitForSeconds(KeepAliveSeconds);
            ClearFootprint();
        }
    }
}
