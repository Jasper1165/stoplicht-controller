using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using stoplicht_controller.Classes;

class Program
{
    static List<Direction> directions = new List<Direction>();
    static Dictionary<string, Sensor> sensors = new Dictionary<string, Sensor>(); // Dictionary om sensoren op te slaan
    static Dictionary<int, Group> groups = new Dictionary<int, Group>(); // Dictionary om groepen op te slaan
    static string host = "tcp://*:5555";
    static string[] topics = new string[] { "laneSensor", "specialSensor", "priorityVehicle" };
    static Communicator communicator = new Communicator(host, topics);
    static void Main()
    {
        loadIntersectionData();
        communicator.Start();



    }
    // Update method
    static void update()
    {

    }
    static void loadIntersectionData()
    {
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        string jsonFilePath = Path.Combine(basePath, "Resources", "intersectionData", "lanes.json");

        // check if file exists
        if (File.Exists(jsonFilePath))
        {
            string jsonContent = File.ReadAllText(jsonFilePath);
            JObject jsonObject = JObject.Parse(jsonContent);

            // Load sensors
            var sensorData = jsonObject["sensors"].ToObject<Dictionary<string, Sensor>>();
            foreach (var sensorKvp in sensorData)
            {
                string sensorName = sensorKvp.Key;
                Sensor sensor = sensorKvp.Value;
                sensors[sensorName] = sensor;
            }

            // deserialize groups (json)
            var groupsData = jsonObject["groups"].ToObject<Dictionary<string, Group>>();

            foreach (var groupKvp in groupsData)
            {
                int groupId = int.Parse(groupKvp.Key);
                Group group = groupKvp.Value;
                group.Id = groupId;
                groups[groupId] = group;

                // loop through trafficlights
                foreach (var laneKvp in group.Lanes)
                {
                    int laneId = int.Parse(laneKvp.Key);
                    TrafficLight trafficLight = laneKvp.Value ?? new TrafficLight();
                    trafficLight.Id = laneId;

                    // create instances of trafficlight
                    Direction direction = new Direction
                    {
                        Id = group.Id + "_" + trafficLight.Id,
                        IntersectsWith = group.IntersectsWith,
                        trafficLights = new List<TrafficLight> { new TrafficLight { Id = trafficLight.Id } }
                    };
                    directions.Add(direction);
                }
            }
        }
        else
        {
            Console.WriteLine($"Het bestand is niet gevonden: {jsonFilePath}");
        }
    }
}
