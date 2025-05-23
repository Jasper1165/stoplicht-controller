using stoplicht_controller.Classes;
using stoplicht_controller.Enums;
using stoplicht_controller.Managers;

public class StatePublisher : IDisposable
{
    // TrafficLightController instance to listen for light state changes
    private readonly TrafficLightController _tlc;
    // BridgeController instance to listen for bridge state changes
    private readonly BridgeController _bc;
    // Communicator used to publish combined state messages
    private readonly Communicator _comm;
    // Payload dictionary collecting all light and bridge states for publication
    private readonly Dictionary<string, string> _payload = new();
    // Lock object to ensure thread-safe publication
    private readonly object _lock = new();

    /// <summary>
    /// Subscribes to state change events from the traffic light and bridge controllers.
    /// </summary>
    public StatePublisher(
        TrafficLightController tlc,
        BridgeController bc,
        Communicator comm)
    {
        _tlc = tlc;
        _bc = bc;
        _comm = comm;

        // Instead of a timer, publish on each state change event
        _tlc.StateChanged += Publish;
        _bc.StateChanged += Publish;
    }

    /// <summary>
    /// Gathers current traffic light and bridge states, then publishes them.
    /// This method is invoked whenever either controller raises a StateChanged event.
    /// </summary>
    private void Publish()
    {
        lock (_lock)
        {
            // Clear previous payload entries
            _payload.Clear();

            // 1) Collect traffic light states (including conflict lights via Direction.Color)
            foreach (var dir in _tlc.directions)
            {
                if (dir.TrafficLights == null) continue;

                // Map enum color to Dutch string for publication
                var kleur = dir.Color switch
                {
                    LightColor.Green => "groen",
                    LightColor.Orange => "oranje",
                    _ => "rood"
                };
                foreach (var tl in dir.TrafficLights)
                    _payload[tl.Id] = kleur;
            }

            // 2) Update bridge sensor data and include current bridge state
            _bc.ProcessBridgeSensorData();
            var brugState = _bc.CurrentBridgeState;
            _payload[BridgeController.BRIDGE_LIGHT_ID] = brugState;

            // 3) Publish combined states on the "stoplichten" topic
            _comm.PublishMessage("stoplichten", _payload);
        }
    }

    /// <summary>
    /// Unsubscribes from state change events to stop publishing updates.
    /// </summary>
    public void Dispose()
    {
        _tlc.StateChanged -= Publish;
        _bc.StateChanged -= Publish;
    }
}
