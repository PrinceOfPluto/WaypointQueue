using HarmonyLib;
using Model;
using System.Collections.Generic;
using Track;

namespace WaypointQueue.Wrappers
{
    // Wrapped for easier test mocking
    internal class TrainControllerWrapper
    {
        public bool TryGetCarForId(string carId, out Car car)
        {
            return TrainController.Shared.TryGetCarForId(carId, out car);
        }

        public int CalculateNumHandbrakes(List<Car> cars, int minimumHandbrakes = 1, int maximumHandbrakes = 3)
        {
            return Traverse.Create(TrainController.Shared).Method("CalculateNumHandbrakes", [typeof(List<Car>), typeof(int), typeof(int)], [cars, minimumHandbrakes, maximumHandbrakes]).GetValue<int>();
        }

        public Car CheckForCarAtLocation(Location location)
        {
            return TrainController.Shared.CheckForCarAtPoint(Graph.Shared.GetPosition(location));
        }

        public IEnumerable<string> GetNearbyCarIds(Location location, float searchRadius)
        {
            return TrainController.Shared.CarIdsInRadius(location.GetPosition(), searchRadius);
        }
    }
}
