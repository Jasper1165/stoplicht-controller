using Newtonsoft.Json;

public class PriorityVehicle
{
    [JsonProperty("baan")]
    public string Lane { get; set; } = string.Empty;

    [JsonProperty("simulatie_tijd_ms")]
    public int SimulationTimeMs { get; set; }

    [JsonProperty("prioriteit")]
    public int Priority { get; set; }

    // Override Equals for comparison
    public override bool Equals(object? obj)
    {
        if (obj is not PriorityVehicle other)
            return false;

        return Lane == other.Lane &&
               SimulationTimeMs == other.SimulationTimeMs &&
               Priority == other.Priority;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Lane, SimulationTimeMs, Priority);
    }
}