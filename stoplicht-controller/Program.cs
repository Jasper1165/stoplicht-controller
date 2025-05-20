using stoplicht_controller.Classes;
using stoplicht_controller.Managers;

class Program
{
    // Globale state
    public static List<Direction> Directions { get; set; } = new List<Direction>();
    public static List<Direction> PriorityVehicleQueue { get; set; } = new List<Direction>();

    // Communicator instellingen
    static string subscriberAddress = "tcp://127.0.0.1:5556"; // static string subscriberAddress = "tcp://10.121.17.233:5556";
    static string publisherAddress = "tcp://127.0.0.1:5555"; // static string publisherAddress = "tcp://10.121.17.233:5555";
    static string[] topics = { "sensoren_rijbaan", "voorrangsvoertuig", "sensoren_speciaal", "sensoren_bruggen" };

    static Bridge bridge = new Bridge();

    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);
    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

    static async Task Main(string[] args)
    {
        // Laad de intersection data
        IntersectionDataLoader.LoadIntersectionData(Directions);

        // Initialize TrafficLightController (it sets up last-green times internally)

        // Managers
        var specialSensorDataProcessor = new SpecialSensorDataProcessor(communicator, bridge);
        var trafficLightController = new TrafficLightController(communicator, Directions, bridge);
        var priorityVehicleManager = new PriorityVehicleManager(communicator, Directions, trafficLightController);
        var priorityCalculator = new PriorityCalculator(); // Create an instance of IPriorityCalculator

        // Start subscriber en loopen
        var subscriberTask = Task.Run(() => communicator.StartSubscriber(), cancellationTokenSource.Token);
        var priorityTask = Task.Run(() => priorityVehicleManager.Update(), cancellationTokenSource.Token);
        var sensorSpecialTask = Task.Run(() => specialSensorDataProcessor.SpecialSensorLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);
        var trafficLightTask = Task.Run(() => trafficLightController.TrafficLightCycleLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);

        Console.WriteLine("Druk op Enter om te stoppen...");
        Console.ReadLine();

        cancellationTokenSource.Cancel();
        await Task.WhenAll(subscriberTask, priorityTask, sensorSpecialTask, trafficLightTask);
    }
}
