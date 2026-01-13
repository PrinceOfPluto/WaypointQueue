using Model;
using Model.Ops;

namespace WaypointQueue.Wrappers
{
    internal interface IOpsControllerWrapper
    {
        Area AreaForCarPosition(OpsCarPosition position);
        bool TryGetAreaById(string id, out Area area);
        bool TryGetIndustryById(string id, out Industry industry);
        IndustryComponent IndustryComponentForPosition(OpsCarPosition position);
        bool TryGetCarDesination(Car car, out OpsCarPosition destination);
        bool TryResolveOpsCarPosition(string opsCarPositionIdentifier, out OpsCarPosition opsCarPosition);
    }
}