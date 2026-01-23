using System;
using System.Collections.Generic;

namespace WaypointQueue
{
    internal class ServiceProvider
    {
        private readonly Dictionary<Type, object> _serviceRegistry = [];

        public void AddSingleton<T>(T service)
        {
            _serviceRegistry[typeof(T)] = service;
        }

        public T GetService<T>() where T : class
        {
            if (_serviceRegistry.TryGetValue(typeof(T), out object service))
            {
                return (T)service;
            }

            throw new InvalidOperationException($"Service for type {typeof(T).FullName} is not registered.");
        }
    }
}
