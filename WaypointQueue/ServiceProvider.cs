using System;
using System.Collections.Generic;

namespace WaypointQueue
{
    internal class ServiceProvider
    {
        private readonly Dictionary<Type, Func<object>> _serviceRegistry = [];

        public void AddSingleton<TService>(Func<TService> implementationFactory) where TService : class
        {
            _serviceRegistry[typeof(TService)] = () =>
            {
                if (implementationFactory == null)
                {
                    throw new InvalidOperationException($"Service factory for type {typeof(TService).FullName} is null.");
                }
                return implementationFactory();
            };
        }

        public TService GetService<TService>() where TService : class
        {
            if (_serviceRegistry.TryGetValue(typeof(TService), out var factory))
            {
                return (TService)factory();
            }

            throw new InvalidOperationException($"Service factory for type {typeof(TService).FullName} is not registered.");
        }
    }
}
