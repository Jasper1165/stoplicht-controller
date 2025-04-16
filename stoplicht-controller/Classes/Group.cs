public class Group
{
    // Initialiseer de collecties met een lege lijst zodat ze nooit null zijn.
    public List<int> IntersectsWith { get; set; } = new List<int>();
    public List<int> IsInverseOf { get; set; } = new List<int>();
    public List<int> ExtendsTo { get; set; } = new List<int>();

    // Als VehicleType een string is en je verwacht dat deze wel altijd een waarde heeft:
    public string VehicleType { get; set; } = string.Empty;

    // Als TrafficLights een lijst is:
    public List<TrafficLight> TrafficLights { get; set; } = new List<TrafficLight>();

    // Nieuwe properties voor overgangscondities: gebruik een woordenboek met keys (bijv. "green", "red") en de lijst met condities.
    public Dictionary<string, List<TransitionCondition>> TransitionRequirements { get; set; }
        = new Dictionary<string, List<TransitionCondition>>();
    public Dictionary<string, List<TransitionCondition>> TransitionBlockers { get; set; }
        = new Dictionary<string, List<TransitionCondition>>();
}
