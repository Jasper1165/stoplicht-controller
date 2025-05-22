using stoplicht_controller.Classes;
using stoplicht_controller.Enums;
using stoplicht_controller.Managers;

public class StatePublisher : IDisposable
{
    private readonly TrafficLightController _tlc;
    private readonly BridgeController _bc;
    private readonly Communicator _comm;
    private readonly Dictionary<string, string> _payload = new();
    private readonly object _lock = new();

    public StatePublisher(
        TrafficLightController tlc,
        BridgeController bc,
        Communicator comm)
    {
        _tlc = tlc;
        _bc = bc;
        _comm = comm;

        // In plaats van een timer:
        _tlc.StateChanged += Publish;
        _bc.StateChanged += Publish;
    }

    private void Publish()
    {
        lock (_lock)
        {
            _payload.Clear();

            // 1) verkeerslichten (inclusief conflict-lichten via Direction.Color)
            foreach (var dir in _tlc.directions)
            {
                if (dir.TrafficLights == null) continue;
                var kleur = dir.Color switch
                {
                    LightColor.Green => "groen",
                    LightColor.Orange => "oranje",
                    _ => "rood"
                };
                foreach (var tl in dir.TrafficLights)
                    _payload[tl.Id] = kleur;
            }

            // 2) brug
            _bc.ProcessBridgeSensorData();
            var brugState = _bc.CurrentBridgeState;
            _payload[BridgeController.BRIDGE_LIGHT_ID] = brugState;

            // 3) conflicts - VERWIJDER DEZE SECTIE HELEMAAL
            // De conflict-lichten worden al afgehandeld via de Direction.Color properties hierboven

            // debug-dump
            // Console.WriteLine($"[Publish] payload: {string.Join(", ", _payload.Select(kv => $"{kv.Key}={kv.Value}"))}");

            // 4) publiceer
            _comm.PublishMessage("stoplichten", _payload);
        }
    }

    public void Dispose()
    {
        _tlc.StateChanged -= Publish;
        _bc.StateChanged -= Publish;
    }
}
