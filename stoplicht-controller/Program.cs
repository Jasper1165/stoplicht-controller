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

    static string subscriberAddress = "tcp://10.121.17.233:5557";
    static string publisherAddress = "tcp://10.121.17.233:5556";
    static string[] topics = { "sensoren_rijbaan", "tijd", "voorrangsvoertuig" };
    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);

    // Duur in milliseconden:
    private const int ORANGE_DURATION = 2000;         // 2 seconden oranje
    private const int DEFAULT_GREEN_DURATION = 8000;    // 8 seconden groen (standaard)
    private const int SHORT_GREEN_DURATION = 3000;      // 3 seconden groen bij weinig verkeer
    private const int PRIORITY_THRESHOLD = 3;           // Drempel voor standaard groen
    private const int HIGH_PRIORITY_THRESHOLD = 6;      // Drempel voor file (verlengde groen)
    private const double AGING_SCALE_SECONDS = 7;       // 1 extra prioriteitspunt per 7 seconden wachten

    private static DateTime lastSwitchTime = DateTime.Now;
    private static DateTime lastOrangeTime = DateTime.Now;
    private static List<Direction> currentGreenDirections = new List<Direction>();
    private static List<Direction> currentOrangeDirections = new List<Direction>();

    // Houdt per richting (ID) bij wanneer deze voor het laatst groen was.
    private static Dictionary<int, DateTime> lastGreenTimes = new Dictionary<int, DateTime>();

    static void Main()
    {
        LoadIntersectionData();
        foreach (var direction in Directions)
            lastGreenTimes[direction.Id] = DateTime.Now;

        communicator.StartSubscriber();

        if (Directions.Any())
        {
            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        while (true)
        {
            Update();
            Thread.Sleep(500);
        }
    }

    static void Update()
    {
        ProcessSensorMessage();
        ProcessPriorityVehicleMessage();
        // Afhandeling voorrangsvoertuigen (prio meldingen) 1 voor 1
        HandlePriorityVehicles();
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
            int dynamicGreenDuration = sumEffectivePriority >= HIGH_PRIORITY_THRESHOLD ? DEFAULT_GREEN_DURATION + 2000 :
                                       sumEffectivePriority < PRIORITY_THRESHOLD ? SHORT_GREEN_DURATION : DEFAULT_GREEN_DURATION;

            if (timeSinceGreen >= dynamicGreenDuration)
            {
                SetLightsToOrange();
                lastOrangeTime = DateTime.Now;
                SendTrafficLightStates();
            }
            else
            {
                var extraCandidates = GetExtraGreenCandidates();
                if (extraCandidates.Any())
                {
                    foreach (var extra in extraCandidates)
                    {
                        currentGreenDirections.Add(extra);
                        extra.Color = LightColor.Green;
                        lastGreenTimes[extra.Id] = DateTime.Now;
                        Console.WriteLine($"Extra direction {extra.Id} toegevoegd aan groen met effectieve prioriteit {GetEffectivePriority(extra)}.");
                    }
                    lastSwitchTime = DateTime.Now;
                    SendTrafficLightStates();
                }
            }
            return;
        }

        // Als geen enkele richting groen is, wissel over naar een nieuwe groene groep.
        if (!currentGreenDirections.Any())
        {
            SwitchTrafficLights();
            SendTrafficLightStates();
        }
    }

    private static List<Direction> GetExtraGreenCandidates()
    {
        var candidates = Directions
            .Where(d => GetPriority(d) > 0 && !currentGreenDirections.Contains(d))
            .OrderByDescending(d => GetEffectivePriority(d))
            .ThenBy(d => d.Id)
            .ToList();

        var extraCandidates = new List<Direction>();
        foreach (var candidate in candidates)
        {
            if (!currentGreenDirections.Concat(extraCandidates).Any(green => HasConflict(green, candidate)))
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

        // Zet oude groene richtingen op rood.
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
        // Indien een van beide geen intersections heeft, is er geen conflict.
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
        int priority = 0;
        foreach (var trafficLight in direction.TrafficLights)
        {
            bool front = trafficLight.Sensors.Any(s => s.Position == SensorPosition.Front && s.IsActivated);
            bool back = trafficLight.Sensors.Any(s => s.Position == SensorPosition.Back && s.IsActivated);
            priority += front && back ? 5 : (front || back ? 1 : 0);
        }
        return priority;
    }

    // Effectieve prioriteit = basisprioriteit plus aging bonus
    private static int GetEffectivePriority(Direction direction)
    {
        int basePriority = GetPriority(direction);
        if (!lastGreenTimes.TryGetValue(direction.Id, out DateTime lastGreen))
        {
            lastGreen = DateTime.Now;
            lastGreenTimes[direction.Id] = lastGreen;
        }
        int agingBonus = (int)((DateTime.Now - lastGreen).TotalSeconds / AGING_SCALE_SECONDS);
        return basePriority + agingBonus;
    }

    static void ProcessPriorityVehicleMessage()
    {
        if (string.IsNullOrEmpty(communicator.PriorityVehicleData))
            return;
        var priorityVehicleData = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(communicator.PriorityVehicleData);
        if (priorityVehicleData == null || !priorityVehicleData.ContainsKey("queue"))
            return;
        foreach (var item in priorityVehicleData["queue"])
        {
            if (!item.TryGetValue("baan", out var baanObj) || !item.TryGetValue("prioriteit", out var priorityObj))
                continue;

            string baan = baanObj.ToString();
            if (!int.TryParse(priorityObj.ToString(), out int priority))
                continue;

            int directionId = int.Parse(baan.Split('.')[0]);
            if (!PriorityVehicleQueue.Any(d => d.Id == directionId))
                PriorityVehicleQueue.Add(new Direction { Id = directionId, Priority = priority });
        }
    }

    // Nieuwe methode voor het afhandelen van voorrangsvoertuigen
    // Deze methode verwerkt meldingen 1 voor 1 en houdt rekening met de prioriteit:
    // - Prio 1: Voor voorrangsvoertuigen. Deze overschrijven de normale cyclus: alle conflicterende richtingen worden op rood gezet, de betreffende richting krijgt groen,
    //          en na een korte periode (hier 3 seconden) gaat de reguliere cyclus verder.
    // - Prio 2: Voor openbaar vervoer. Indien mogelijk wordt de richting toegevoegd aan de huidige groene groep (mits er geen conflict is) en krijgt zo voorrang.
    private static void HandlePriorityVehicles()
    {
        if (!PriorityVehicleQueue.Any())
            return;

        // Sorteer op prioriteit (1 vóór 2) en vervolgens op ID.
        var nextVehicle = PriorityVehicleQueue.OrderBy(x => x.Priority).ThenBy(x => x.Id).First();
        int prio = nextVehicle.Priority ?? 0;
        int directionId = nextVehicle.Id;
        var prioDirection = Directions.FirstOrDefault(d => d.Id == directionId);
        if (prioDirection == null)
        {
            PriorityVehicleQueue.Remove(nextVehicle);
            return;
        }

        if (prio == 1)
        {
            Console.WriteLine($"Handling priority 1 vehicle in direction {directionId}");
            foreach (var direction in Directions.Where(d => d.Id != prioDirection.Id && HasConflict(prioDirection, d)))
            {
                direction.Color = LightColor.Red;
                Console.WriteLine($"Direction {direction.Id} (conflict met {directionId}) gezet op rood.");
            }
            prioDirection.Color = LightColor.Green;
            currentGreenDirections = new List<Direction> { prioDirection };
            currentOrangeDirections.Clear();
            SendTrafficLightStates();

            Thread.Sleep(3000); // Wachtperiode voor afhandeling

            prioDirection.Color = LightColor.Red;
            Console.WriteLine($"Priority 1 vehicle in direction {directionId} is afgehandeld, terug naar normale cyclus.");
            PriorityVehicleQueue.Remove(nextVehicle);
            SwitchTrafficLights();
            SendTrafficLightStates();
        }
        else if (prio == 2)
        {
            Console.WriteLine($"Handling priority 2 vehicle in direction {directionId}");
            if (!currentGreenDirections.Contains(prioDirection) && !currentGreenDirections.Any(green => HasConflict(green, prioDirection)))
            {
                currentGreenDirections.Add(prioDirection);
                prioDirection.Color = LightColor.Green;
                Console.WriteLine($"Priority 2: Direction {directionId} toegevoegd aan de huidige groene groep.");
                SendTrafficLightStates();
            }
            else
            {
                Console.WriteLine($"Priority 2: Direction {directionId} conflicteert met de huidige groene groep, melding blijft in wachtrij.");
                return;
            }
            Thread.Sleep(3000);
            PriorityVehicleQueue.Remove(nextVehicle);
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
                direction.Intersections = intersectsArray.Select(i => i.ToObject<int>()).ToList();

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

        var stateDict = sensorData.Keys
            .Select(tlId => Directions.SelectMany(d => d.TrafficLights)
                                        .FirstOrDefault(tl => tl.Id == tlId))
            .Where(tl => tl != null)
            .Select(tl => new
            {
                Id = tl.Id,
                State = Directions.FirstOrDefault(d => d.TrafficLights.Contains(tl))?.Color == LightColor.Green ? "groen" :
                        Directions.FirstOrDefault(d => d.TrafficLights.Contains(tl))?.Color == LightColor.Orange ? "oranje" : "rood"
            })
            .ToDictionary(x => x.Id, x => x.State);

        communicator.PublishMessage("stoplichten", stateDict);
    }
}
