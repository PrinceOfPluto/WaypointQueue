using Model;
using System.Collections.Generic;
using Track;

namespace WaypointQueue.Services
{
    internal interface ICarService
    {
        void BleedAirOnCut(List<Car> cars);
        Car.LogicalEnd ClosestLogicalEndTo(Car car, Location location);
        void ConnectAir(Car car);
        void SetAnglecocks(List<Car> cars, bool open);
        List<Car> EnumerateCoupled(Car car, Car.LogicalEnd fromEnd);
        List<Car> EnumerateAdjacentCarsTowardEnd(Car car, Car.LogicalEnd directionToCount, bool inclusive = false);
        List<Car> FilterAnySplitLocoTenderPairs(List<Car> carsToCut);
        Car.LogicalEnd GetEndRelativeToWaypoint(Car car, Location waypointLocation, bool useFurthestEnd);
        (Car.LogicalEnd closest, Car.LogicalEnd furthest) GetEndsRelativeToLocation(Car car, Location location);
        Car.LogicalEnd GetOppositeEnd(Car.LogicalEnd logicalEnd);
        (Location closest, Location furthest) GetTrainEndLocations(Car car, Location location, out float closestDistance, out Car closestCar, out Car furthestCar, out Car.LogicalEnd closestEnd, out Car.LogicalEnd furthestEnd);
        void SetHandbrakesOnCut(List<Car> cars);
        void UpdateCarsForAE(BaseLocomotive locomotive);
        bool IsCarLocomotiveType(Car car);
        bool ShouldPeriodicReroute(BaseLocomotive locomotive);
        void TogglePeriodicRerouteForLoco(BaseLocomotive locomotive);
        BaseLocomotive GetLocoById(string locoId);
    }
}