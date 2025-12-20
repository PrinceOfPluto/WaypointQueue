namespace WaypointQueue.UI
{
    internal class DropdownOption(string label, string value)
    {
        public string Label { get; set; } = label;
        public string Value { get; set; } = value;
    }
}
