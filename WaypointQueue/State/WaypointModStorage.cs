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
                    return _keyValueObject[KeyRouteAssignments].DictionaryValue.ToDictionary(k => k.Key, v => RouteAssignment.FromPropertyValue(v.Value));
                }
                return [];
            }
            set
            {
                if (value == null)
                {
                    _keyValueObject[KeyRouteAssignments] = null;
                    return;
                }
                _keyValueObject[KeyRouteAssignments] = Value.Dictionary(value.ToDictionary(k => k.Key, v => v.Value.ToPropertyValue()));
            }
        }

        public List<DelayedBleedAirCutEntry> DelayedBleedAirCutEntries
        {
            get
            {
                if (_keyValueObject != null && _keyValueObject.Keys.Contains(KeyDelayedBleedAirCutEntries))
                {
                    Value value = _keyValueObject[KeyDelayedBleedAirCutEntries];

                    if (value.IsNull || value.Type != KeyValue.Runtime.ValueType.Array)
                    {
                        return [];
                    }

                    return value.ArrayValue.Select(v => DelayedBleedAirCutEntry.FromPropertyValue(v)).ToList();
                }
                return [];
            }
            set
            {
                if (value == null)
                {
                    _keyValueObject[KeyDelayedBleedAirCutEntries] = null;
                    return;
                }
                _keyValueObject[KeyDelayedBleedAirCutEntries] = Value.Array(value.Select(e => e.ToPropertyValue()).ToList());
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

        public void MigrateModStorageFromJsonStringsToPropertyValues()
        {
            string bleedAirCarsJson = _keyValueObject[KeyDelayedBleedAirCutEntries];
            if (!String.IsNullOrEmpty(bleedAirCarsJson))
            {
                DelayedBleedAirCutEntries = JsonConvert.DeserializeObject<List<DelayedBleedAirCutEntry>>(bleedAirCarsJson);
            }

            string assignmentsJson = _keyValueObject[KeyRouteAssignments];
            if (!String.IsNullOrEmpty(assignmentsJson))
            {
                RouteAssignments = JsonConvert.DeserializeObject<Dictionary<string, RouteAssignment>>(assignmentsJson);
            }
        }

        public void ResetData()
        {
            var dictionary = new Dictionary<string, Value>();
            _keyValueObject.ResetData(dictionary, SetValueOrigin.Local);
        }
    }
}
