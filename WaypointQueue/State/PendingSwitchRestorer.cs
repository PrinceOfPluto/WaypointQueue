using Game.Events;
using Game.State;
using HarmonyLib;
using Model;
using System;
using System.Collections;
using System.Collections.Generic;
using Track;
using Track.Search;
using UnityEngine;
using WaypointQueue.Services;

namespace WaypointQueue.State
{
    internal class PendingSwitchRestorer : MonoBehaviour
    {
        internal struct PendingSwitch
        {
            public string NodeId;

            // The position to put the switch back to, and the position the AE left it in.
            public bool OriginalThrown;
            public bool LeftThrown;

            public string LocomotiveId;
            public string LocomotiveIdent;

            // Switch position (game space) and when it was added (unscaled time).
            public Vector3 Position;
            public float AddedAt;
        }

        private const float IntervalSeconds = 2f;

        // Reset a switch only while the train that left it is still close by, as the game does for
        // switches a train has just passed. A switch left longer, or by a train that has gone, may
        // now be relied on by another train, so it is dropped instead of thrown.
        private const float NearbyMeters = 200f;
        private const float MaxAgeSeconds = 600f;

        private static readonly Dictionary<string, PendingSwitch> _pending = [];

        private Coroutine _coroutine;

        private ICarService _carService;


        private void Awake()
        {
            _carService = ModLocator.ServiceProvider.GetService<ICarService>();
        }

        private void OnEnable()
        {
            EventBus.Subscribe<MapWillUnloadEvent>(this, OnMapWillUnload);
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe(this);
        }

        private void OnMapWillUnload(MapWillUnloadEvent @event)
        {
            _pending.Clear();

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        internal void Add(PendingSwitch pending)
        {
            if (!StateManager.IsHost) return;

            _pending[pending.NodeId] = pending;

            if (_coroutine == null)
            {
                _coroutine = StartCoroutine(Tick());
            }
        }

        // Hands a waiting switch over to a route that is throwing it: the position to put it back to.
        // It's no longer retried here.
        internal bool TryTake(string nodeId, out bool originalThrown)
        {
            originalThrown = false;
            if (!_pending.TryGetValue(nodeId, out PendingSwitch pending))
                return false;
            _pending.Remove(nodeId);
            originalThrown = pending.OriginalThrown;
            return true;
        }

        private IEnumerator Tick()
        {
            var wait = new WaitForSecondsRealtime(IntervalSeconds);
            while (_pending.Count > 0)
            {
                yield return wait;
                CheckPending();
            }
            _coroutine = null;
        }

        private void CheckPending()
        {
            if (_pending.Count == 0 || !StateManager.IsHost || TrainController.Shared == null)
                return;

            TrainController trainController = TrainController.Shared;
            Graph graph = Graph.Shared;
            List<string> done = [];
            foreach (PendingSwitch pending in _pending.Values)
            {
                try
                {
                    TrackNode node = graph.GetNode(pending.NodeId);
                    if (node == null || node.isThrown == pending.OriginalThrown)
                    {
                        // Gone, or already back where it was.
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (node.isThrown != pending.LeftThrown)
                    {
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: switch {pending.NodeId} was changed by someone else; leaving it");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (node.IsCTCSwitch && !node.IsCTCSwitchUnlocked)
                    {
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: switch {pending.NodeId} is under CTC control; leaving it to the dispatcher");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (Time.unscaledTime - pending.AddedAt > MaxAgeSeconds)
                    {
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: switch {pending.NodeId} was left too long; leaving it");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (IsOnCurrentRoute(pending, node) || !trainController.CanSetSwitch(node, pending.OriginalThrown, out _))
                        continue;

                    if (!IsTrainNearby(pending, graph))
                    {
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: train is no longer near switch {pending.NodeId}; leaving it");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (trainController.TrySetSwitch(pending.NodeId, pending.OriginalThrown, "AE " + pending.LocomotiveIdent, out string error))
                        ModLocator.Loader.LogDebug($"Loco {pending.LocomotiveIdent}: reset switch {pending.NodeId} now that the train is clear");
                    else
                        ModLocator.Loader.LogError($"Loco {pending.LocomotiveIdent}: couldn't reset switch {pending.NodeId}: {error}");

                    done.Add(pending.NodeId);
                }
                catch (Exception ex)
                {
                    ModLocator.Loader.LogError($"Restoring switch {pending.NodeId} failed: {ex}");
                    done.Add(pending.NodeId);
                }
            }

            foreach (string id in done)
                _pending.Remove(id);
        }

        // Whether any car of the train that left the switch is still within NearbyMeters of it.
        private bool IsTrainNearby(PendingSwitch pending, Graph graph)
        {
            var locomotive = _carService.GetLocoById(pending.LocomotiveId);
            if (locomotive == null)
                return false;

            foreach (Car car in _carService.EnumerateCoupled(locomotive))
            {
                if (Vector3.Distance(graph.GetPosition(car.LocationA), pending.Position) <= NearbyMeters
                    || Vector3.Distance(graph.GetPosition(car.LocationB), pending.Position) <= NearbyMeters)
                    return true;
            }
            return false;
        }

        // The AE's current route still needs the switch where it is, for example a move that comes
        // back over it.
        private bool IsOnCurrentRoute(PendingSwitch pending, TrackNode node)
        {
            var locomotive = _carService.GetLocoById(pending.LocomotiveId);
            if (locomotive == null || locomotive.AutoEngineerPlanner == null)
                return false;

            var planner = locomotive.AutoEngineerPlanner;

            List<RouteSearch.Step> route = (List<RouteSearch.Step>)Traverse.Create(planner).Field("_route").GetValue();
            if (route == null)
                return false;

            int startStepIndex = (int)Traverse.Create(planner).Field("_startStepIndex").GetValue();

            for (int i = Math.Max(0, startStepIndex); i < route.Count; i++)
            {
                if (route[i].Node == node)
                    return true;
            }
            return false;
        }
    }
}
