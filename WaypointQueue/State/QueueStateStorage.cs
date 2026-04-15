using Game.AccessControl;
using Game.State;
using KeyValue.Runtime;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WaypointQueue.State
{
    internal class QueueStateStorage : IPropertyAccessControlDelegate, IDisposable
    {
        private readonly KeyValueObject _keyValueObject;

        public readonly string ObjectId = "_waypointQueueMod.queueStates";

        // Each key should be a locomotive id
        public int Count => _keyValueObject.Keys.Count();

        public Dictionary<string, LocoWaypointState> GetAll()
        {
            Dictionary<string, LocoWaypointState> pairs = [];
            foreach (var locoId in _keyValueObject.Keys)
            {
                LocoWaypointState state = LocoWaypointState.FromPropertyValue(_keyValueObject[locoId]);
                pairs.Add(locoId, state);
            }
            return pairs;
        }

        public bool ContainsQueueForLocoId(string locoId)
        {
            return _keyValueObject.Keys.Contains(locoId);
        }

        public void SetLocoQueue(LocoWaypointState state)
        {
            _keyValueObject[state.LocomotiveId] = state.ToPropertyValue();
        }

        public LocoWaypointState GetQueueByLocoId(string locoId)
        {
            if (_keyValueObject.Keys.Contains(locoId))
            {
                LocoWaypointState state = LocoWaypointState.FromPropertyValue(_keyValueObject[locoId]);
                return state;
            }
            return null;
        }

        public QueueStateStorage(KeyValueObject keyValueObject)
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

        public IDisposable ObserveQueueState(string locoId, Action<LocoWaypointState> action, bool callInitial)
        {
            return _keyValueObject.Observe(locoId, (Value value) =>
            {
                LocoWaypointState state = LocoWaypointState.FromPropertyValue(value);
                action(state);
            }, callInitial);
        }

        public void MigrateQueueStatesFromJsonStringsToPropertyValues()
        {
            foreach (var locoId in _keyValueObject.Keys)
            {
                string json = _keyValueObject[locoId];
                LocoWaypointState state = JsonConvert.DeserializeObject<LocoWaypointState>(json);
                _keyValueObject[locoId] = state.ToPropertyValue();
            }
        }
    }
}
