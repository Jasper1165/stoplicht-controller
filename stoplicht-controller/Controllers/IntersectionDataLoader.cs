using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

namespace stoplicht_controller.Managers
{
    /// <summary>
    /// Loads intersection and lane data from a JSON resource file into Direction objects.
    /// </summary>
    public static class IntersectionDataLoader
    {
        /// <summary>
        /// Reads the lanes.json file from the Resources folder and populates the provided directions list.
        /// Each JSON group becomes a Direction, with its intersections and TrafficLight sensors initialized.
        /// </summary>
        /// <param name="directions">List of Direction instances to populate.</param>
        public static void LoadIntersectionData(List<Direction> directions)
        {
            // Construct the path to the JSON file in the application Resources directory
            string jsonFilePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Resources",
                "intersectionData",
                "lanes.json");

            // If the JSON file is missing, log an error and abort loading
            if (!File.Exists(jsonFilePath))
            {
                Console.WriteLine($"File not found: {jsonFilePath}");
                return;
            }

            // Read the entire JSON content
            string jsonContent = File.ReadAllText(jsonFilePath);
            // Parse the JSON into a JObject for easier traversal
            JObject jsonObject = JObject.Parse(jsonContent);

            // Extract the "groups" section, mapping group IDs to their JSON definitions
            var groupsData = jsonObject["groups"]?.ToObject<Dictionary<string, JObject>>();
            if (groupsData == null)
                return;

            // Iterate over each group entry
            foreach (var (groupIdStr, groupObj) in groupsData)
            {
                // Convert the group ID key to an integer
                if (!int.TryParse(groupIdStr, out int groupId))
                    continue;

                // Create a new Direction with the parsed ID
                var direction = new Direction { Id = groupId };

                // Read the list of intersecting directions, if present
                if (groupObj["intersects_with"] is JArray intersectsArray)
                    direction.Intersections = intersectsArray
                        .Select(i => i.ToObject<int>())
                        .ToList();

                // Process each lane within this group
                if (groupObj["lanes"] is JObject lanesObj)
                {
                    foreach (var laneProperty in lanesObj.Properties())
                    {
                        // Convert the lane key to an integer lane ID
                        if (!int.TryParse(laneProperty.Name, out int laneId))
                            continue;

                        // Create a TrafficLight identifier using "groupId.laneId"
                        var trafficLight = new TrafficLight
                        {
                            Id = $"{groupId}.{laneId}"
                        };

                        // Initialize front and back sensors for the lane
                        trafficLight.Sensors.AddRange(new List<Sensor>
                        {
                            new Sensor { Position = SensorPosition.Front, IsActivated = false },
                            new Sensor { Position = SensorPosition.Back,  IsActivated = false }
                        });

                        // Add the configured TrafficLight to the direction
                        direction.TrafficLights.Add(trafficLight);
                    }
                }

                // Add the fully populated Direction to the shared list
                directions.Add(direction);
            }

            Console.WriteLine("Intersection data loaded.");
        }
    }
}
