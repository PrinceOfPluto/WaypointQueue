using System;
using System.Collections.Generic;
using System.Linq;

namespace WaypointQueue
{
    [Serializable]
    public class RouteAssignment
    {
        public string LocoId;
        public string RouteId;
        public bool Loop;
    }

    public static class RouteAssignmentRegistry
    {
        private static readonly Dictionary<string, RouteAssignment> _byLocoId = new Dictionary<string, RouteAssignment>();

        public static event Action OnChanged;


        public static (string routeId, bool loop) Get(string locoId)
        {
            if (string.IsNullOrEmpty(locoId)) return (null, false);
            if (_byLocoId.TryGetValue(locoId, out var a)) return (a.RouteId, a.Loop);
            return (null, false);
        }

        public static RouteAssignment GetAssignment(string locoId)
        {
            if (string.IsNullOrEmpty(locoId)) return null;
            _byLocoId.TryGetValue(locoId, out var a);
            return a;
        }

        public static List<RouteAssignment> All() => _byLocoId.Values.ToList();


        public static void Set(string locoId, string routeId, bool loop)
        {
            if (string.IsNullOrEmpty(locoId)) return;

            if (!_byLocoId.TryGetValue(locoId, out var a))
            {
                a = new RouteAssignment { LocoId = locoId };
                _byLocoId[locoId] = a;
            }

            a.RouteId = routeId;
            a.Loop = loop;

            OnChanged?.Invoke();
        }

        public static void Remove(string locoId)
        {
            if (string.IsNullOrEmpty(locoId)) return;
            if (_byLocoId.Remove(locoId))
                OnChanged?.Invoke();
        }

        public static void Clear()
        {
            if (_byLocoId.Count == 0) return;
            _byLocoId.Clear();
            OnChanged?.Invoke();
        }


        public static void ReplaceAll(IEnumerable<RouteAssignment> items)
        {
            _byLocoId.Clear();
            if (items != null)
            {
                foreach (var a in items)
                {
                    if (!string.IsNullOrEmpty(a?.LocoId))
                        _byLocoId[a.LocoId] = a;
                }
            }
            OnChanged?.Invoke();
        }
    }
}
