using HarmonyLib;
using Model;
using Model.Ops;
using System.Linq;
using static Model.Ops.OpsController;

namespace WaypointQueue.Wrappers
{
    // Wrapped for easier test mocking
    internal class OpsControllerWrapper : IOpsControllerWrapper
    {
        public bool TryGetCarDesination(Car car, out OpsCarPosition destination)
        {
            bool hasDestination = OpsController.Shared.TryGetDestinationInfo(car, out string _, out bool _, out _, out destination);
            return hasDestination;
        }

        public bool TryResolveOpsCarPosition(string opsCarPositionIdentifier, out OpsCarPosition opsCarPosition)
        {
            try
            {
                opsCarPosition = OpsController.Shared.ResolveOpsCarPosition(opsCarPositionIdentifier);
                return true;
            }
            catch (InvalidOpsCarPositionException)
            {
                opsCarPosition = new();
                return false;
            }
        }

        public Area AreaForCarPosition(OpsCarPosition position)
        {
            return OpsController.Shared.AreaForCarPosition(position);
        }

        public Industry GetIndustryById(string id)
        {
            return OpsController.Shared.AllIndustries.Where(i => i.identifier == id).FirstOrDefault();
        }

        public Area GetAreaById(string id)
        {
            return OpsController.Shared.Areas.Where(i => i.identifier == id).FirstOrDefault();
        }

        public IndustryComponent IndustryComponentForPosition(OpsCarPosition position)
        {
            return Traverse.Create(OpsController.Shared).Method("IndustryComponentForPosition", [typeof(OpsCarPosition)], [position]).GetValue<IndustryComponent>();
        }
    }
}
