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
        private const int ORANGE_DURATION = 2000;
        private const int DEFAULT_GREEN_DURATION = 6000;
        private const int SHORT_GREEN_DURATION = 3000;
        private const int PRIORITY_THRESHOLD = 3;
        private const int HIGH_PRIORITY_THRESHOLD = 6;
        private const double AGING_SCALE_SECONDS = 7;

        // Brug- en cycle
        private const int BRIDGE_GREEN_DURATION = 9000;
        private const int BRIDGE_ORANGE_DURATION = 9000;
        private const int POST_BRIDGE_NORMAL_PHASE_MS = 30000;
        private const int BRIDGE_COOLDOWN_SECONDS = 20;

        // ================================
        //       RUNTIME FIELDS
        // ================================
        private DateTime lastSwitchTime = DateTime.Now;
        private DateTime lastOrangeTime = DateTime.Now;

        private List<Direction> currentGreenDirections = new List<Direction>();
        private List<Direction> currentOrangeDirections = new List<Direction>();

        private static Dictionary<int, DateTime> lastGreenTimes = new Dictionary<int, DateTime>();

        private bool isHandlingPriorityVehicle = false;

        private Communicator communicator;
        private List<Direction> directions;

        private Task bridgeTask;
        private bool isBridgeCycleRunning = false;
        private readonly object bridgeLock = new object();

        private readonly int bridgeDirectionA = 71;
        private readonly int bridgeDirectionB = 72;

        private bool bridgeUsedThisCycle = false;
        private bool postBridgeNormalPhaseActive = false;
        private DateTime postBridgePhaseStartTime;
        private DateTime lastBridgeClosedTime = DateTime.MinValue;

        // Software vs fysiek
        private string currentBridgeState = "rood";
        private string physicalBridgeState = "dicht";

        // ================================
        //           CONSTRUCTOR
        // ================================
        public TrafficLightController(Communicator communicator, List<Direction> directions)
        {
            this.communicator = communicator;
            this.directions = directions;
            SetInitialBridgeState();
        }

        public static void InitializeLastGreenTimes(List<Direction> directions)
        {
            foreach (var direction in directions)
            {
                lastGreenTimes[direction.Id] = DateTime.Now;
            }
        }

        private void SetInitialBridgeState()
        {
            var dir71 = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dir72 = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            currentBridgeState = "rood";
            if (dir71 != null) dir71.Color = LightColor.Red;
            if (dir72 != null) dir72.Color = LightColor.Red;

            if (dir71 != null)
            {
                foreach (var conflictId in dir71.Intersections)
                {
                    var conflictDir = directions.FirstOrDefault(d => d.Id == conflictId);
                    if (conflictDir != null && conflictDir.Id != bridgeDirectionA && conflictDir.Id != bridgeDirectionB)
                    {
                        conflictDir.Color = LightColor.Green;
                    }
                }
            }
            if (dir72 != null)
            {
                foreach (var conflictId in dir72.Intersections)
                {
                    var conflictDir = directions.FirstOrDefault(d => d.Id == conflictId);
                    if (conflictDir != null && conflictDir.Id != bridgeDirectionA && conflictDir.Id != bridgeDirectionB)
                    {
                        conflictDir.Color = LightColor.Green;
                    }
                }
            }
            SendTrafficLightStates();
        }

        // ================================
        //       MAIN LOOP
        // ================================
        public async Task TrafficLightCycleLoop(CancellationToken token)
        {
            if (directions.Any())
            {
                SwitchTrafficLights();
                SendTrafficLightStates();
            }

            while (!token.IsCancellationRequested)
            {
                ProcessSensorMessage();
                ProcessBridgeSensorData();
                if (!postBridgeNormalPhaseActive)
                {
                    CheckBridgeRequests();
                }
                else
                {
                    double elapsed = (DateTime.Now - postBridgePhaseStartTime).TotalMilliseconds;
                    if (elapsed >= POST_BRIDGE_NORMAL_PHASE_MS)
                    {
                        postBridgeNormalPhaseActive = false;
                        bridgeUsedThisCycle = false;
                        Console.WriteLine("Post-bridge fase voorbij, brug kan weer openen volgende ronde.");
                    }
                }

                UpdateTrafficLights();
                await Task.Delay(500, token);
            }
        }

        // ================================
        //   PROCESS BRIDGE STATE
        // ================================
        private void ProcessBridgeSensorData()
        {
            if (string.IsNullOrEmpty(communicator.BridgeSensorData)) return;

            try
            {
                var bridgeData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(communicator.BridgeSensorData);
                if (bridgeData != null && bridgeData.ContainsKey("81.1"))
                {
                    var inner = bridgeData["81.1"];
                    if (inner.ContainsKey("state"))
                    {
                        string st = inner["state"];
                        if (st == "open" || st == "dicht")
                        {
                            if (physicalBridgeState != st)
                            {
                                physicalBridgeState = st;
                                Console.WriteLine($"Fysieke brugstatus => {physicalBridgeState}");
                            }
                        }
                    }
                }
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine($"Fout bij deserializen BridgeSensorData: {ex.Message}");
            }
        }

        // ================================
        //       UPDATE TRAFFIC LIGHTS
        // ================================
        public void UpdateTrafficLights()
        {
            if (isHandlingPriorityVehicle) return;

            DateTime now = DateTime.Now;
            double timeSinceGreen = (now - lastSwitchTime).TotalMilliseconds;
            double timeSinceOrange = (now - lastOrangeTime).TotalMilliseconds;

            if (currentOrangeDirections.Any())
            {
                if (timeSinceOrange >= ORANGE_DURATION)
                {
                    SetLightsToRed();
                    SwitchTrafficLights();
                    SendTrafficLightStates();
                }
                return;
            }

            if (currentGreenDirections.Any())
            {
                int sumEffectivePriority = currentGreenDirections.Sum(d => GetEffectivePriority(d));
                int dynamicGreenDuration = sumEffectivePriority >= HIGH_PRIORITY_THRESHOLD
                    ? DEFAULT_GREEN_DURATION + 2000
                    : sumEffectivePriority < PRIORITY_THRESHOLD
                        ? SHORT_GREEN_DURATION
                        : DEFAULT_GREEN_DURATION;

                if (timeSinceGreen >= dynamicGreenDuration)
                {
                    SetLightsToOrange();
                    lastOrangeTime = DateTime.Now;
                    SendTrafficLightStates();
                }
                else
                {
                    var extraCandidates = GetExtraGreenCandidates();
                    if (extraCandidates.Any())
                    {
                        foreach (var extra in extraCandidates)
                        {
                            currentGreenDirections.Add(extra);
                            extra.Color = LightColor.Green;
                            lastGreenTimes[extra.Id] = DateTime.Now;
                        }
                        lastSwitchTime = DateTime.Now;
                        SendTrafficLightStates();
                    }
                }
                return;
            }

            if (!currentGreenDirections.Any())
            {
                SwitchTrafficLights();
                SendTrafficLightStates();
            }
        }

        private List<Direction> GetExtraGreenCandidates()
        {
            var excludedIds = GetBridgeIntersectionSet();
            var candidates = directions
                .Where(d => !excludedIds.Contains(d.Id))
                .Where(d => GetPriority(d) > 0 && !currentGreenDirections.Contains(d))
                .OrderByDescending(d => GetEffectivePriority(d))
                .ThenBy(d => d.Id)
                .ToList();

            var extraCandidates = new List<Direction>();
            foreach (var candidate in candidates)
            {
                bool conflictWithCurrentGreen = currentGreenDirections
                    .Concat(extraCandidates)
                    .Any(g => HasConflict(g, candidate));

                if (conflictWithCurrentGreen) continue;
                extraCandidates.Add(candidate);
            }
            return extraCandidates;
        }

        private void SwitchTrafficLights()
        {
            var excludedIds = GetBridgeIntersectionSet();
            var availableDirections = directions
                .Where(d => !excludedIds.Contains(d.Id))
                .Where(d => GetPriority(d) > 0)
                .OrderByDescending(d => GetEffectivePriority(d))
                .ThenBy(d => d.Id)
                .ToList();

            if (!availableDirections.Any())
            {
                Console.WriteLine("Geen beschikbare richtingen voor groen (brug + intersecties).");
                return;
            }

            var newGreenGroup = new List<Direction>();
            foreach (var candidate in availableDirections)
            {
                if (newGreenGroup.Any(g => HasConflict(g, candidate)))
                    continue;
                newGreenGroup.Add(candidate);
            }

            if (!newGreenGroup.Any())
            {
                Console.WriteLine("Geen niet-conflicterende richtingen gevonden.");
                return;
            }

            if (newGreenGroup.SequenceEqual(currentGreenDirections))
            {
                Console.WriteLine("Groen-groep ongewijzigd.");
                return;
            }

            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Red;
            }
            currentGreenDirections.Clear();

            currentGreenDirections = newGreenGroup;
            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Green;
                lastGreenTimes[dir.Id] = DateTime.Now;
            }
            lastSwitchTime = DateTime.Now;
        }

        private HashSet<int> GetBridgeIntersectionSet()
        {
            var excluded = new HashSet<int>();
            var dir71 = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dir72 = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            if (dir71 != null)
            {
                excluded.Add(dir71.Id);
                foreach (var conflictId in dir71.Intersections)
                    excluded.Add(conflictId);
            }
            if (dir72 != null)
            {
                excluded.Add(dir72.Id);
                foreach (var conflictId in dir72.Intersections)
                    excluded.Add(conflictId);
            }
            return excluded;
        }

        private bool HasConflict(Direction d1, Direction d2)
        {
            if (!d1.Intersections.Any() || !d2.Intersections.Any()) return false;
            return d1.Intersections.Contains(d2.Id) || d2.Intersections.Contains(d1.Id);
        }

        private void SetLightsToOrange()
        {
            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Orange;
            }
            currentOrangeDirections = new List<Direction>(currentGreenDirections);
            currentGreenDirections.Clear();
        }

        private void SetLightsToRed()
        {
            foreach (var dir in currentOrangeDirections)
            {
                dir.Color = LightColor.Red;
            }
            currentOrangeDirections.Clear();
        }

        private int GetPriority(Direction direction)
        {
            int priority = 0;
            foreach (var tl in direction.TrafficLights)
            {
                bool front = tl.Sensors.Any(s => s.Position == SensorPosition.Front && s.IsActivated);
                bool back = tl.Sensors.Any(s => s.Position == SensorPosition.Back && s.IsActivated);
                priority += (front && back) ? 5 : (front || back ? 1 : 0);
            }
            return priority;
        }

        private int GetEffectivePriority(Direction direction)
        {
            int basePriority = GetPriority(direction);
            DateTime lastGreen = lastGreenTimes.ContainsKey(direction.Id)
                ? lastGreenTimes[direction.Id]
                : DateTime.Now;
            int agingBonus = (int)((DateTime.Now - lastGreen).TotalSeconds / AGING_SCALE_SECONDS);
            return basePriority + agingBonus;
        }

        private void ProcessSensorMessage()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData)) return;

            try
            {
                var sensorData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
                if (sensorData == null) return;

                foreach (var (trafficLightId, sensors) in sensorData)
                {
                    var trafficLight = directions
                        .SelectMany(d => d.TrafficLights)
                        .FirstOrDefault(tl => tl.Id == trafficLightId);
                    if (trafficLight == null) continue;

                    foreach (var sensor in trafficLight.Sensors)
                    {
                        if (sensor.Position == SensorPosition.Front && sensors.TryGetValue("voor", out bool frontValue))
                            sensor.IsActivated = frontValue;
                        else if (sensor.Position == SensorPosition.Back && sensors.TryGetValue("achter", out bool backValue))
                            sensor.IsActivated = backValue;
                    }
                }
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine($"Fout bij deserializen sensor data: {ex.Message}");
            }
        }

        // ================================
        //     SEND TRAFFIC STATES
        // ================================
        private void SendTrafficLightStates()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData)) return;

            var sensorData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
            if (sensorData == null) return;

            var stateDict = sensorData.Keys
                .Select(tlId => directions.SelectMany(d => d.TrafficLights)
                                          .FirstOrDefault(tl => tl.Id == tlId))
                .Where(tl => tl != null)
                .Select(tl => new
                {
                    Id = tl.Id,
                    State = directions.FirstOrDefault(d => d.TrafficLights.Contains(tl))?.Color == LightColor.Green
                                ? "groen"
                            : directions.FirstOrDefault(d => d.TrafficLights.Contains(tl))?.Color == LightColor.Orange
                                ? "oranje"
                                : "rood"
                })
                .ToDictionary(x => x.Id, x => x.State);

            stateDict["81.1"] = currentBridgeState;

            communicator.PublishMessage("stoplichten", stateDict);
        }

        // ================================
        //      BRUG-LOGIC
        // ================================
        private void CheckBridgeRequests()
        {
            // Cooldown-check
            var timeSinceClosed = (DateTime.Now - lastBridgeClosedTime).TotalSeconds;
            if (timeSinceClosed < BRIDGE_COOLDOWN_SECONDS)
            {
                // In cooldown
                return;
            }

            if (bridgeUsedThisCycle) return;
            if (isBridgeCycleRunning) return;

            var dir71 = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dir72 = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);
            if (dir71 == null || dir72 == null) return;

            int priority71 = GetPriority(dir71);
            int priority72 = GetPriority(dir72);

            // We check if either side > 0
            if (priority71 > 0 || priority72 > 0)
            {
                lock (bridgeLock)
                {
                    if (!isBridgeCycleRunning)
                    {
                        isBridgeCycleRunning = true;
                        bridgeTask = Task.Run(async () =>
                        {
                            try
                            {
                                Console.WriteLine("=== Start Single Bridge Session ===");
                                // Bepaal beide prioriteiten
                                //  - If both > 0 => we handle them one after the other (without closing in between!)
                                //  - If only one > 0 => just handle that side.

                                // We chain them in 1 session
                                await HandleBridgeSession();
                            }
                            finally
                            {
                                isBridgeCycleRunning = false;
                                bridgeUsedThisCycle = true;
                                postBridgeNormalPhaseActive = true;
                                postBridgePhaseStartTime = DateTime.Now;
                                lastBridgeClosedTime = DateTime.Now;
                                Console.WriteLine("Brugcyclus geheel klaar; cooldown started.");
                            }
                        });
                    }
                }
            }
        }

        /// <summary>
        /// HandleBridgeSession() opent de brug 1x en laat alle benodigde kanten (71 en/of 72)
        /// varen voordat de brug weer fysiek dichtgaat.
        /// </summary>
        private async Task HandleBridgeSession()
        {
            // 1) Bepaal of 71 prioriteit heeft en of 72 prioriteit heeft
            var dir71 = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dir72 = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);
            if (dir71 == null || dir72 == null) return;

            int p71 = GetPriority(dir71);
            int p72 = GetPriority(dir72);

            bool sideAHasPriority = (p71 > 0);
            bool sideBHasPriority = (p72 > 0);

            // 2) Als geen van beide >0, meteen return
            if (!sideAHasPriority && !sideBHasPriority)
            {
                Console.WriteLine("Geen boot, geen brug nodig.");
                return;
            }

            // 3) Forceer autoverkeer dat kruist => rood
            await ForceConflictDirectionsToRed(bridgeDirectionA);
            await ForceConflictDirectionsToRed(bridgeDirectionB);

            // 4) Wacht tot brug fysiek "open"
            Console.WriteLine("... Wachten tot brug fysiek OPEN is ...");
            currentBridgeState = "groen";
            while (physicalBridgeState != "open")
            {
                await Task.Delay(500);
                ProcessBridgeSensorData();
            }
            // Brug is fysiek open
            Console.WriteLine("Fysieke brug is nu OPEN => we gaan varen.");

            // 5) Als 71 prioriteit had => 7s groen + 7s oranje
            if (sideAHasPriority)
            {
                await LetBoatsPass(bridgeDirectionA, "71");
            }

            // 6) Check nogmaals of 72 intussen prioriteit heeft
            //    (of had die al => sideBHasPriority)
            p72 = GetPriority(dir72);
            sideBHasPriority = (p72 > 0) || sideBHasPriority;
            if (sideBHasPriority)
            {
                await LetBoatsPass(bridgeDirectionB, "72");
            }

            // 7) Nu zijn beide kanten klaar => brug software op "rood",
            //    Wachten tot fysiek "dicht"
            currentBridgeState = "rood";
            SendTrafficLightStates();
            Console.WriteLine("Nu brug fysiek sluiten (wachten op 'dicht'..)");
            while (physicalBridgeState != "dicht")
            {
                await Task.Delay(500);
                ProcessBridgeSensorData();
            }

            // 8) Kruizende wegen => oranje => rood => etc.
            // MakeCrossingOranjeBeforeGreen();
            MakeCrossingGreen();
            Console.WriteLine("Beide kanten gevaren en brug fysiek dicht => autoverkeer weer groen.");
        }

        /// <summary>
        /// Laat 1 kant (dirId) 7s groen + 7s oranje varen zonder de brug tussentijds fysiek dicht te doen.
        /// </summary>
        private async Task LetBoatsPass(int dirId, string sideLabel)
        {
            Console.WriteLine($"... Boten laten passeren op kant {sideLabel} ...");
            var direction = directions.First(d => d.Id == dirId);
            if (direction == null) return;

            // 7s groen
            direction.Color = LightColor.Green;
            Console.WriteLine($"Richting {dirId} staat nu GROEN.");
            SendTrafficLightStates();
            await Task.Delay(BRIDGE_GREEN_DURATION);

            // 7s oranje
            direction.Color = LightColor.Orange;
            Console.WriteLine($"Richting {dirId} staat nu ORANJE.");
            SendTrafficLightStates();
            await Task.Delay(BRIDGE_ORANGE_DURATION + 1000);

            // Zet dan op rood
            direction.Color = LightColor.Red;
            Console.WriteLine($"Richting {dirId} staat nu ROOD (varen klaar).");
            SendTrafficLightStates();
        }

        // ==> De user wilde: "als we de brug dicht hebben, dan oranje => rood => ...
        //    NU: We doen oranje voordat we final op groen zetten
        private void MakeCrossingOranjeBeforeGreen()
        {
            var crossing = GetAllCrossingDirections();
            // Zet oranje
            foreach (var cdir in crossing)
            {
                cdir.Color = LightColor.Orange;
                Console.WriteLine($"Kruizend verkeer {cdir.Id} => ORANJE.");
            }
            Thread.Sleep(ORANGE_DURATION);

            // Zet rood
            foreach (var cdir in crossing)
            {
                cdir.Color = LightColor.Red;
                Console.WriteLine($"Kruizend verkeer {cdir.Id} => ROOD.");
            }
        }

        private void MakeCrossingGreen()
        {
            var crossing = GetAllCrossingDirections();
            foreach (var cdir in crossing)
            {
                cdir.Color = LightColor.Green;
                Console.WriteLine($"Kruizend verkeer {cdir.Id} => GROEN.");
            }
            SendTrafficLightStates();
        }

        /// <summary>
        /// Kruizende directions van 71 en 72 (excl. 71/72 zelf)
        /// </summary>
        private List<Direction> GetAllCrossingDirections()
        {
            var list = new List<Direction>();
            var dir71 = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dir72 = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            if (dir71 != null)
            {
                foreach (var c in dir71.Intersections)
                {
                    var cdir = directions.FirstOrDefault(d => d.Id == c);
                    if (cdir != null && cdir.Id != bridgeDirectionA && cdir.Id != bridgeDirectionB)
                    {
                        list.Add(cdir);
                    }
                }
            }
            if (dir72 != null)
            {
                foreach (var c in dir72.Intersections)
                {
                    var cdir = directions.FirstOrDefault(d => d.Id == c);
                    if (cdir != null && cdir.Id != bridgeDirectionA && cdir.Id != bridgeDirectionB)
                    {
                        list.Add(cdir);
                    }
                }
            }
            return list.Distinct().ToList();
        }

        private async Task ForceConflictDirectionsToRed(int bridgeDir)
        {
            // Verzamel alle conflicterende richtingen
            var conflictDirections = directions
                .Where(d => d.Id != bridgeDir && d.Intersections.Contains(bridgeDir))
                .ToList();

            // 1) Bepaal welke ervan nu groen zijn -> eerst oranje
            foreach (var dir in conflictDirections)
            {
                if (dir.Color == LightColor.Green)
                {
                    dir.Color = LightColor.Orange;
                    Console.WriteLine($"Richting {dir.Id} => ORANJE (brug {bridgeDir}).");
                }
            }
            // Even wachten op de oranje-duur, alleen als er minstens 1 groene was
            // (of je altijd wilt wachten, kan je kiezen)
            if (conflictDirections.Any(d => d.Color == LightColor.Orange))
            {
                await Task.Delay(ORANGE_DURATION);
            }

            // 2) Zet ze nu op rood
            foreach (var dir in conflictDirections)
            {
                dir.Color = LightColor.Red;
                Console.WriteLine($"Richting {dir.Id} => ROOD (brug {bridgeDir}).");
            }

            // 3) Haal ze ook uit currentGreenDirections/currentOrangeDirections
            currentGreenDirections.RemoveAll(d => conflictDirections.Contains(d));
            currentOrangeDirections.RemoveAll(d => conflictDirections.Contains(d));
        }
    }
}
