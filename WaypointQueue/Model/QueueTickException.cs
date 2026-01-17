using System;

namespace WaypointQueue.Model
{
    internal class QueueTickException(string message, Exception innerException) : Exception(message, innerException)
    {
    }
}
