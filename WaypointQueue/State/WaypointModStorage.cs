using Game.AccessControl;
using Game.State;
using KeyValue.Runtime;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WaypointQueue.State
{
    internal class WaypointModStorage : IPropertyAccessControlDelegate, IDisposable
    {
        private readonly KeyValueObject _modKeyValueObject;

        public readonly string ObjectId = "_waypointQueueMod";

        public readonly string KeyRoutes = "routes";

        public readonly string KeyRouteAssignments = "routeAssignments";

        public List<RouteDefinition> Routes
        {
            get
            {
                if (_modKeyValueObject != null)
                {
                    string json = _modKeyValueObject[KeyRoutes].StringValue;
                    return JsonConvert.DeserializeObject<List<RouteDefinition>>(json);
                }

                return [];
            }
            set
            {
                string json = JsonConvert.SerializeObject(value);
                _modKeyValueObject[KeyRoutes] = Value.String(json);
            }
        }

        public Dictionary<string, RouteAssignment> RouteAssignments
        {
            get
            {
                if (_modKeyValueObject != null)
                {
                    string json = _modKeyValueObject[KeyRouteAssignments].StringValue;
                    return JsonConvert.DeserializeObject<Dictionary<string, RouteAssignment>>(json);
                }

                return [];
            }
            set
            {
                string json = JsonConvert.SerializeObject(value);
                _modKeyValueObject[KeyRouteAssignments] = Value.String(json);
            }
        }

        public WaypointModStorage(KeyValueObject keyValueObject)
        {
            _modKeyValueObject = keyValueObject;
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
            if (!(_modKeyValueObject == null))
            {
                UnityEngine.Object.DestroyImmediate(_modKeyValueObject);
                StateManager.Shared.UnregisterPropertyObject(ObjectId);
            }
        }

        public IDisposable ObserveRoutes(Action<List<RouteDefinition>> action, bool initial)
        {
            return _modKeyValueObject.Observe(KeyRoutes, delegate
            {
                action(Routes);
            }, initial);
        }

        public IDisposable ObserveRouteAssignments(Action<Dictionary<string, RouteAssignment>> action, bool initial)
        {
            return _modKeyValueObject.Observe(KeyRouteAssignments, delegate
            {
                action(RouteAssignments);
            }, initial);
        }
    }
}
