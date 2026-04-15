using KeyValue.Runtime;

namespace WaypointQueue.Model
{
    public interface IStorableProperty
    {
        public abstract Value ToPropertyValue();
    }
}
