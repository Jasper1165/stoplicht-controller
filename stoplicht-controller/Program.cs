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

    static string subscriberAddress = "tcp://10.121.17.84:5556";
    static string publisherAddress = "tcp://0.0.0.0:5557";
    static string[] topics = { "sensoren_rijbaan", "tijd", "voorrangsvoertuig" };
    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);

    // Duur in milliseconden:
    private const int ORANGE_DURATION = 5000; // 5 seconden oranje
    private const int GREEN_DURATION = 8000;  // 5 seconden groen

    private static DateTime lastSwitchTime = DateTime.Now;
    private static DateTime lastOrangeTime = DateTime.Now;
    private static int lastSwitchIndex = 0;
    private static List<Direction> currentGreenDirections = new List<Direction>();
    private static List<Direction> currentOrangeDirections = new List<Direction>();
    // We gebruiken geen aparte currentRedDirections; rood is de status buiten de actieve groepen

    static void Main()
    {
        LoadIntersectionData();
        communicator.StartSubscriber();

        // Initialiseer de cyclus als er al richtingdata is
        if (Directions.Any())
        {
            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        while (true)
        {
            Update();
            Console.WriteLine(communicator.LaneSensorData);
            // Thread.Sleep(100);
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
        double orangeDifference = (now - lastOrangeTime).TotalMilliseconds;

        // Eerst: als we in de orange fase zitten, wachten we tot die voorbij is.
        if (currentOrangeDirections.Any())
        {
            if (orangeDifference >= ORANGE_DURATION)
            {
                // Oranje fase voorbij: zet alle oranje lichten naar rood en start de nieuwe schakeling.
                SetLightsToRed();
                SwitchTrafficLights();
                SendTrafficLightStates();
            }
            return; // Anders niets doen
        }

        // Als er een groene groep aanwezig is
        if (currentGreenDirections.Any())
        {
            // Indien de groene periode voorbij is, start de overgang naar orange.
            if (timeDifference >= GREEN_DURATION)
            {
                SetLightsToOrange();
                lastOrangeTime = DateTime.Now;
                SendTrafficLightStates();
            }
            return;
        }

        // Als er geen groene groep aanwezig is, start een nieuwe schakeling.
        if (!currentGreenDirections.Any())
        {
            SwitchTrafficLights();
            SendTrafficLightStates();
        }
    }

    private static void SwitchTrafficLights()
    {
        // Verkrijg alle richtingen met een prioriteit hoger dan 0
        var availableDirections = Directions
            .Where(d => GetPriority(d) > 0)
            .OrderByDescending(d => GetPriority(d))
            .ThenBy(d => d.Id)
            .ToList();

        if (!availableDirections.Any())
        {
            Console.WriteLine("Geen beschikbare richtingen gevonden om naar groen te schakelen.");
            return;
        }

        // Selecteer via een round-robin principe de nieuwe groene groep
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

        // Als de nieuwe groep gelijk is aan de huidige groene groep, selecteer een andere startindex
        if (newGreenGroup.SequenceEqual(currentGreenDirections))
        {
            lastSwitchIndex = (lastSwitchIndex + 1) % count;
            Console.WriteLine("Zelfde groene groep, probeer volgende index.");
            return;
        }

        // Zet de huidige groene groep (indien aanwezig) op rood
        foreach (var dir in currentGreenDirections)
        {
            dir.Color = LightColor.Red;
            Console.WriteLine($"Direction {dir.Id} staat nu op rood.");
        }
        currentGreenDirections.Clear();

        // Stel de nieuwe groep in op groen
        currentGreenDirections = newGreenGroup;
        foreach (var dir in currentGreenDirections)
        {
            dir.Color = LightColor.Green;
            Console.WriteLine($"Direction {dir.Id} staat nu op groen met prioriteit {GetPriority(dir)}.");
        }

        lastSwitchTime = DateTime.Now;
        lastSwitchIndex = (lastSwitchIndex + newGreenGroup.Count) % count;
    }

    private static void SetLightsToOrange()
    {
        // Zet huidige groene lichten op oranje
        foreach (var dir in currentGreenDirections)
        {
            dir.Color = LightColor.Orange;
            Console.WriteLine($"Direction {dir.Id} staat nu op oranje.");
        }
        currentOrangeDirections = new List<Direction>(currentGreenDirections);
        currentGreenDirections.Clear();
    }

    private static void SetLightsToRed()
    {
        // Zet de oranje groep op rood
        foreach (var dir in currentOrangeDirections)
        {
            dir.Color = LightColor.Red;
            Console.WriteLine($"Direction {dir.Id} staat nu op rood.");
        }
        currentOrangeDirections.Clear();
    }

    // Bepaalt de prioriteit van een richting: hogere waarde als beide sensoren (file) actief zijn
    private static int GetPriority(Direction direction)
    {
        int priority = 0;
        foreach (var trafficLight in direction.TrafficLights)
        {
            int activatedSensors = trafficLight.Sensors.Count(s => s.IsActivated);
            if (activatedSensors == 2)
            {
                priority += 2;
            }
            else if (activatedSensors == 1)
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

        Dictionary<string, Dictionary<string, bool>> sensorData = null;
        try
        {
            sensorData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
        }
        catch (JsonReaderException ex)
        {
            Console.WriteLine($"Fout bij het deserialiseren van sensor data: {ex.Message}");
            return;
        }

        if (sensorData == null)
        {
            Console.WriteLine("Gereserveerde sensor data is null.");
            return;
        }

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

    // Stelt JSON samen met de huidige kleuren van alle verkeerslichten en stuurt het terug via de Communicator
    static void SendTrafficLightStates()
    {
        if (string.IsNullOrEmpty(communicator.LaneSensorData))
            return;

        var sensorData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
        if (sensorData == null)
            return;

        var stateDict = new Dictionary<string, string>();
        foreach (var (trafficLightId, _) in sensorData)
        {
            var trafficLight = Directions
                .SelectMany(d => d.TrafficLights)
                .FirstOrDefault(tl => tl.Id == trafficLightId);
            if (trafficLight == null)
                continue;

            var direction = Directions.FirstOrDefault(d => d.TrafficLights.Contains(trafficLight));
            if (direction == null)
                continue;

            string state = direction.Color == LightColor.Green ? "groen" :
                           direction.Color == LightColor.Orange ? "oranje" : "rood";
            stateDict[trafficLight.Id] = state;
        }
        communicator.PublishMessage("stoplichten", stateDict);
    }
}
