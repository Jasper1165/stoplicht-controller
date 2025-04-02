using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

class Program
{
    static public List<Direction> Directions { get; set; } = new List<Direction>();
    static public Bridge Bridge { get; set; } = new Bridge();
    static public List<Direction> PriorityVehicleQueue { get; set; } = new List<Direction>();

    static string subscriberAddress = "tcp://192.168.1.150:5555";
    static string publisherAddress = "tcp://192.168.1.150:5555";
    static string[] topics = { "sensoren_rijbaan", "tijd", "voorrangsvoertuig" };
    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);

    private const int GREEN_DURATION = 5000; // 5 seconden
    private static DateTime lastSwitchTime = DateTime.Now;
    private static int lastSwitchIndex = 0;
    private static List<Direction> currentGreenDirections = new List<Direction>();

    static void Main()
    {
        LoadIntersectionData();
        communicator.StartPublisher("sensoren_rijbaan");
        communicator.StartSubscriber();

        // Initialiseer de cyclus als er al richtingdata is
        if (Directions.Any())
        {
            SwitchTrafficLights();
        }

        while (true)
        {
            Update();
            Thread.Sleep(1000);
        }
    }

    static void Update()
    {
        ProcessSensorMessage();
        ProcessPriorityVehicleMessage();
        UpdateTrafficLights();
    }

    public static void UpdateTrafficLights()
    {
        DateTime now = DateTime.Now;
        double timeDifference = (now - lastSwitchTime).TotalMilliseconds;
        Console.WriteLine($"Time since last switch: {timeDifference} ms");

        // Als er nog geen groene richtingen zijn, doe een initiële switch.
        if (currentGreenDirections == null || !currentGreenDirections.Any())
        {
            Console.WriteLine("Geen richting ingesteld voor groen! Initiële switch...");
            SwitchTrafficLights();
            return;
        }

        // Als de groene periode (5 seconden) voorbij is, wissel dan naar de volgende groep.
        if (timeDifference >= GREEN_DURATION)
        {
            SwitchTrafficLights();
        }
    }
