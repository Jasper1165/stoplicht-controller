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
    /// <summary>
    ///  Afhandelen van prio-1 (direct groen) en prio-2 (vooraan in normale wachtrij) voertuigen.
    ///  – Prio 1: “first-come = first-served”; blijft groen tot de entry uit de JSON verdwijnt
    ///  – Prio 2: éénmalig naar de kop van normalQueue, géén onmiddellijke kleurwijziging
    /// </summary>
    public class PriorityVehicleManager
    {
        // constructor-injectie
        private readonly Communicator communicator;
        private readonly List<Direction> directions;   // alle richtingen van de kruising
        private readonly List<Direction> normalQueue;  // wachtrij die de TrafficLightController afwerkt

        // ================  STATUS  ================
        private int? activePrio1 = null;               // huidig actieve prio-1 direction-id
        private readonly Queue<int> waitingPrio1 = new(); // FIFO wachtrij overige prio-1’s
        private readonly HashSet<int> queuedPrio2 = new(); // actieve prio-2’s in normalQueue

        // externe check voor TrafficLightController
        public bool HasActivePrio1 => activePrio1.HasValue;

        public PriorityVehicleManager(
            Communicator communicator,
            List<Direction> directions,
            List<Direction> normalQueue)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.normalQueue = normalQueue;
        }

        // hoofd-loop – wordt idealiter in een aparte Task gestart
        public void PriorityVehicleHandlerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var json = communicator.PriorityVehicleData;
                if (!string.IsNullOrEmpty(json))
                {
                    HandleJson(json);
                    communicator.PriorityVehicleData = null;   // markeer als “verwerkt”
                }
                Thread.Sleep(50);
            }
        }

        // -----------------  JSON  -----------------
        private void HandleJson(string json)
        {
            // Parse ‘queue’ exact in de volgorde waarin hij binnenkomt
            var wrapper = JsonConvert
                .DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(json)
                    ?? new();

            var queue = wrapper.GetValueOrDefault("queue", new());

            var prio1Seen = new HashSet<int>();   // alles wat NU nog in JSON staat
            var prio2Seen = new HashSet<int>();

            foreach (var item in queue)
            {
                if (!item.TryGetValue("baan", out var baanObj) ||
                    !item.TryGetValue("prioriteit", out var prioObj))
                    continue;

                var baan = baanObj.ToString();
                if (!baan.Contains('.')) continue;
                if (!int.TryParse(baan.Split('.')[0], out int dirId)) continue;
                if (!int.TryParse(prioObj.ToString(), out int prio)) continue;
                if (!directions.Any(d => d.Id == dirId)) continue;

                if (prio == 1)
                {
                    prio1Seen.Add(dirId);
                    RegisterPrio1(dirId);             // FIFO-behoud
                }
                else if (prio == 2)
                {
                    prio2Seen.Add(dirId);
                    RegisterPrio2(dirId);
                }
            }

            CleanupEndedPrio1(prio1Seen);
            CleanupEndedPrio2(prio2Seen);

            PublishLightStates();
        }

        // -----------------  PRIO-1  -----------------
        /// <summary>Voegt een prio-1 toe; als er nog niets actief is, wordt hij direct geactiveerd.</summary>
        private void RegisterPrio1(int dirId)
        {
            if (activePrio1 == dirId || waitingPrio1.Contains(dirId)) return;

            if (!activePrio1.HasValue)
            {
                ActivatePrio1(dirId);
            }
            else
            {
                waitingPrio1.Enqueue(dirId); // wacht rustig tot hij aan de beurt is
            }
        }

        private void ActivatePrio1(int dirId)
        {
            activePrio1 = dirId;

            // Zet alle conflicterende richtingen op Rood
            var prioDir = directions.First(d => d.Id == dirId);
            foreach (var interId in prioDir.Intersections)
            {
                var inter = directions.FirstOrDefault(d => d.Id == interId);
                if (inter != null) inter.Color = LightColor.Red;
            }

            // Zet prio-1 zelf op Groen
            prioDir.Color = LightColor.Green;
            Console.WriteLine($"[PRIO-1] Direction {dirId} → GREEN (intersections RED)");
        }

        /// <summary>Controleert of de actieve prio-1 is verdwenen en maakt ruimte voor de volgende.</summary>
        private void CleanupEndedPrio1(HashSet<int> stillPresentPrio1)
        {
            if (activePrio1.HasValue && !stillPresentPrio1.Contains(activePrio1.Value))
            {
                // einde noodprocedure voor deze richting
                var dir = directions.First(d => d.Id == activePrio1.Value);
                dir.Color = LightColor.Red;
                Console.WriteLine($"[PRIO-1] Direction {dir.Id} ended → RED");

                activePrio1 = null;
            }

            // start eventueel de volgende prio-1 uit de wachtrij
            if (!activePrio1.HasValue && waitingPrio1.TryDequeue(out int nextId))
                ActivatePrio1(nextId);
        }

        // -----------------  PRIO-2  -----------------
        private void RegisterPrio2(int dirId)
        {
            if (queuedPrio2.Contains(dirId)) return;

            // Verwijder eerst eventuele kopie uit normale queue
            normalQueue.RemoveAll(d => d.Id == dirId);

            // Plaats als nieuwe entry VOORIN de queue (Priority = 2)
            var original = directions.First(d => d.Id == dirId);
            normalQueue.Insert(0, new Direction
            {
                Id = dirId,
                Priority = 2,
                Color = original.Color,
                Intersections = original.Intersections
            });

            queuedPrio2.Add(dirId);
            Console.WriteLine($"[PRIO-2] Direction {dirId} moved to front of queue");
        }

        private void CleanupEndedPrio2(HashSet<int> stillPresentPrio2)
        {
            var ended = queuedPrio2.Except(stillPresentPrio2).ToList();
            foreach (var id in ended)
            {
                queuedPrio2.Remove(id);

                // vervang entry met een “normale” versie zodat de prioriteit terugvalt
                normalQueue.RemoveAll(d => d.Id == id && d.Priority == 2);
                var orig = directions.First(d => d.Id == id);
                normalQueue.Add(orig);

                Console.WriteLine($"[PRIO-2] Direction {id} ended → returned to normal queue");
            }
        }

        // -----------------  MQTT / UI  -----------------
        private void PublishLightStates()
        {
            var map = new Dictionary<string, string>();
            foreach (var dir in directions)
            {
                if (dir.TrafficLights == null) continue;
                foreach (var tl in dir.TrafficLights)
                {
                    if (tl == null) continue;
                    map[tl.Id] = dir.Color switch
                    {
                        LightColor.Green => "groen",
                        LightColor.Orange => "oranje",
                        _ => "rood"
                    };
                }
            }
            communicator.PublishMessage("stoplichten", map);
        }
    }
}
