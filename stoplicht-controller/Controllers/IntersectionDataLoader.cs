using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

namespace stoplicht_controller.Managers
{
    public static class IntersectionDataLoader
    {
        public static void LoadIntersectionData(List<Direction> directions)
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
                directions.Add(direction);
            }

            // Initializeer de brug-data (mogelijk aan te vullen als nodig)
            Program.Bridge.VehicleOnBridge = false;
            Program.Bridge.VesselUnderBridge = false;
            Program.Bridge.TrafficJamNearBridge = false;

            Console.WriteLine("Intersection data geladen..");
        }
    }
}
