// TrafficLightController.cs
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
    public class TrafficLightController
    {
        // ================================
        //       CONFIG CONSTANTS
        // ================================
        private const int ORANGE_DURATION = 4000;                    // how long orange shows (ms)
        private const int DEFAULT_GREEN_DURATION = 6000;            // default green phase duration (ms)
        private const int HIGH_PRIORITY_THRESHOLD = 6;               // when to add extra green time
        private const double AGING_SCALE_SECONDS = 7;                // seconds per “aging” point

        // ================================
        //       RUNTIME FIELDS
        // ================================
        private DateTime lastSwitchTime = DateTime.Now;
        private DateTime lastOrangeTime = DateTime.Now;

        private List<Direction> currentGreenDirections = new List<Direction>();
        private List<Direction> currentOrangeDirections = new List<Direction>();
        private static Dictionary<int, DateTime> lastGreenTimes = new Dictionary<int, DateTime>();

        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly BridgeController bridgeController;
        private readonly PriorityVehicleManager priorityManager;

        public TrafficLightController(
            Communicator communicator,
            List<Direction> directions,
            Bridge bridge,
            List<Direction> normalQueue,
            CancellationToken token)
        {
            this.communicator = communicator;
            this.directions = directions;
            InitializeLastGreenTimes(directions);

            bridgeController = new BridgeController(communicator, directions, bridge);
            priorityManager = new PriorityVehicleManager(communicator, directions, normalQueue);

            // Start de priority-handler in een aparte taak
            Task.Run(() => priorityManager.PriorityVehicleHandlerLoop(token), token);
        }

        /// <summary>
        /// Initializeert de laatste-groen tijden zodat aging vanaf nu telt.
        /// </summary>
        public static void InitializeLastGreenTimes(List<Direction> dirs)
        {
            foreach (var d in dirs)
                lastGreenTimes[d.Id] = DateTime.Now;
        }

        /// <summary>
        /// Main loop: initial switch, dan elke 500ms sensors en update.
        /// </summary>
        public async Task TrafficLightCycleLoop(CancellationToken token)
        {
            // Eerste keer meteen schakelen
            if (directions.Any())
            {
                SwitchTrafficLights();
                SendTrafficLightStates();
            }

            while (!token.IsCancellationRequested)
            {
                ProcessSensorMessage();
                bridgeController.ProcessBridgeSensorData();
                bridgeController.Update();
                UpdateTrafficLights();
                await Task.Delay(500, token);
            }
        }

        /// <summary>
        /// Schakelt alleen als er geen Prio-1 actief is.
        /// </summary>
        public void UpdateTrafficLights()
        {
            // ❌ Vroege exit bij actieve Prio-1
            if (priorityManager.HasActivePrio1)
                return;

            var now = DateTime.Now;
            var sinceGreen = (now - lastSwitchTime).TotalMilliseconds;
            var sinceOrange = (now - lastOrangeTime).TotalMilliseconds;

            // 1) Oranje-fase
            if (currentOrangeDirections.Any())
            {
                if (sinceOrange >= ORANGE_DURATION)
                {
                    SetLightsToRed();
                    SwitchTrafficLights();
                    SendTrafficLightStates();
                }
                return;
            }

            // 2) Groene fase
            if (currentGreenDirections.Any())
            {
                int sumPriority = currentGreenDirections.Sum(GetEffectivePriority);
                int greenDuration = DEFAULT_GREEN_DURATION;
                if (sumPriority >= HIGH_PRIORITY_THRESHOLD)
                    greenDuration += 2000;

                if (sinceGreen >= greenDuration)
                {
                    SetLightsToOrange();
                    lastOrangeTime = DateTime.Now;
                    SendTrafficLightStates();
                }
                else
                {
                    // Mid‐cycle extra toevoegen
                    var extra = GetExtraGreenCandidates();
                    if (extra.Any())
                    {
                        foreach (var e in extra)
                        {
                            e.Color = LightColor.Green;
                            currentGreenDirections.Add(e);
                            lastGreenTimes[e.Id] = DateTime.Now;
                        }
                        lastSwitchTime = DateTime.Now;
                        SendTrafficLightStates();
                    }
                }
                return;
            }

            // 3) Nieuwe groene fase
            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        /// <summary>
        /// Zoek richtingen die mid‐cycle kunnen bij, zonder conflict met groen of brug.
        /// </summary>
        private List<Direction> GetExtraGreenCandidates()
        {
            var blocked = bridgeController.GetBridgeIntersectionSet();
            return directions
                .Where(d => GetPriority(d) > 0
                            && !currentGreenDirections.Contains(d)
                            && !blocked.Contains(d.Id))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id)
                .Where(d => !currentGreenDirections.Any(g => HasConflict(g, d)))
                .ToList();
        }

        /// <summary>
        /// Kies een nieuwe set GREEN, excl. brug en conflicten.
        /// </summary>
        private void SwitchTrafficLights()
        {
            var blocked = bridgeController.GetBridgeIntersectionSet();
            var candidates = directions
                .Where(d => GetPriority(d) > 0 && !blocked.Contains(d.Id))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id)
                .ToList();

            if (!candidates.Any()) return;

            // kies conflictvrije combinaties
            var pick = new List<Direction>();
            foreach (var d in candidates)
            {
                if (!pick.Any(x => HasConflict(x, d)))
                    pick.Add(d);
            }

            // oude groen → rood
            foreach (var d in currentGreenDirections)
                d.Color = LightColor.Red;

            // nieuwe groen
            currentGreenDirections = pick;
            foreach (var d in pick)
            {
                d.Color = LightColor.Green;
                lastGreenTimes[d.Id] = DateTime.Now;
            }
            lastSwitchTime = DateTime.Now;
        }

        private void SetLightsToOrange()
        {
            foreach (var d in currentGreenDirections)
                d.Color = LightColor.Orange;
            currentOrangeDirections = currentGreenDirections.ToList();
            currentGreenDirections.Clear();
        }

        private void SetLightsToRed()
        {
            foreach (var d in currentOrangeDirections)
                d.Color = LightColor.Red;
            currentOrangeDirections.Clear();
        }

        /// <summary>
        /// True als twee richtingen elkaar kruisen.
        /// </summary>
        private bool HasConflict(Direction a, Direction b)
            => a.Intersections.Contains(b.Id) || b.Intersections.Contains(a.Id);

        /// <summary>
        /// Basisprioriteit uit sensoren (1 of 5 punten per TL).
        /// </summary>
        private int GetPriority(Direction d)
        {
            int p = 0;
            foreach (var tl in d.TrafficLights)
            {
                bool front = tl.Sensors.Any(s => s.Position == SensorPosition.Front && s.IsActivated);
                bool back = tl.Sensors.Any(s => s.Position == SensorPosition.Back && s.IsActivated);
                p += (front && back) ? 5 : (front || back ? 1 : 0);
            }
            return p;
        }

        /// <summary>
        /// Aging bonus: richtingen krijgen extra naarmate ze wachten.
        /// </summary>
        private int GetEffectivePriority(Direction d)
        {
            int baseP = GetPriority(d);
            var last = lastGreenTimes.TryGetValue(d.Id, out var t) ? t : DateTime.Now;
            int agingBonus = (int)((DateTime.Now - last).TotalSeconds / AGING_SCALE_SECONDS);
            return baseP + agingBonus;
        }

        /// <summary>
        /// Lees sensor-JSON en update elke TrafficLight.Sensors.
        /// </summary>
        private void ProcessSensorMessage()
        {
            var raw = communicator.LaneSensorData;
            if (string.IsNullOrEmpty(raw)) return;

            try
            {
                var map = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(raw);
                if (map == null) return;

                foreach (var kv in map)
                {
                    var tl = directions.SelectMany(d => d.TrafficLights)
                                       .FirstOrDefault(t => t.Id == kv.Key);
                    if (tl == null) continue;

                    foreach (var s in tl.Sensors)
                    {
                        if (s.Position == SensorPosition.Front && kv.Value.TryGetValue("voor", out bool fv))
                            s.IsActivated = fv;
                        if (s.Position == SensorPosition.Back && kv.Value.TryGetValue("achter", out bool bv))
                            s.IsActivated = bv;
                    }
                }
            }
            catch
            {
                // negeer parse-fouten
            }
        }

        /// <summary>
        /// Publiceer huidige kleur per TL en brug.
        /// </summary>
        private void SendTrafficLightStates()
        {
            var raw = communicator.LaneSensorData;
            if (string.IsNullOrEmpty(raw)) return;

            var map = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(raw);
            if (map == null) return;

            var output = map.Keys.ToDictionary(
                id => id,
                id =>
                {
                    var tl = directions.SelectMany(d => d.TrafficLights).First(t => t.Id == id);
                    var dir = directions.First(d => d.TrafficLights.Contains(tl));
                    return dir.Color switch
                    {
                        LightColor.Green => "groen",
                        LightColor.Orange => "oranje",
                        _ => "rood"
                    };
                });

            // ook brugstatus
            output["81.1"] = bridgeController.CurrentBridgeState;
            communicator.PublishMessage("stoplichten", output);
        }
    }
}
