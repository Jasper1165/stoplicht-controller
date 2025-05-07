
using Newtonsoft.Json;

public class PriorityVehicleQueue
{
    [JsonProperty("queue")]
    public List<PriorityVehicle> Queue { get; set; } = new List<PriorityVehicle>();
}