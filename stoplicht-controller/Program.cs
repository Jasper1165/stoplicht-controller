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

    static string subscriberAddress = "tcp://192.168.1.150:5556";
    static string publisherAddress = "tcp://192.168.1.150:5556";
    static string[] topics = { "sensoren_rijbaan", "tijd", "priorityVehicle" };
    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);

    static void Main()
    {
        LoadIntersectionData();
        communicator.StartSubscriber();
        communicator.StartPublisher("sensoren_rijbaan");

        while (true)
        {
            Update();
            Thread.Sleep(1000);
        }
    }

    static void Update()
    {
        ProcessSensorMessage();

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

            // Update sensoren
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
