using Model;
using Model.Ops;

namespace WaypointQueue.Wrappers
{
    internal interface IOpsControllerWrapper
    {
        Area AreaForCarPosition(OpsCarPosition position);
        Area GetAreaById(string id);
        Industry GetIndustryById(string id);
        IndustryComponent IndustryComponentForPosition(OpsCarPosition position);
        bool TryGetCarDesination(Car car, out OpsCarPosition destination);
        bool TryResolveOpsCarPosition(string opsCarPositionIdentifier, out OpsCarPosition opsCarPosition);
    }
}