using Model;
using System;
using System.Collections.Generic;
using System.Linq;
using static Model.Car;

namespace WaypointQueue
{
    internal class CarUtils
    {
        public static string CarListToString(List<Car> cars)
        {
            return String.Join("-", cars.Select(c => $"[{c.Ident}]"));
        }

        public static string LogicalEndToString(LogicalEnd logicalEnd)
        {
            return logicalEnd == LogicalEnd.A ? "A" : "B";
        }
    }
}