private static void SwitchTrafficLights()
{
    var availableDirections = Directions
        .Where(d => GetPriority(d) > 0)
        .OrderByDescending(d => GetPriority(d)) // Eerst hoogste prioriteit
        .ThenBy(d => d.Id)                      // Daarna laagste ID
        .ToList();

    if (!availableDirections.Any())
    {
        Console.WriteLine("Geen beschikbare richtingen gevonden om naar groen te schakelen.");
        return;
    }

    // Start selectie vanaf lastSwitchIndex (Round-Robin)
    var newGreenGroup = new List<Direction>();
    int count = availableDirections.Count;

    for (int i = 0; i < count; i++)
    {
        int index = (lastSwitchIndex + i) % count;
        var candidate = availableDirections[index];

        bool conflicts = newGreenGroup.Any(green =>
            green.Intersections.Contains(candidate.Id) ||
            candidate.Intersections.Contains(green.Id));

        if (!conflicts)
        {
            newGreenGroup.Add(candidate);
        }
    }

    // **Controleer of de nieuwe groep niet dezelfde is als de vorige**
    if (newGreenGroup.SequenceEqual(currentGreenDirections))
    {
        lastSwitchIndex = (lastSwitchIndex + 1) % count; // Ga verder in de lijst
        Console.WriteLine("Zelfde groene groep, probeer volgende index.");
        return;
    }

    // **Zet de vorige groep op rood**
    foreach (var dir in currentGreenDirections)
    {
        dir.Color = LightColor.Red;
        Console.WriteLine($"Direction {dir.Id} staat nu op rood.");
    }
    currentGreenDirections.Clear();

    // **Zet de nieuwe groep op groen**
    currentGreenDirections = newGreenGroup;
    foreach (var dir in currentGreenDirections)
    {
        dir.Color = LightColor.Green;
        Console.WriteLine($"Direction {dir.Id} staat nu op groen met prioriteit {GetPriority(dir)}.");
    }

    // **Update de tijd en index voor de volgende ronde**
    lastSwitchTime = DateTime.Now;
    lastSwitchIndex = (lastSwitchIndex + newGreenGroup.Count) % count; // Spring over de gekozen richtingen heen
}



    // Bepaalt de prioriteit van een richting: hogere prioriteit als beide sensoren (file) actief zijn.// Bepaalt de prioriteit van een richting: hogere waarde als beide sensoren (file) actief zijn
    private static int GetPriority(Direction direction)
    {
        int priority = 0;
        foreach (var trafficLight in direction.TrafficLights)
        {
            int activatedSensors = trafficLight.Sensors.Count(s => s.IsActivated);
            if (activatedSensors == 2) // Beide sensoren ingedrukt: file
            {
                priority += 2;
            }
            else if (activatedSensors == 1) // Eén sensor ingedrukt
            {
                priority += 1;
            }
        }
        return priority;
    }

    static void ProcessPriorityVehicleMessage()
    {
        if (string.IsNullOrEmpty(communicator.PriorityVehicleData))
            return;

        var priorityVehicleData = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(communicator.PriorityVehicleData);
        if (priorityVehicleData == null || !priorityVehicleData.ContainsKey("queue"))
            return;

        var queue = priorityVehicleData["queue"];
        foreach (var item in queue)
        {
            if (!item.TryGetValue("baan", out var baanObj) || !item.TryGetValue("prioriteit", out var priorityObj))
                continue;

            string baan = baanObj.ToString();
            if (!int.TryParse(priorityObj.ToString(), out int priority))
                continue;

            var directionId = int.Parse(baan.Split('.')[0]);
            if (!PriorityVehicleQueue.Any(d => d.Id == directionId))
            {
                PriorityVehicleQueue.Add(new Direction { Id = directionId, Priority = priority });
            }
        }
    }

    static void ProcessSensorMessage()
    {
        if (string.IsNullOrEmpty(communicator.LaneSensorData))
            return;

        var sensorData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
        if (sensorData == null)
            return;

        foreach (var (trafficLightId, sensors) in sensorData)
        {
            var trafficLight = Directions
                .SelectMany(d => d.TrafficLights)
                .FirstOrDefault(tl => tl.Id == trafficLightId);

            if (trafficLight == null)
                continue;

            foreach (var sensor in trafficLight.Sensors)
            {
                if (sensor.Position == SensorPosition.Front && sensors.TryGetValue("voor", out bool frontValue))
                {
                    sensor.IsActivated = frontValue;
                }
                else if (sensor.Position == SensorPosition.Back && sensors.TryGetValue("achter", out bool backValue))
                {
                    sensor.IsActivated = backValue;
                }
            }
        }
    }

    static void LoadIntersectionData()
    {
        string jsonFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "intersectionData", "lanes.json");

        if (!File.Exists(jsonFilePath))
        {
            Console.WriteLine($"Het bestand is niet gevonden: {jsonFilePath}");
            return;
        }

        string jsonContent = File.ReadAllText(jsonFilePath);
        JObject jsonObject = JObject.Parse(jsonContent);
        var groupsData = jsonObject["groups"]?.ToObject<Dictionary<string, JObject>>();

        if (groupsData == null)
            return;

        foreach (var (groupIdStr, groupObj) in groupsData)
        {
            if (!int.TryParse(groupIdStr, out int groupId))
                continue;

            var direction = new Direction { Id = groupId };

            if (groupObj["intersects_with"] is JArray intersectsArray)
            {
                direction.Intersections = intersectsArray.Select(i => i.ToObject<int>()).ToList();
            }

            if (groupObj["lanes"] is JObject lanesObj)
            {
                foreach (var laneProperty in lanesObj.Properties())
                {
                    if (!int.TryParse(laneProperty.Name, out int laneId))
                        continue;

                    var trafficLight = new TrafficLight { Id = $"{groupId}.{laneId}" };

                    trafficLight.Sensors.AddRange(new List<Sensor>
                    {
                        new Sensor { Position = SensorPosition.Front, IsActivated = false },
                        new Sensor { Position = SensorPosition.Back, IsActivated = false }
                    });

                    direction.TrafficLights.Add(trafficLight);
                }
            }

            Directions.Add(direction);
        }

        Console.WriteLine("Intersection data geladen..");
    }
}
