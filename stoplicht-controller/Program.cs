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

    static string subscriberAddress = "tcp://10.121.17.233:5556";
    static string publisherAddress = "tcp://*:5557";
    static string[] topics = { "sensoren_rijbaan", "tijd", "voorrangsvoertuig" };
    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);

    // Duur in milliseconden:
    private const int ORANGE_DURATION = 2000;         // 2 seconden oranje
    private const int DEFAULT_GREEN_DURATION = 8000;    // 8 seconden groen (standaard)
    private const int SHORT_GREEN_DURATION = 3000;      // 3 seconden groen bij weinig verkeer
    private const int PRIORITY_THRESHOLD = 3;           // Drempel voor standaard groen
    private const int HIGH_PRIORITY_THRESHOLD = 6;      // Drempel voor file (verlengde groen)
    private const double AGING_SCALE_SECONDS = 7;      // 1 extra prioriteitspunt per 10 seconden wachten

    private static DateTime lastSwitchTime = DateTime.Now;
    private static DateTime lastOrangeTime = DateTime.Now;
    private static List<Direction> currentGreenDirections = new List<Direction>();
    private static List<Direction> currentOrangeDirections = new List<Direction>();

    // Houdt per richting (ID) bij wanneer deze voor het laatst groen was.
    private static Dictionary<int, DateTime> lastGreenTimes = new Dictionary<int, DateTime>();

    static void Main()
    {
        LoadIntersectionData();
        // Initialiseer aging voor alle richtingen:
        foreach(var direction in Directions)
        {
            lastGreenTimes[direction.Id] = DateTime.Now;
        }
        communicator.StartSubscriber();

        if (Directions.Any())
        {
            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        while (true)
        {
            Update();
            // Console.WriteLine(communicator.LaneSensorData);
            Thread.Sleep(500);
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
        double timeSinceGreen = (now - lastSwitchTime).TotalMilliseconds;
        double timeSinceOrange = (now - lastOrangeTime).TotalMilliseconds;

        if (currentOrangeDirections.Any())
        {
            if (timeSinceOrange >= ORANGE_DURATION)
            {
                SetLightsToRed();
                SwitchTrafficLights();
                SendTrafficLightStates();
            }
            return;
        }

        if (currentGreenDirections.Any())
        {
            int sumEffectivePriority = currentGreenDirections.Sum(d => GetEffectivePriority(d));
            int dynamicGreenDuration = DEFAULT_GREEN_DURATION;
            if (sumEffectivePriority >= HIGH_PRIORITY_THRESHOLD)
                dynamicGreenDuration += 2000;
            else if (sumEffectivePriority < PRIORITY_THRESHOLD)
                dynamicGreenDuration = SHORT_GREEN_DURATION;

            if (timeSinceGreen >= dynamicGreenDuration)
            {
                SetLightsToOrange();
                lastOrangeTime = DateTime.Now;
                SendTrafficLightStates();
            }
            else
            {
                // Probeer extra, niet-conflicterende richtingen toe te voegen aan de huidige groene groep
                var extraCandidates = GetExtraGreenCandidates();
                if (extraCandidates.Any())
                {
                    foreach (var extra in extraCandidates)
                    {
                        currentGreenDirections.Add(extra);
                        extra.Color = LightColor.Green;
                        // Update aging van de toegevoegde richting
                        lastGreenTimes[extra.Id] = DateTime.Now;
                        Console.WriteLine($"Extra direction {extra.Id} toegevoegd aan groen met effectieve prioriteit {GetEffectivePriority(extra)}.");
                    }
                    lastSwitchTime = DateTime.Now;
                    SendTrafficLightStates();
                }
            }
            return;
        }

        if (!currentGreenDirections.Any())
        {
            SwitchTrafficLights();
            SendTrafficLightStates();
        }
    }

    private static List<Direction> GetExtraGreenCandidates()
    {
        // Verkrijg alle beschikbare richtingen met prioriteit > 0, op basis van effectieve prioriteit
        var candidates = Directions
            .Where(d => GetPriority(d) > 0 && !currentGreenDirections.Contains(d))
            .OrderByDescending(d => GetEffectivePriority(d))
            .ThenBy(d => d.Id)
            .ToList();

        var extraCandidates = new List<Direction>();
        foreach (var candidate in candidates)
        {
            bool conflict = currentGreenDirections.Concat(extraCandidates)
                .Any(green => HasConflict(green, candidate));
            if (!conflict)
                extraCandidates.Add(candidate);
        }
        return extraCandidates;
    }

    private static void SwitchTrafficLights()
    {
        var availableDirections = Directions
            .Where(d => GetPriority(d) > 0)
            .OrderByDescending(d => GetEffectivePriority(d))
            .ThenBy(d => d.Id)
            .ToList();

        if (!availableDirections.Any())
        {
            Console.WriteLine("Geen beschikbare richtingen gevonden om naar groen te schakelen.");
            return;
        }

        var newGreenGroup = new List<Direction>();
        foreach (var candidate in availableDirections)
        {
            if (!newGreenGroup.Any(green => HasConflict(green, candidate)))
                newGreenGroup.Add(candidate);
        }

        if (!newGreenGroup.Any())
        {
            Console.WriteLine("Geen niet-conflicterende richtingen gevonden.");
            return;
        }

        if (newGreenGroup.SequenceEqual(currentGreenDirections))
        {
            Console.WriteLine("Zelfde groene groep, geen wijziging.");
            return;
        }

        foreach (var dir in currentGreenDirections)
        {
            dir.Color = LightColor.Red;
            Console.WriteLine($"Direction {dir.Id} staat nu op rood.");
        }
        currentGreenDirections.Clear();

        currentGreenDirections = newGreenGroup;
        foreach (var dir in currentGreenDirections)
        {
            dir.Color = LightColor.Green;
            lastGreenTimes[dir.Id] = DateTime.Now;
            Console.WriteLine($"Direction {dir.Id} staat nu op groen met effectieve prioriteit {GetEffectivePriority(dir)}.");
        }
        lastSwitchTime = DateTime.Now;
    }

    private static bool HasConflict(Direction d1, Direction d2)
    {
        if (!d1.Intersections.Any() || !d2.Intersections.Any())
            return false;
        return d1.Intersections.Contains(d2.Id) || d2.Intersections.Contains(d1.Id);
    }

    private static void SetLightsToOrange()
    {
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
        foreach (var dir in currentOrangeDirections)
        {
            dir.Color = LightColor.Red;
            Console.WriteLine($"Direction {dir.Id} staat nu op rood.");
        }
        currentOrangeDirections.Clear();
    }

    private static int GetPriority(Direction direction)
    {
        // Basisprioriteit op basis van sensorgegevens (file = 5, enkelvoud = 1)
        int priority = 0;
        foreach (var trafficLight in direction.TrafficLights)
        {
            bool front = trafficLight.Sensors.Any(s => s.Position == SensorPosition.Front && s.IsActivated);
            bool back = trafficLight.Sensors.Any(s => s.Position == SensorPosition.Back && s.IsActivated);
            if (front && back)
                priority += 5;
            else if (front || back)
                priority += 1;
        }
        return priority;
    }

    // Effectieve prioriteit = basisprioriteit plus aging bonus
    private static int GetEffectivePriority(Direction direction)
    {
        int basePriority = GetPriority(direction);
        DateTime lastGreen;
        if (!lastGreenTimes.TryGetValue(direction.Id, out lastGreen))
        {
            lastGreen = DateTime.Now;
            lastGreenTimes[direction.Id] = lastGreen;
        }
        double waitingTimeSec = (DateTime.Now - lastGreen).TotalSeconds;
        int agingBonus = (int)(waitingTimeSec / AGING_SCALE_SECONDS);
        return basePriority + agingBonus;
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
            var trafficLight = Directions.SelectMany(d => d.TrafficLights)
                                         .FirstOrDefault(tl => tl.Id == trafficLightId);
            if (trafficLight == null)
                continue;
            foreach (var sensor in trafficLight.Sensors)
            {
                if (sensor.Position == SensorPosition.Front && sensors.TryGetValue("voor", out bool frontValue))
                    sensor.IsActivated = frontValue;
                else if (sensor.Position == SensorPosition.Back && sensors.TryGetValue("achter", out bool backValue))
                    sensor.IsActivated = backValue;
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
            var trafficLight = Directions.SelectMany(d => d.TrafficLights)
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
