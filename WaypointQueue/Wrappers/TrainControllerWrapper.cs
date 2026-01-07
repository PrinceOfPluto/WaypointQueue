using Game.Messages;
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

        public void ApplyHandbrakesAsNeeded(List<Car> cars)
        {
            Traverse.Create(TrainController.Shared).Method("ApplyHandbrakesAsNeeded", [typeof(List<Car>), typeof(PlaceTrainHandbrakes)], [cars, PlaceTrainHandbrakes.Automatic]).GetValue();
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
