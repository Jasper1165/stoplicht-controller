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
        private const int ORANGE_DURATION = 2000;         // 2 seconden oranje voor 'gewone' richtingen
        private const int DEFAULT_GREEN_DURATION = 8000;  // 8 seconden normaal groen
        private const int SHORT_GREEN_DURATION = 3000;    // 3 seconden kort groen
        private const int PRIORITY_THRESHOLD = 3;
        private const int HIGH_PRIORITY_THRESHOLD = 6;
        private const double AGING_SCALE_SECONDS = 7;     // elke 7s wachten => +1 aging

        // Brug-specifiek
        private const int BRIDGE_GREEN_DURATION = 7000;   // 7s groen voor brug
        private const int BRIDGE_ORANGE_DURATION = 7000;  // 7s oranje voor brug

        // ======= Nieuw: na 1 brugcyclus forceren we 30s gewone verkeersregeling =====
        private const int POST_BRIDGE_NORMAL_PHASE_MS = 30000; // 30s “normale cyclus verplicht”

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

        // --- Brug-cycli ---
        private Task bridgeTask;
        private bool isBridgeCycleRunning = false;
        private readonly object bridgeLock = new object();

        // Identificatie van de brugrichtingen
        private readonly int bridgeDirectionA = 71;
        private readonly int bridgeDirectionB = 72;

        // ======= Nieuw: vlag en timer om brug-gebruik slechts 1x per cyclus te doen =====
        private bool bridgeUsedThisCycle = false;   // Zodra we de brug open hebben gehad, staat dit op true
        private bool postBridgeNormalPhaseActive = false;
        private DateTime postBridgePhaseStartTime;

        // ================================
        //           CONSTRUCTOR
        // ================================
        public TrafficLightController(Communicator communicator, List<Direction> directions)
        {
            this.communicator = communicator;
            this.directions = directions;

            // Zet direct bij het aanmaken:
            // direction 71/72 => rood, hun intersecties => groen
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
            // 1) Stel direction 71 en 72 zelf in op ROOD
            var dir71 = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dir72 = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            if (dir71 != null)
            {
                dir71.Color = LightColor.Red;
                Console.WriteLine($"Richting {dir71.Id} op ROOD (init).");
            }
            if (dir72 != null)
            {
                dir72.Color = LightColor.Red;
                Console.WriteLine($"Richting {dir72.Id} op ROOD (init).");
            }

            // 2) Zet alle “conflicterende”/kruizende richtingen van 71 en 72 op GROEN
            //    zodat auto’s meteen kunnen doorrijden
            if (dir71 != null)
            {
                foreach (var conflictId in dir71.Intersections)
                {
                    var conflictDir = directions.FirstOrDefault(d => d.Id == conflictId);
                    if (conflictDir != null
                        && conflictDir.Id != bridgeDirectionA
                        && conflictDir.Id != bridgeDirectionB)
                    {
                        conflictDir.Color = LightColor.Green;
                        Console.WriteLine($"Richting {conflictDir.Id} op GROEN (init, kruist 71).");
                    }
                }
            }
            if (dir72 != null)
            {
                foreach (var conflictId in dir72.Intersections)
                {
                    var conflictDir = directions.FirstOrDefault(d => d.Id == conflictId);
                    if (conflictDir != null
                        && conflictDir.Id != bridgeDirectionA
                        && conflictDir.Id != bridgeDirectionB)
                    {
                        conflictDir.Color = LightColor.Green;
                        Console.WriteLine($"Richting {conflictDir.Id} op GROEN (init, kruist 72).");
                    }
                }
            }

            // Optioneel: stuur meteen de eerste keer de states naar de communicator
            SendTrafficLightStates();
        }


        // ================================
        //       MAIN CYCLE LOOP
        // ================================
        public async Task TrafficLightCycleLoop(CancellationToken token)
        {
            if (directions.Any())
            {
                SwitchTrafficLights();
                SendTrafficLightStates();
            }

            // Hoofdlus
            while (!token.IsCancellationRequested)
            {
                ProcessSensorMessage();

                // 1) Als we niet in post-bridge fase zitten, checken we brugverzoeken
                if (!postBridgeNormalPhaseActive)
                {
                    CheckBridgeRequests();
                }
                else
                {
                    // Als we in de post-bridge fase zitten, check of die nog duurt
                    double elapsed = (DateTime.Now - postBridgePhaseStartTime).TotalMilliseconds;
                    if (elapsed >= POST_BRIDGE_NORMAL_PHASE_MS)
                    {
                        // Post-bridge-fase is klaar => reset
                        postBridgeNormalPhaseActive = false;
                        bridgeUsedThisCycle = false;  // Nieuwe ronde kan brug weer openen
                        Console.WriteLine("Post-bridge normale fase is afgelopen, brug kan in volgende ronde weer openen.");
                    }
                }

                // 2) Update de normale stoplichtlogica
                UpdateTrafficLights();

                // Even wachten
                await Task.Delay(500, token);
            }
        }

        // ================================
        //       UPDATE-LOGICA
        // ================================
        public void UpdateTrafficLights()
        {
            if (isHandlingPriorityVehicle) return;

            DateTime now = DateTime.Now;
            double timeSinceGreen = (now - lastSwitchTime).TotalMilliseconds;
            double timeSinceOrange = (now - lastOrangeTime).TotalMilliseconds;

            // Oranje -> rood
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

            // Groen -> oranje?
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
                            Console.WriteLine($"Extra richting {extra.Id} toegevoegd (priority={GetEffectivePriority(extra)}).");
                        }
                        lastSwitchTime = DateTime.Now;
                        SendTrafficLightStates();
                    }
                }
                return;
            }

            // Niets groen
            if (!currentGreenDirections.Any())
            {
                SwitchTrafficLights();
                SendTrafficLightStates();
            }
        }

        /// <summary>
        /// Selecteert extra richtingen die conflictvrij erbij kunnen.
        /// </summary>
        private List<Direction> GetExtraGreenCandidates()
        {
            // Bouw de excluded-lijst van de brug en hun intersecties
            var excludedIds = GetBridgeIntersectionSet();

            var candidates = directions
                .Where(d => !excludedIds.Contains(d.Id)) // <-- Brug & intersecties uitsluiten
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

                if (conflictWithCurrentGreen)
                    continue;

                // We kunnen hier evt. IsBridgeOpen-check nog hebben, maar
                // als we ze sws uitsluiten, is dat niet meer nodig

                extraCandidates.Add(candidate);
            }
            return extraCandidates;
        }


        /// <summary>
        /// Bepaalt nieuwe hoofdrichting(en) op groen, op basis van prioriteiten
        /// </summary>
        private void SwitchTrafficLights()
        {
            // Stel eerst een verzameling samen van wat we “excluden” uit de normale cyclus:
            // - De brugrichtingen zelf (71, 72)
            // - Alles wat intersect met 71 of 72
            var excludedIds = GetBridgeIntersectionSet();

            // Hier filteren we: “alle richtingen behalve brug en hun intersecties”
            var availableDirections = directions
                .Where(d => !excludedIds.Contains(d.Id))            // exclude brug + intersecties
                .Where(d => GetPriority(d) > 0)
                .OrderByDescending(d => GetEffectivePriority(d))
                .ThenBy(d => d.Id)
                .ToList();

            if (!availableDirections.Any())
            {
                Console.WriteLine("Geen beschikbare richtingen gevonden voor groen (brug + intersecties uitgesloten).");
                return;
            }

            // Zelfde logic als anders: bouw de newGreenGroup
            var newGreenGroup = new List<Direction>();
            foreach (var candidate in availableDirections)
            {
                // ... check conflicts, etc.
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
                Console.WriteLine("Groen-groep blijft ongewijzigd.");
                return;
            }

            // Oude greens -> rood
            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Red;
                Console.WriteLine($"Richting {dir.Id} op rood.");
            }
            currentGreenDirections.Clear();

            // Nieuwe greens
            currentGreenDirections = newGreenGroup;
            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Green;
                lastGreenTimes[dir.Id] = DateTime.Now;
                Console.WriteLine($"Richting {dir.Id} nu groen (priority={GetEffectivePriority(dir)}).");
            }
            lastSwitchTime = DateTime.Now;
        }

        /// <summary>
        /// Bepaal de IDs van de brugrichtingen (71,72) plus al hun intersecties.
        /// Deze IDs sluiten we uit in de normale cyclus.
        /// </summary>
        private HashSet<int> GetBridgeIntersectionSet()
        {
            var excluded = new HashSet<int>();

            // Pak direction 71 en 72
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

        private bool HasConflictWithBridge(Direction direction)
        {
            return direction.Id == bridgeDirectionA
                   || direction.Id == bridgeDirectionB
                   || direction.Intersections.Contains(bridgeDirectionA)
                   || direction.Intersections.Contains(bridgeDirectionB);
        }

        private void SetLightsToOrange()
        {
            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Orange;
                Console.WriteLine($"Richting {dir.Id} staat op oranje.");
            }
            currentOrangeDirections = new List<Direction>(currentGreenDirections);
            currentGreenDirections.Clear();
        }

        private void SetLightsToRed()
        {
            foreach (var dir in currentOrangeDirections)
            {
                dir.Color = LightColor.Red;
                Console.WriteLine($"Richting {dir.Id} staat op rood.");
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

        // ================================
        //       SENSOR MESSAGING
        // ================================
        private void ProcessSensorMessage()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData)) return;

            Dictionary<string, Dictionary<string, bool>> sensorData = null;
            try
            {
                sensorData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine($"Fout bij deserializen sensor data: {ex.Message}");
                return;
            }
            if (sensorData == null)
            {
                Console.WriteLine("Sensor data is null.");
                return;
            }

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

            communicator.PublishMessage("stoplichten", stateDict);
        }

        // ================================
        //      BRUG-SPECIFIEKE LOGIC
        // ================================
        private void CheckBridgeRequests()
        {
            // 1) Als de brug deze cyclus al één keer geopend is, niet nog eens
            if (bridgeUsedThisCycle) return;

            // 2) Als de brug al in de open-cyclus zit, ook niets doen
            if (isBridgeCycleRunning) return;

            // 3) Check prioriteit
            var dir71 = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dir72 = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);
            if (dir71 == null || dir72 == null) return;

            int priority71 = GetPriority(dir71);
            int priority72 = GetPriority(dir72);

            // 4) Als (71 of 72) > 0, start de brugcyclus
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
                                Console.WriteLine("=== Start ÉÉNMALIGE brugcyclus (beide kanten indien nodig) ===");

                                // --- 1) Bepaal wie er eerst gaat ---
                                int directionId = (priority71 >= priority72)
                                    ? bridgeDirectionA
                                    : bridgeDirectionB;

                                // --- 2) Open brug voor de "eerste" kant ---
                                await HandleSingleBridgeSide(directionId);

                                // --- 3) Kijk of de "andere kant" óók nog boot-prioriteit heeft ---
                                // (herbereken voor de zekerheid, sensoren kunnen veranderd zijn)
                                priority71 = GetPriority(dir71);
                                priority72 = GetPriority(dir72);

                                if (directionId == bridgeDirectionA && priority72 > 0)
                                {
                                    Console.WriteLine("Andere kant (72) heeft óók prioriteit; open brug nu voor 72");
                                    await HandleSingleBridgeSide(bridgeDirectionB);
                                }
                                else if (directionId == bridgeDirectionB && priority71 > 0)
                                {
                                    Console.WriteLine("Andere kant (71) heeft óók prioriteit; open brug nu voor 71");
                                    await HandleSingleBridgeSide(bridgeDirectionA);
                                }

                                // Hierna is de brug echt klaar (beide kanten gedaan)
                            }
                            finally
                            {
                                // 4) Sluit de cycle definitief af
                                isBridgeCycleRunning = false;
                                bridgeUsedThisCycle = true;

                                // 5) Start direct de "normale" fase van X seconden
                                postBridgeNormalPhaseActive = true;
                                postBridgePhaseStartTime = DateTime.Now;
                                Console.WriteLine("Brugcyclus voltooid (beide kanten). X seconden lang normale cyclus afdraaien.");
                            }
                        });
                    }
                }
            }
        }

        private bool IsBridgeOpen()
        {
            return isBridgeCycleRunning;
        }

        private async Task HandleSingleBridgeSide(int directionId)
        {
            Console.WriteLine($"Brugcyclus voor richting {directionId} start...");

            ForceConflictDirectionsToRed(directionId);

            // 1) Brug open => 7s groen
            var dir = directions.First(d => d.Id == directionId);
            dir.Color = LightColor.Green;
            Console.WriteLine($"Richting {directionId} staat nu GROEN (brug open).");
            SendTrafficLightStates();
            await Task.Delay(BRIDGE_GREEN_DURATION);

            // 2) 7s oranje
            dir.Color = LightColor.Orange;
            Console.WriteLine($"Richting {directionId} staat nu ORANJE.");
            SendTrafficLightStates();
            await Task.Delay(BRIDGE_ORANGE_DURATION);

            // 3) Brug dicht => zet BEIDE brugrichtingen op rood
            dir.Color = LightColor.Red;
            Console.WriteLine($"Richting {directionId} staat op ROOD (brug dicht).");

            // Zet ook de andere brugrichting (71 of 72) op rood
            var otherBridgeId = (directionId == bridgeDirectionA) ? bridgeDirectionB : bridgeDirectionA;
            var otherBridge = directions.FirstOrDefault(d => d.Id == otherBridgeId);
            if (otherBridge != null)
            {
                otherBridge.Color = LightColor.Red;
                Console.WriteLine($"Richting {otherBridge.Id} staat óók op ROOD (brug dicht).");
            }

            // 4) Zet alle kruizende wegen van 71 én 72 op groen
            var dir71 = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dir72 = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            if (dir71 != null)
            {
                foreach (var conflictId in dir71.Intersections)
                {
                    var conflictDir = directions.FirstOrDefault(dx => dx.Id == conflictId);
                    if (conflictDir != null
                        && conflictDir.Id != bridgeDirectionA  // skip 71 zelf
                        && conflictDir.Id != bridgeDirectionB) // skip 72
                    {
                        conflictDir.Color = LightColor.Green;
                        Console.WriteLine($"Richting {conflictDir.Id} staat op GROEN (auto's, kruist brug {dir71.Id}).");
                    }
                }
            }

            if (dir72 != null)
            {
                foreach (var conflictId in dir72.Intersections)
                {
                    var conflictDir = directions.FirstOrDefault(dx => dx.Id == conflictId);
                    if (conflictDir != null
                        && conflictDir.Id != bridgeDirectionA
                        && conflictDir.Id != bridgeDirectionB)
                    {
                        conflictDir.Color = LightColor.Green;
                        Console.WriteLine($"Richting {conflictDir.Id} staat op GROEN (auto's, kruist brug {dir72.Id}).");
                    }
                }
            }

            SendTrafficLightStates();

            Console.WriteLine($"Brugcyclus voor richting {directionId} klaar.");
        }

        private void ForceConflictDirectionsToRed(int bridgeDir)
        {
            // Zorg dat alles wat intersect met de brugrichting rood staat
            foreach (var d in directions)
            {
                if (d.Id == bridgeDir) continue;
                if (d.Intersections.Contains(bridgeDir))
                {
                    d.Color = LightColor.Red;
                    Console.WriteLine($"Richting {d.Id} forced RED (conflict met brug {bridgeDir}).");
                }
            }

            // Haal ze ook uit de currentGreen/currentOrange-lijsten
            currentGreenDirections.RemoveAll(d => d.Intersections.Contains(bridgeDir));
            currentOrangeDirections.RemoveAll(d => d.Intersections.Contains(bridgeDir));
        }
    }
}
