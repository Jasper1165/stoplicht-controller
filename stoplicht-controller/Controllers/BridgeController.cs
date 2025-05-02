// BridgeController.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

namespace stoplicht_controller.Managers
{
    public class BridgeController
    {
        // ================================
        //       CONFIG CONSTANTS
        // ================================
        private const int BRIDGE_GREEN_DURATION = 9000;
        private const int BRIDGE_ORANGE_DURATION = 9000;
        private const int POST_BRIDGE_NORMAL_PHASE_MS = 30000;
        private const int BRIDGE_COOLDOWN_SECONDS = 20;
        private const int SAFETY_CHECK_INTERVAL = 500;       // ms tussen safety checks
        private const int BRIDGE_SWITCH_EXTRA_DELAY_MS = 8000; // extra wachttijd bij switch

        // ================================
        //       RUNTIME FIELDS
        // ================================
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly Bridge bridge;

        private Task bridgeTask;
        private bool isBridgeCycleRunning = false;
        private readonly object bridgeLock = new object();

        private readonly int bridgeDirectionA = 71;
        private readonly int bridgeDirectionB = 72;

        private bool bridgeUsedThisCycle = false;
        private bool postBridgeNormalPhaseActive = false;
        private DateTime postBridgePhaseStartTime;
        private DateTime lastBridgeClosedTime = DateTime.MinValue;

        private string currentBridgeState = "rood";
        private string physicalBridgeState = "dicht";

        // ================================
        //       PUBLIC INTERFACES
        // ================================
        public string CurrentBridgeState => currentBridgeState;
        public HashSet<int> GetBridgeIntersectionSet()
        {
            var excluded = new HashSet<int>();
            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);
            if (dirA != null)
            {
                excluded.Add(dirA.Id);
                foreach (var id in dirA.Intersections) excluded.Add(id);
            }
            if (dirB != null)
            {
                excluded.Add(dirB.Id);
                foreach (var id in dirB.Intersections) excluded.Add(id);
            }
            return excluded;
        }

