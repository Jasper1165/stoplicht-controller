public class TrafficLight
{
    public string Id { get; set; } = string.Empty;
    public List<Sensor> Sensors { get; set; } = new List<Sensor>();
}
