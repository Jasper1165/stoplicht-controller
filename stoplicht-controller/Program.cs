using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using stoplicht_controller.Classes;

class Program
{
    static public List<Direction> Directions { get; set; } = new List<Direction>();
    static public Bridge Bridge { get; set; } = new Bridge();
    static public List<Direction> priorityVehicleQueue = new List<Direction>();
    static string subscriberAddress = "tcp://192.168.50.137:5555";
    static string publisherAddress = "tcp://10.6.0.4:5556";
    static string[] topics = new string[] { "sensoren_rijbaan", "specialSensor", "priorityVehicle" };
    static Communicator communicator = new Communicator(subscriberAddress, publisherAddress, topics);
    static void Main()
    {
        loadIntersectionData();
        communicator.StartSubscriber();
        communicator.StartPublisher(topic: "auto");

        while(true) {
            update();
        }
    }
    // Update method
    static void update()
    {
        Console.WriteLine(communicator.LaneSensorData);
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

            var groupsData = jsonObject["groups"].ToObject<Dictionary<string, JObject>>();

            foreach (var groupKvp in groupsData)
            {
                // Maak een nieuwe Direction instance voor elke groep
                int groupId = int.Parse(groupKvp.Key);
                JObject groupObj = groupKvp.Value;

                Direction direction = new Direction();
                direction.Id = groupId;

                // Lees de intersections uit de groep (dit bepaalt met welke andere groepen deze Direction een intersectie heeft)
                JArray intersectsArray = (JArray)groupObj["intersects_with"];
                foreach (var intersect in intersectsArray)
                {
                    direction.Intersections.Add(intersect.ToObject<int>());
                }

                // Maak voor elke lane binnen de groep een TrafficLight aan
                JObject lanesObj = (JObject)groupObj["lanes"];
                foreach (var laneProperty in lanesObj.Properties())
                {
                    // Bepaal de laneId op basis van de key in de lanes dictionary
                    int laneId = int.Parse(laneProperty.Name);
                    // Stel een voorbeeld traffic light id samen, bv. group * 10 + lane
                    string trafficLightId = $"{groupId}.{laneId}";

                    TrafficLight tl = new TrafficLight
                    {
                        Id = trafficLightId
                    };

                    // Hier kun je eventueel extra logica toevoegen om Sensor instanties toe te voegen.
                    // Bijvoorbeeld: als er in de groep sensorinformatie staat (zoals transition_blockers),
                    // dan kun je op basis daarvan Sensor objecten aanmaken en toevoegen aan tl.Sensors.

                    direction.TrafficLights.Add(tl);
                }

                // Voeg de nieuw aangemaakte Direction toe aan de controller
                Directions.Add(direction);
            }

            Console.WriteLine("Intersection data geladen..");
            // foreach (var direction in Directions)
            // {
            //     foreach(var intersection in direction.TrafficLights)
            //     {
            //         Console.WriteLine($"TrafficLight {intersection.Id} in Direction {direction.Id}");
            //     }
            // }
        }
        else
        {
            Console.WriteLine($"Het bestand is niet gevonden: {jsonFilePath}");
        }
    }
}
