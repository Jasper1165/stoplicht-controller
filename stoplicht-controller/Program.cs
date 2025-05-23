using stoplicht_controller.Classes;
using stoplicht_controller.Managers;

class Program
{
    // Global state: list of all traffic directions
    public static List<Direction> Directions { get; set; } = new List<Direction>();
    // Queue for priority vehicles awaiting service
    public static List<Direction> PriorityVehicleQueue { get; set; } = new List<Direction>();

    // Communicator configuration: addresses and topics for messaging
    static string subscriberAddress = "tcp://127.0.0.1:5556"; // address to receive messages
    static string publisherAddress = "tcp://127.0.0.1:5555";  // address to send messages
    static string[] topics = { "sensoren_rijbaan", "voorrangsvoertuig", "sensoren_speciaal", "sensoren_bruggen" };

    // Bridge instance for detecting bridge state and jams
    static Bridge bridge = new Bridge();

    // Communicator instance to handle pub/sub interactions
    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);
    // Token source to allow graceful cancellation of async loops
    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

    /// <summary>
    /// Application entry point: loads data, initializes managers and controllers,
    /// then starts background loops until the user requests shutdown.
    /// </summary>
    static async Task Main(string[] args)
    {
        // Load intersection definitions into the global Directions list
        IntersectionDataLoader.LoadIntersectionData(Directions);

        // Initialize data processors and controllers
        var specialSensorDataProcessor = new SpecialSensorDataProcessor(communicator, bridge);
        var trafficLightController = new TrafficLightController(communicator, Directions, bridge);
        var priorityVehicleManager = new PriorityVehicleManager(communicator, Directions, trafficLightController);
        var priorityCalculator = new PriorityCalculator(); // instance of priority calculation strategy

        // Set up the publisher to send combined traffic and bridge states on each change
        using var statePublisher = new StatePublisher(
            trafficLightController,
            trafficLightController.bridgeController,   // requires public BridgeController property
            communicator
        );

        // Start background tasks for messaging and control loops
        var subscriberTask = Task.Run(() => communicator.StartSubscriber(), cancellationTokenSource.Token);
        var priorityTask = Task.Run(() => priorityVehicleManager.Update(), cancellationTokenSource.Token);
        var sensorSpecialTask = Task.Run(() => specialSensorDataProcessor.SpecialSensorLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);
        var trafficLightTask = Task.Run(() => trafficLightController.TrafficLightCycleLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);

        Console.WriteLine("Press Enter to stop...");
        Console.ReadLine();

        // Signal cancellation and wait for all loops to complete
        cancellationTokenSource.Cancel();
        await Task.WhenAll(subscriberTask, priorityTask, sensorSpecialTask, trafficLightTask);
    }
}
