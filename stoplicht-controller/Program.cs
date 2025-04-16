using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;
using stoplicht_controller.Managers;

class Program
{
    // Globale state
    public static List<Direction> Directions { get; set; } = new List<Direction>();
    public static Bridge Bridge { get; set; } = new Bridge();
    public static List<Direction> PriorityVehicleQueue { get; set; } = new List<Direction>();

    // Communicator instellingen
    static string subscriberAddress = "tcp://10.121.17.233:5556"; // 84 (Marnick)
    static string publisherAddress = "tcp://10.121.17.233:5555";
    static string[] topics = { "sensoren_rijbaan", "voorrangsvoertuig", "sensoren_speciaal", "sensoren_bruggen" };
    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);
    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

    static async Task Main(string[] args)
    {
        // Laad de intersection data (richtingen en verkeerslichten)
        IntersectionDataLoader.LoadIntersectionData(Directions);
        // Initialiseer lastGreenTimes in een aparte helper in de TrafficLightController (of in een state manager)
        TrafficLightController.InitializeLastGreenTimes(Directions);

        // Initialiseer de managers
        var specialSensorDataProcessor = new SpecialSensorDataProcessor(communicator, Bridge);
        var priorityVehicleManager = new PriorityVehicleManager(communicator, Directions, PriorityVehicleQueue);
        var trafficLightController = new TrafficLightController(communicator, Directions);

        // Start de communicator subscriber in een aparte taak (parallel)
        Task subscriberTask = Task.Run(() => communicator.StartSubscriber(), cancellationTokenSource.Token);
        // Start de afzonderlijke asynchrone loops
        Task priorityTask = Task.Run(() => priorityVehicleManager.PriorityVehicleHandlerLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);
        Task sensorSpecialTask = Task.Run(() => specialSensorDataProcessor.SpecialSensorLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);
        Task trafficLightTask = Task.Run(() => trafficLightController.TrafficLightCycleLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);

        Console.WriteLine("Druk op Enter om te stoppen...");
        Console.ReadLine();

        cancellationTokenSource.Cancel();
        await Task.WhenAll(subscriberTask, priorityTask, trafficLightTask, sensorSpecialTask);
    }
}