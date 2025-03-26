
using System;
using Newtonsoft.Json;
namespace stoplicht_controller.Classes;
public class Group
{
    public int Id { get; set; }

    [JsonProperty("intersects_with")]
    public List<int> IntersectsWith { get; set; }

    [JsonProperty("is_inverse_of")]
    public object IsInverseOf { get; set; }

    [JsonProperty("extends_to")]
    public object ExtendsTo { get; set; }

    [JsonProperty("vehicle_type")]
    public List<string> VehicleType { get; set; }

    [JsonProperty("lanes")]
    public Dictionary<string, Lane> Lanes { get; set; }

    [JsonProperty("is_physical_barrier")]
    public bool IsPhysicalBarrier { get; set; }
}
