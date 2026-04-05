using Game.AccessControl;
using Game.State;
using KeyValue.Runtime;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using WaypointQueue.Model;

namespace WaypointQueue.State
{
    internal class WaypointModStorage : IPropertyAccessControlDelegate, IDisposable
    {
        private readonly KeyValueObject _keyValueObject;

        public readonly string ObjectId = "_waypointQueueMod";

        public readonly string KeyVersion = "version";

        public readonly string KeyRouteAssignments = "routeAssignments";
        public readonly string KeyDelayedBleedAirCutEntries = "delayedBleedAirCars";

        public string Version
        {
            get
            {
                if (_keyValueObject != null && _keyValueObject.Keys.Contains(KeyVersion))
                {
                    return _keyValueObject[KeyVersion].StringValue;
                }
                return string.Empty;
            }
            set
            {
                _keyValueObject[KeyVersion] = value;
            }
        }

        public Dictionary<string, RouteAssignment> RouteAssignments
        {
            get
            {
                if (_keyValueObject != null && _keyValueObject.Keys.Contains(KeyRouteAssignments))
                {
                    string json = _keyValueObject[KeyRouteAssignments];
                    return JsonConvert.DeserializeObject<Dictionary<string, RouteAssignment>>(json);
                }
                return [];
            }
            set
            {
                _keyValueObject[KeyRouteAssignments] = JsonConvert.SerializeObject(value);
            }
        }

        public List<DelayedBleedAirCutEntry> DelayedBleedAirCutEntries
        {
            get
            {
                if (_keyValueObject != null && _keyValueObject.Keys.Contains(KeyDelayedBleedAirCutEntries))
                {
                    string json = _keyValueObject[KeyDelayedBleedAirCutEntries];
                    _keyValueObject.Get(KeyDelayedBleedAirCutEntries);
                    return JsonConvert.DeserializeObject<List<DelayedBleedAirCutEntry>>(json);
                }
                return [];
            }
            set
            {
                _keyValueObject[KeyDelayedBleedAirCutEntries] = JsonConvert.SerializeObject(value);
            }
        }

        public WaypointModStorage(KeyValueObject keyValueObject)
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

        public IDisposable ObserveRouteAssignments(Action<Dictionary<string, RouteAssignment>> action, bool callInitial)
        {
            return _keyValueObject.Observe(KeyRouteAssignments, (Value value) =>
            {
                action(RouteAssignments);
            }, callInitial);
        }
    }
}
