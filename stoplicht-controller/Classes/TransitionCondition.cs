public class TransitionCondition
{
    // Let op: sommige condities bevatten bijvoorbeeld een sensor of een andere groep.
    public string Type { get; set; } = string.Empty;
    public string Sensor { get; set; } = string.Empty;
    public bool? SensorState { get; set; }
    public int? Group { get; set; }
    public string TrafficLightState { get; set; } = string.Empty;
}