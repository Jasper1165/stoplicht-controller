using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

namespace stoplicht_controller.Managers
{
    public class PriorityVehicleManager
    {
        private Communicator communicator;
        private List<Direction> directions;
        private List<Direction> priorityVehicleQueue;

        public PriorityVehicleManager(Communicator communicator, List<Direction> directions, List<Direction> priorityVehicleQueue)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.priorityVehicleQueue = priorityVehicleQueue;
        }

        public void PriorityVehicleHandlerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                ProcessPriorityVehicleMessage();
                HandlePriorityVehicles();
                Thread.Sleep(100);
            }
        }

        private void ProcessPriorityVehicleMessage()
        {
            if (string.IsNullOrEmpty(communicator.PriorityVehicleData))
                return;

            Dictionary<string, List<Dictionary<string, object>>> priorityVehicleData = null;
            try
            {
                string jsonData = communicator.PriorityVehicleData;
                priorityVehicleData = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(jsonData);
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine($"Fout bij het deserialiseren van voorrangsvoertuig data: {ex.Message}");
                return;
            }
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
                if (!priorityVehicleQueue.Any(d => d.Id == directionId))
                    priorityVehicleQueue.Add(new Direction { Id = directionId, Priority = priority });
            }
        }

        private void HandlePriorityVehicles()
        {
            if (!priorityVehicleQueue.Any())
                return;

            var nextVehicle = priorityVehicleQueue.OrderBy(x => x.Priority).ThenBy(x => x.Id).First();
            int prio = nextVehicle.Priority ?? 0;
            int directionId = nextVehicle.Id;
            var prioDirection = directions.FirstOrDefault(d => d.Id == directionId);
            if (prioDirection == null)
            {
                priorityVehicleQueue.Remove(nextVehicle);
                return;
            }

            // We signalen dat we de normale cyclus tijdelijk pauzeren
            // Voor eenvoud gebruiken we hier een directe aanpak (kan ook met events)
            if (prio == 1)
            {
                Console.WriteLine($"Handling priority 1 vehicle in direction {directionId}");
                foreach (var direction in directions.Where(d => d.Id != prioDirection.Id && HasConflict(prioDirection, d)))
                {
                    direction.Color = LightColor.Red;
                    Console.WriteLine($"Richting {direction.Id} (conflict met {directionId}) gezet op rood.");
                }
                prioDirection.Color = LightColor.Green;
                // Bij prioriteit 1 werken we met een exclusieve afhandeling:
                Thread.Sleep(3000);
                prioDirection.Color = LightColor.Red;
                Console.WriteLine($"Priority 1 voertuig in richting {directionId} afgehandeld.");
                priorityVehicleQueue.Remove(nextVehicle);
            }
            else if (prio == 2)
            {
                Console.WriteLine($"Handling priority 2 vehicle in direction {directionId}");
                // Als richting nog niet groen is en er geen conflict is, voeg toe aan de groep.
                if (!prioDirection.Color.Equals(LightColor.Green) && !directions.Any(green => green.Color.Equals(LightColor.Green) && HasConflict(green, prioDirection)))
                {
                    prioDirection.Color = LightColor.Green;
                    Console.WriteLine($"Priority 2: Richting {directionId} wordt groen.");
                }
                else
                {
                    Console.WriteLine($"Priority 2: Richting {directionId} conflicteert, blijft in de wachtrij.");
                    return;
                }
                Thread.Sleep(3000);
                prioDirection.Color = LightColor.Red;
                priorityVehicleQueue.Remove(nextVehicle);
            }
            communicator.PriorityVehicleData = null;
        }

        private bool HasConflict(Direction d1, Direction d2)
        {
            if (!d1.Intersections.Any() || !d2.Intersections.Any())
                return false;
            return d1.Intersections.Contains(d2.Id) || d2.Intersections.Contains(d1.Id);
        }
    }
}
