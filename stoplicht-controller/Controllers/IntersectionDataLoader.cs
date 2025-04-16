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

                // Hier wordt er een Direction (of Group) aangemaakt op basis van het JSON object.
                var direction = new Direction { Id = groupId };

                if (groupObj["intersects_with"] is JArray intersectsArray)
                    direction.Intersections = intersectsArray.Select(i => i.ToObject<int>()).ToList();

                // Inlezen van lanes
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

                // EXTRA: Inlezen van transition_requirements indien aanwezig
                // if (groupObj["transition_requirements"] != null)
                // {
                //     // Verwacht een structuur met keys zoals "green" of "red" en een array met condities
                //     var requirementsObj = groupObj["transition_requirements"] as JObject;
                //     if (requirementsObj != null)
                //     {
                //         // Zorg ervoor dat de property in je model bestaat (bijv. in een Group-klasse)
                //         // Hier casten we de transition_requirements naar een Dictionary<string, List<TransitionCondition>>
                //         try
                //         {
                //             var requirements = requirementsObj.ToObject<Dictionary<string, List<TransitionCondition>>>();
                //             // Stel de requirements in. Aangezien je nu wellicht met Direction werkt, kun je
                //             // de property eventueel toevoegen aan Direction of aan een aparte Group instantie.
                //             // Hieronder als voorbeeld:
                //             direction.TransitionRequirements = requirements;
                //         }
                //         catch (Exception ex)
                //         {
                //             Console.WriteLine($"Fout bij het inlezen van transition_requirements: {ex.Message}");
                //         }
                //     }
                // }

                // EXTRA: Inlezen van transition_blockers indien aanwezig
                // if (groupObj["transition_blockers"] != null)
                // {
                //     var blockersObj = groupObj["transition_blockers"] as JObject;
                //     if (blockersObj != null)
                //     {
                //         try
                //         {
                //             var blockers = blockersObj.ToObject<Dictionary<string, List<TransitionCondition>>>();
                //             direction.TransitionBlockers = blockers;
                //         }
                //         catch (Exception ex)
                //         {
                //             Console.WriteLine($"Fout bij het inlezen van transition_blockers: {ex.Message}");
                //         }
                //     }
                // }

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
