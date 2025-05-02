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
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly List<Direction> priorityVehicleQueue;

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

            // ACK: leegmaken zodat we dit bericht niet opnieuw inlezen
            communicator.PriorityVehicleData = null;

            if (priorityVehicleData == null || !priorityVehicleData.ContainsKey("queue"))
                return;

            foreach (var item in priorityVehicleData["queue"])
            {
                if (!item.TryGetValue("baan", out var baanObj) || !item.TryGetValue("prioriteit", out var priorityObj))
                    continue;

                string baan = baanObj.ToString();
                if (!int.TryParse(priorityObj.ToString(), out int priority))
                    continue;

                // Haal directionId uit "2.1" → 2
                if (!int.TryParse(baan.Split('.')[0], out int directionId))
                    continue;

                if (!priorityVehicleQueue.Any(d => d.Id == directionId))
                    priorityVehicleQueue.Add(new Direction { Id = directionId, Priority = priority });
            }
        }

        private void HandlePriorityVehicles()
        {
            if (!priorityVehicleQueue.Any())
                return;

            var nextVehicle = priorityVehicleQueue
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.Id)
                .First();

            int prio = nextVehicle.Priority ?? 0;
            int directionId = nextVehicle.Id;
            var prioDirection = directions.FirstOrDefault(d => d.Id == directionId);

            if (prioDirection == null)
            {
                priorityVehicleQueue.Remove(nextVehicle);
                return;
            }

            Console.WriteLine($"Handling priority {prio} vehicle in direction {directionId}");

            if (prio == 1)
            {
                // Ruime pauze en exclusieve groenfase
                foreach (var direction in directions.Where(d => d.Id != prioDirection.Id && HasConflict(prioDirection, d)))
                {
                    direction.Color = LightColor.Red;
                    Console.WriteLine($"Richting {direction.Id} (conflict met {directionId}) gezet op rood.");
                }
                prioDirection.Color = LightColor.Green;
                Thread.Sleep(3000);
                prioDirection.Color = LightColor.Red;
                Console.WriteLine($"Priority 1 voertuig in richting {directionId} afgehandeld.");
                priorityVehicleQueue.Remove(nextVehicle);
            }
            else if (prio == 2)
            {
                // Toevoegen aan bestaande groene groep indien mogelijk
                if (prioDirection.Color != LightColor.Green &&
                    !directions.Any(g => g.Color == LightColor.Green && HasConflict(g, prioDirection)))
                {
                    prioDirection.Color = LightColor.Green;
                    Console.WriteLine($"Priority 2: Richting {directionId} wordt groen.");
                    Thread.Sleep(3000);
                    prioDirection.Color = LightColor.Red;
                    priorityVehicleQueue.Remove(nextVehicle);
                }
                else
                {
                    Console.WriteLine($"Priority 2: Richting {directionId} conflicteert, blijft in de wachtrij.");
                }
            }

            // Clear communicator zodat we niet blijven hangen
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