        // ================================
        //       CONSTRUCTOR
        // ================================
        public BridgeController(Communicator communicator, List<Direction> directions, Bridge bridge)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.bridge = bridge;
            SetInitialBridgeState();
        }

        private void SetInitialBridgeState()
        {
            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            currentBridgeState = "rood";
            if (dirA != null) dirA.Color = LightColor.Red;
            if (dirB != null) dirB.Color = LightColor.Red;

            Action<Direction> openConflicts = dir =>
            {
                foreach (var cid in dir.Intersections)
                {
                    var c = directions.FirstOrDefault(d => d.Id == cid);
                    if (c != null && c.Id != bridgeDirectionA && c.Id != bridgeDirectionB)
                        c.Color = LightColor.Green;
                }
            };
            if (dirA != null) openConflicts(dirA);
            if (dirB != null) openConflicts(dirB);

            SendTrafficLightStates();
        }

        // ================================
        //   SENSOR & UPDATE METHODS
        // ================================
        public void ProcessBridgeSensorData()
        {
            if (string.IsNullOrEmpty(communicator.BridgeSensorData)) return;
            try
            {
                var bridgeData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(communicator.BridgeSensorData);
                if (bridgeData != null && bridgeData.ContainsKey("81.1"))
                {
                    var inner = bridgeData["81.1"];
                    if (inner.TryGetValue("state", out string st) &&
                        (st == "open" || st == "dicht") &&
                        physicalBridgeState != st)
                    {
                        physicalBridgeState = st;
                        Console.WriteLine($"Fysieke brugstatus bijgewerkt naar: {physicalBridgeState}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fout bij verwerken van brugsensordata: {ex.Message}");
            }
        }

        public void ResetBridgeCycle()
        {
            if (postBridgeNormalPhaseActive &&
                (DateTime.Now - postBridgePhaseStartTime).TotalMilliseconds >= POST_BRIDGE_NORMAL_PHASE_MS)
            {
                Console.WriteLine("Post-brug normale fase voltooid, reset brugcyclus.");
                bridgeUsedThisCycle = false;
                postBridgeNormalPhaseActive = false;
            }
        }

        public void Update()
        {
            ProcessBridgeSensorData();
            ResetBridgeCycle();

            var elapsed = (DateTime.Now - lastBridgeClosedTime).TotalSeconds;
            if (elapsed < BRIDGE_COOLDOWN_SECONDS || bridgeUsedThisCycle || isBridgeCycleRunning)
                return;

            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);
            if (dirA == null || dirB == null) return;

            int pA = GetPriority(dirA), pB = GetPriority(dirB);
            if (pA <= 0 && pB <= 0) return;

            lock (bridgeLock)
            {
                if (!isBridgeCycleRunning)
                {
                    isBridgeCycleRunning = true;
                    bridgeTask = Task.Run(async () =>
                    {
                        try { await HandleBridgeSession(); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Fout tijdens brugsessie: {ex.Message}");
                            SetInitialBridgeState();
                        }
                        finally
                        {
                            isBridgeCycleRunning = false;
                            bridgeUsedThisCycle = true;
                            postBridgeNormalPhaseActive = true;
                            postBridgePhaseStartTime = DateTime.Now;
                            lastBridgeClosedTime = DateTime.Now;
                        }
                    });
                }
            }
        }

        private async Task HandleBridgeSession()
        {
            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);

            bool sideA = GetPriority(dirA) > 0;
            bool sideB = GetPriority(dirB) > 0;
            if (!sideA && !sideB) return;

            await ForceConflictDirectionsToRed(bridgeDirectionA);
            await ForceConflictDirectionsToRed(bridgeDirectionB);

            Console.WriteLine("Wachten tot geen voertuig op de brug...");
            await WaitUntilNoBridgeVehicle();

            // Open de brug
            currentBridgeState = "groen";
            SendTrafficLightStates();
            Console.WriteLine("Wachten tot de brug fysiek open is...");
            await WaitForPhysicalBridgeState("open");

            // Laat boten passeren aan kant A
            if (sideA)
            {
                await LetBoatsPass(bridgeDirectionA);
                // Tussen de botenchecks: als er nog een schip onder de brug is, even extra wachten
                while (bridge.VesselUnderBridge)
                {
                    Console.WriteLine($"Extra wachttijd van {BRIDGE_SWITCH_EXTRA_DELAY_MS}ms omdat er nog een schip onder de brug is.");
                    await Task.Delay(BRIDGE_SWITCH_EXTRA_DELAY_MS);
                }
            }

            // Daarna passeren aan kant B (als nog prioriteit)
            sideB = GetPriority(dirB) > 0 || sideB;
            if (sideB)
            {
                await LetBoatsPass(bridgeDirectionB);
            }

            Console.WriteLine("Wachten tot geen schip onder de brug...");
            await WaitUntilNoVesselUnderBridge();

            Console.WriteLine("Schip vrij, brug kan nu dicht. Software signaal verzenden...");
            currentBridgeState = "rood";
            SendTrafficLightStates();

            Console.WriteLine("Wachten tot de brug fysiek dicht is...");
            await WaitForPhysicalBridgeState("dicht");

            MakeCrossingGreen();
        }

        private async Task WaitUntilNoBridgeVehicle()
        {
            int retryCounter = 0, maxRetries = 60;
            while (true)
            {
                ProcessBridgeSensorData();
                Console.WriteLine($"Voertuig op brug status: {bridge.VehicleOnBridge}");
                if (!bridge.VehicleOnBridge)
                {
                    Console.WriteLine("Geen voertuig meer op de brug gedetecteerd.");
                    await Task.Delay(SAFETY_CHECK_INTERVAL);
                    if (!bridge.VehicleOnBridge) break;
                }
                if (++retryCounter >= maxRetries)
                {
                    Console.WriteLine("Veiligheidsklep geactiveerd: max wachttijd voor voertuig op brug bereikt.");
                    break;
                }
                await Task.Delay(SAFETY_CHECK_INTERVAL);
            }
        }

        private async Task WaitUntilNoVesselUnderBridge()
        {
            int retryCounter = 0, maxRetries = 180;
            int consecutiveNegativeReadings = 0, requiredNegativeReadings = 4;
            while (true)
            {
                ProcessBridgeSensorData();
                Console.WriteLine($"Schip onder brug status: {bridge.VesselUnderBridge}");
                if (!bridge.VesselUnderBridge)
                {
                    if (++consecutiveNegativeReadings >= requiredNegativeReadings)
                    {
                        Console.WriteLine("Voldoende bevestiging: geen schip meer onder de brug.");
                        break;
                    }
                }
                else
                {
                    if (consecutiveNegativeReadings > 0)
                    {
                        Console.WriteLine("Schip opnieuw gedetecteerd, reset negatieve telling.");
                        consecutiveNegativeReadings = 0;
                    }
                }
                if (++retryCounter >= maxRetries)
                {
                    Console.WriteLine("KRITIEKE WAARSCHUWING: Maximum wachttijd voor schip onder brug bereikt!");
                    retryCounter = maxRetries - 10;
                }
                await Task.Delay(SAFETY_CHECK_INTERVAL);
            }
        }

        private async Task WaitForPhysicalBridgeState(string targetState)
        {
            int retryCounter = 0, maxRetries = 240;
            while (physicalBridgeState != targetState)
            {
                ProcessBridgeSensorData();
                Console.WriteLine($"Wachten op fysieke status '{targetState}', nu '{physicalBridgeState}'");
                if (targetState == "dicht" && bridge.VesselUnderBridge)
                {
                    Console.WriteLine("WAARSCHUWING: schip nog onder brug bij sluiten, wacht tot vrij is...");
                    await WaitUntilNoVesselUnderBridge();
                }
                if (++retryCounter >= maxRetries)
                {
                    Console.WriteLine($"WAARSCHUWING: max wachttijd voor fysieke status '{targetState}' bereikt.");
                    retryCounter = maxRetries - 10;
                }
                await Task.Delay(SAFETY_CHECK_INTERVAL);
            }

            if (targetState == "dicht" && bridge.VesselUnderBridge)
            {
                Console.WriteLine("KRITIEKE SITUATIE: Brug is dicht maar schip onder brug!");
            }
        }

        private async Task LetBoatsPass(int dirId)
        {
            var dir = directions.First(d => d.Id == dirId);
            dir.Color = LightColor.Green; SendTrafficLightStates();
            await Task.Delay(BRIDGE_GREEN_DURATION);
            dir.Color = LightColor.Orange; SendTrafficLightStates();
            await Task.Delay(BRIDGE_ORANGE_DURATION + 3000);
            dir.Color = LightColor.Red; SendTrafficLightStates();
        }

        private void MakeCrossingGreen()
        {
            var all = new List<Direction>();
            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);
            all.AddRange(dirA.Intersections.Select(i => directions.First(d => d.Id == i)));
            all.AddRange(dirB.Intersections.Select(i => directions.First(d => d.Id == i)));
            foreach (var c in all.Distinct())
            {
                if (c.Id != bridgeDirectionA && c.Id != bridgeDirectionB)
                    c.Color = LightColor.Green;
            }
            SendTrafficLightStates();
        }

        private async Task ForceConflictDirectionsToRed(int bridgeDir)
        {
            var conflicts = directions.Where(d => d.Id != bridgeDir && d.Intersections.Contains(bridgeDir)).ToList();
            foreach (var c in conflicts.Where(d => d.Color == LightColor.Green))
                c.Color = LightColor.Orange;
            SendTrafficLightStates();
            if (conflicts.Any(d => d.Color == LightColor.Orange))
                await Task.Delay(BRIDGE_ORANGE_DURATION);
            foreach (var c in conflicts)
                c.Color = LightColor.Red;
            SendTrafficLightStates();
        }

        private int GetPriority(Direction d)
        {
            int p = 0;
            foreach (var tl in d.TrafficLights)
            {
                bool f = tl.Sensors.Any(s => s.Position == SensorPosition.Front && s.IsActivated);
                bool b = tl.Sensors.Any(s => s.Position == SensorPosition.Back && s.IsActivated);
                p += (f && b) ? 5 : (f || b ? 1 : 0);
            }
            return p;
        }

        private void SendTrafficLightStates()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData)) return;
            try
            {
                var sd = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
                if (sd == null) return;

                var dict = sd.Keys.ToDictionary(
                    id => id,
                    id =>
                    {
                        var tl = directions.SelectMany(d => d.TrafficLights).First(t => t.Id == id);
                        var dir = directions.First(d => d.TrafficLights.Contains(tl));
                        return dir.Color == LightColor.Green ? "groen"
                             : dir.Color == LightColor.Orange ? "oranje"
                             : "rood";
                    });

                dict["81.1"] = CurrentBridgeState;
                communicator.PublishMessage("stoplichten", dict);
                Console.WriteLine($"Verkeerslicht statussen verzonden. Brugstatus: {CurrentBridgeState}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fout bij verzenden verkeerslicht statussen: {ex.Message}");
            }
        }
    }
}
