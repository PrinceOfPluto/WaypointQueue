using Game.Messages;
using Model;
using Model.AI;
using System;
using System.Reflection;
using Track;
using UI.EngineControls;
using WaypointQueue.UUM;

namespace WaypointQueue.Services
{
    internal class AutoEngineerService(ICarService carService)
    {
        public void SendToWaypoint(AutoEngineerOrdersHelper ordersHelper, Location location, string coupleToCarId = null)
        {
            (Location, string)? maybeWaypoint = (location.Clamped(), coupleToCarId);
            ordersHelper.SetOrdersValue(null, null, null, null, maybeWaypoint);
        }

        public AutoEngineerOrdersHelper GetOrdersHelper(Car locomotive)
        {
            Type plannerType = typeof(AutoEngineerPlanner);
            FieldInfo fieldInfo = plannerType.GetField("_persistence", BindingFlags.NonPublic | BindingFlags.Instance);
            AutoEngineerPersistence persistence = (AutoEngineerPersistence)fieldInfo.GetValue((locomotive as BaseLocomotive).AutoEngineerPlanner);
            AutoEngineerOrdersHelper ordersHelper = new(locomotive, persistence);
            return ordersHelper;
        }

        public string GetPlannerStatus(BaseLocomotive locomotive)
        {
            Type plannerType = typeof(AutoEngineerPlanner);
            FieldInfo fieldInfo = plannerType.GetField("_persistence", BindingFlags.NonPublic | BindingFlags.Instance);
            AutoEngineerPersistence persistence = (AutoEngineerPersistence)fieldInfo.GetValue((locomotive as BaseLocomotive).AutoEngineerPlanner);
            return persistence.PlannerStatus;
        }

        public void CancelActiveOrders(Car loco)
        {
            Loader.Log($"Canceling active orders for {loco.Ident}");
            AutoEngineerOrdersHelper ordersHelper = GetOrdersHelper(loco);

            if (ordersHelper.Mode == AutoEngineerMode.Waypoint)
            {
                ordersHelper.ClearWaypoint();
            }
        }

        public bool HasActiveWaypoint(AutoEngineerOrdersHelper ordersHelper)
        {
            return ordersHelper.Orders.Waypoint.HasValue;
        }

        public bool IsInWaypointMode(AutoEngineerOrdersHelper ordersHelper)
        {
            return ordersHelper.Orders.Mode == Game.Messages.AutoEngineerMode.Waypoint;
        }

        public bool AtEndOfTrack(BaseLocomotive loco)
        {
            string plannerStatus = GetPlannerStatus(loco);
            return plannerStatus == "End of Track";
        }

        public bool IsNearWaypoint(ManagedWaypoint waypoint)
        {
            (Location _, Location _) = carService.GetTrainEndLocations(waypoint, out float closestDistance, out _, out _);
            return closestDistance < 10;
        }

        public int GetOrdersMaxSpeed(Car locomotive)
        {
            var ordersHelper = GetOrdersHelper(locomotive);
            return ordersHelper.Orders.MaxSpeedMph;
        }
    }
}
