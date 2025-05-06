// PriorityVehicleManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

namespace stoplicht_controller.Managers
{
    public class PriorityVehicleManager
    {
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly List<Direction> normalQueue;

        // Huidige actieve Prio-1 richtingen
        private HashSet<int> activePrio1 = new HashSet<int>();

        // Richting-IDs die momenteel in de prio2-wachtrij staan
        private HashSet<int> queuedPrio2 = new HashSet<int>();

        // Bridge direction IDs die uitgesloten moeten worden van de noodprocedure
        private readonly int[] bridgeDirections = new int[] { 71, 72 };

        // Set om alle uitgesloten richtingen te bewaren (bruglichten + hun intersections)
        private HashSet<int> excludedDirections = new HashSet<int>();

        // Public getter om te weten of er nú een Prio-1 actief is
        public bool HasActivePrio1 => activePrio1.Any();

        public PriorityVehicleManager(
            Communicator communicator,
            List<Direction> directions,
            List<Direction> normalQueue)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.normalQueue = normalQueue;

            InitializeExcludedDirections();
        }

        private void InitializeExcludedDirections()
        {
            foreach (var bridgeId in bridgeDirections)
            {
                excludedDirections.Add(bridgeId);
                var bridgeDirection = directions.FirstOrDefault(d => d.Id == bridgeId);
                if (bridgeDirection?.Intersections != null)
                    foreach (var intersectionId in bridgeDirection.Intersections)
                        excludedDirections.Add(intersectionId);
            }
            Console.WriteLine($"[BRIDGE] Excluded directions: {string.Join(", ", excludedDirections)}");
        }

        public void PriorityVehicleHandlerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var json = communicator.PriorityVehicleData;
                if (!string.IsNullOrEmpty(json))
                    HandleJson(json);

                Thread.Sleep(50);
            }
        }

        private void HandleJson(string json)
        {
            try
            {
                var wrapper = JsonConvert
                    .DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(json)
                    ?? new Dictionary<string, List<Dictionary<string, object>>>();

                // 1) bepaal welke Prio-1 / Prio-2 in de JSON staan
                var prio1Ids = new HashSet<int>();
                var prio2Ids = new HashSet<int>();
                var queue = wrapper.GetValueOrDefault("queue", new List<Dictionary<string, object>>());

                foreach (var item in queue)
                {
                    if (!item.TryGetValue("baan", out var baanObj) ||
                        !item.TryGetValue("prioriteit", out var prioObj))
                        continue;

                    var baan = baanObj.ToString();
                    if (!baan.Contains("."))
                        continue;
                    if (!int.TryParse(baan.Split('.')[0], out int dirId))
                        continue;
                    if (!int.TryParse(prioObj.ToString(), out int prio))
                        continue;
                    if (!directions.Any(d => d.Id == dirId))
                        continue;

                    if (prio == 1) prio1Ids.Add(dirId);
                    else if (prio == 2) prio2Ids.Add(dirId);
                }

                // 2) vrijgeven van verdwenen Prio-1
                var removedPrio1 = activePrio1.Except(prio1Ids).ToList();
                foreach (var id in removedPrio1)
                {
                    var dir = directions.FirstOrDefault(d => d.Id == id);
                    if (dir != null && dir.Color != LightColor.Red)
                    {
                        dir.Color = LightColor.Red;
                        Console.WriteLine($"[EMERGENCY] Released Prio-1 direction {id}, set to RED");
                    }
                    activePrio1.Remove(id);
                }

                // 3) vrijgeven van verdwenen Prio-2
                var removedPrio2 = queuedPrio2.Except(prio2Ids).ToList();
                foreach (var id in removedPrio2)
                {
                    queuedPrio2.Remove(id);
                    // verwijder tijdelijke prio2-entry uit normalQueue
                    normalQueue.RemoveAll(d => d.Id == id && d.Priority == 2);
                    // voeg de originele richting weer achteraan
                    var original = directions.FirstOrDefault(d => d.Id == id);
                    if (original != null)
                    {
                        normalQueue.Add(original);
                        Console.WriteLine($"[Prio 2] Released direction {id}, returned to normal queue");
                    }
                }

                // Publiceer gewijzigde states na vrijgave
                PublishLightStates();

                // 4) JSON “acknowledgen” zodat we niet herhalen
                communicator.PriorityVehicleData = null;

                // 5) verwerk nieuwe prio’s
                if (prio1Ids.Any())
                    HandleEmergencyPrio1(prio1Ids);
                else if (!activePrio1.Any())
                    HandlePrio2Vehicles(prio2Ids);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.Message}");
                communicator.PriorityVehicleData = null;
            }
        }

        private void HandlePrio2Vehicles(HashSet<int> newPrio2Ids)
        {
            foreach (var id in newPrio2Ids.Except(queuedPrio2))
            {
                var dir = directions.FirstOrDefault(d => d.Id == id);
                if (dir == null)
                    continue;

                normalQueue.RemoveAll(d => d.Id == id);
                normalQueue.Insert(0, new Direction
                {
                    Id = id,
                    Priority = 2,
                    Color = dir.Color,
                    Intersections = dir.Intersections
                });
                queuedPrio2.Add(id);
                Console.WriteLine($"[Prio 2] Moved to front: {id}");
            }

            // Publiceer direct na toevoegen Prio-2
            PublishLightStates();
        }

        private void HandleEmergencyPrio1(HashSet<int> currentPrio1Ids)
        {
            Console.WriteLine($"[EMERGENCY] Processing Prio-1: {string.Join(", ", currentPrio1Ids)}");

            // STAP 1: ALLE niet-prio1 en niet-uitgesloten op ROOD
            foreach (var direction in directions)
            {
                if (currentPrio1Ids.Contains(direction.Id))
                    continue;
                if (excludedDirections.Contains(direction.Id))
                    continue;

                if (direction.Color != LightColor.Red)
                {
                    direction.Color = LightColor.Red;
                    Console.WriteLine($"[EMERGENCY] RED: {direction.Id}");
                }
            }

            // STAP 2: veiligheidsperiode
            Thread.Sleep(100);

            // STAP 3: Prio-1 op GROEN
            foreach (var id in currentPrio1Ids)
            {
                if (excludedDirections.Contains(id))
                    continue;
                var dir = directions.FirstOrDefault(d => d.Id == id);
                if (dir != null && dir.Color != LightColor.Green)
                {
                    dir.Color = LightColor.Green;
                    Console.WriteLine($"[EMERGENCY] GREEN: {id}");
                }
            }

            // update actieve Prio-1
            activePrio1 = new HashSet<int>(currentPrio1Ids);

            // Publiceer direct na Prio-1 noodafhandeling
            PublishLightStates();
        }

        /// <summary>
        /// Bouwt de map tl-id → kleur-string en publiceert naar "stoplichten".
        /// </summary>
        private void PublishLightStates()
        {
            var output = directions
                .SelectMany(d => d.TrafficLights)
                .ToDictionary(
                    tl => tl.Id,
                    tl =>
                    {
                        var dir = directions.First(d => d.TrafficLights.Contains(tl));
                        return dir.Color switch
                        {
                            LightColor.Green => "groen",
                            LightColor.Orange => "oranje",
                            _ => "rood"
                        };
                    });

            communicator.PublishMessage("stoplichten", output);
        }
    }
}
