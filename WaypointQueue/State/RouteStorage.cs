using Game.AccessControl;
using Game.State;
using KeyValue.Runtime;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WaypointQueue.State
{
    internal class RouteStorage : IPropertyAccessControlDelegate, IDisposable
    {
        private readonly KeyValueObject _keyValueObject;

        public readonly string ObjectId = "_waypointQueueMod.routes";

        // Each key should be a route id

        public int Count => _keyValueObject.Keys.Count();

        public Dictionary<string, RouteDefinition> GetAll()
        {
            Dictionary<string, RouteDefinition> pairs = [];
            foreach (var locoId in _keyValueObject.Keys)
            {
                string json = _keyValueObject[locoId];
                RouteDefinition route = JsonConvert.DeserializeObject<RouteDefinition>(json);
                pairs.Add(locoId, route);
            }
            return pairs;
        }

        public void SetRoute(RouteDefinition route)
        {
            _keyValueObject[route.Id] = JsonConvert.SerializeObject(route);
        }

        public RouteDefinition GetByRouteId(string routeId)
        {
            if (_keyValueObject.Keys.Contains(routeId))
            {
                return JsonConvert.DeserializeObject<RouteDefinition>(_keyValueObject[routeId]);
            }
            return null;
        }

        public RouteStorage(KeyValueObject keyValueObject)
        {
            _keyValueObject = keyValueObject;
            StateManager.Shared.RegisterPropertyObject(ObjectId, keyValueObject, this);
        }

        public AuthorizationRequirementInfo AuthorizationRequirementForPropertyWrite(string key)
        {
            return key switch
            {
                _ => AuthorizationRequirement.MinimumLevelCrew
            };
        }

        public void Dispose()
        {
            if (!(_keyValueObject == null))
            {
                UnityEngine.Object.DestroyImmediate(_keyValueObject);
                StateManager.Shared.UnregisterPropertyObject(ObjectId);
            }
        }

        public IDisposable ObserveKeyChanges(Action<string, KeyChange> action)
        {
            return _keyValueObject.ObserveKeyChanges(action);
        }

        public IDisposable ObserveRoute(string routeId, Action<RouteDefinition> action, bool callInitial)
        {
            return _keyValueObject.Observe(routeId, (Value value) =>
            {
                string json = value.StringValue;
                var route = JsonConvert.DeserializeObject<RouteDefinition>(_keyValueObject[routeId]);
                action(route);
            }, callInitial);
        }
    }
}
