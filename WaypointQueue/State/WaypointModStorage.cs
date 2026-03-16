using Game.AccessControl;
using Game.State;
using KeyValue.Runtime;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WaypointQueue.State
{
    internal class WaypointModStorage : IPropertyAccessControlDelegate, IDisposable
    {
        private readonly KeyValueObject _modKeyValueObject;

        public readonly string ObjectId = "_waypointQueueMod";

        public readonly string KeyVersion = "version";

        public readonly string KeyLocoWaypointStates = "locoWaypointStates";

        public readonly string KeyRoutes = "routes";

        public readonly string KeyRouteAssignments = "routeAssignments";

        public string Version
        {
            get
            {
                if (_modKeyValueObject != null && _modKeyValueObject.Keys.Contains(KeyVersion))
                {
                    return _modKeyValueObject[KeyVersion].StringValue;
                }
                return string.Empty;
            }
            set
            {
                _modKeyValueObject[KeyVersion] = value;
            }
        }

        public Dictionary<string, LocoWaypointState> LocoWaypointStates
        {
            get
            {
                if (_modKeyValueObject != null && _modKeyValueObject.Keys.Contains(KeyLocoWaypointStates))
                {
                    string json = _modKeyValueObject[KeyLocoWaypointStates].StringValue;
                    return JsonConvert.DeserializeObject<Dictionary<string, LocoWaypointState>>(json);
                }

                return [];
            }
            set
            {
                string json = JsonConvert.SerializeObject(value);
                _modKeyValueObject[KeyLocoWaypointStates] = Value.String(json);
            }
        }

        public List<RouteDefinition> Routes
        {
            get
            {
                if (_modKeyValueObject != null && _modKeyValueObject.Keys.Contains(KeyRoutes))
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
                if (_modKeyValueObject != null && _modKeyValueObject.Keys.Contains(KeyRouteAssignments))
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
    }
}
