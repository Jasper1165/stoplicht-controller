using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

namespace stoplicht_controller.Managers
{
    public class BridgeController
    {
        // ───────────────────────────────────────────────────────────────
        //  ► CONFIG CONSTANTS  ─ timings are in milliseconds unless noted
        // ───────────────────────────────────────────────────────────────
        private const int BRIDGE_GREEN_DURATION = 20_000;     // boot pass A/B
        private const int BRIDGE_ORANGE_DURATION = 10_000;
        private const int POST_BRIDGE_NORMAL_PHASE_MS = 30_000;  // traffic free-flow
        private const int BRIDGE_COOLDOWN_SECONDS = 60;         // min gap between sessions
        private const int SAFETY_CHECK_INTERVAL = 1000;         // polling
        private const int BRIDGE_SWITCH_EXTRA_DELAY_MS = 10000; // ship still under bridge

        // ───────────────────────────────────────────────────────────────
        //  ► RUNTIME FIELDS
        // ───────────────────────────────────────────────────────────────
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly Bridge bridge;
        private HashSet<int> activeConflictDirections = new HashSet<int>();
        private readonly Dictionary<string, string> persistentPayload = new Dictionary<string, string>();
        public bool IsHandlingPriority { get; private set; }
        private readonly object bridgeLock = new();
        private CancellationTokenSource bridgeCts;
        private Task bridgeTask;
        private bool isBridgeCycleRunning;

        private readonly int bridgeDirectionA = 71;   // north side
        private readonly int bridgeDirectionB = 72;   // south side

        private bool bridgeUsedThisCycle;
        private bool postBridgeNormalPhaseActive;
        private DateTime postBridgePhaseStartTime;
        private DateTime lastBridgeClosedTime = DateTime.MinValue;
        private readonly Dictionary<string, string> sharedPayload;
        private string currentBridgeState = "rood";    // software state of TL 81.1
        private string physicalBridgeState = "dicht";  // sensor feedback

        // Bridge traffic light ID (hardcoded)
        private const string BRIDGE_LIGHT_ID = "81.1";

        // ───────────────────────────────────────────────────────────────
        //  ► PUBLIC API
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets the current software state of the bridge traffic light.
        /// </summary>
        public string CurrentBridgeState => currentBridgeState;

        /// <summary>
        /// Retrieves a set of all direction IDs related to the bridge (both bridge directions and their intersections).
        /// </summary>
        /// <returns>A HashSet containing the bridge direction IDs and their intersection IDs.</returns>
        public HashSet<int> GetBridgeIntersectionSet()
        {
            var set = new HashSet<int>();
            void collect(Direction dir)
            {
                if (dir == null) return;
                set.Add(dir.Id);                                  // Add the bridge direction ID
                foreach (var id in dir.Intersections)
                    set.Add(id);                                  // Add each intersection ID
            }
            collect(directions.FirstOrDefault(d => d.Id == bridgeDirectionA));
            collect(directions.FirstOrDefault(d => d.Id == bridgeDirectionB));
            return set;
        }

        /// <summary>
        /// Forces an immediate traffic override to let a boat pass: sets both bridge directions to red, waits for physical closure, then opens crossings.
        /// </summary>
        /// <param name="approachDelayMs">Delay before enforcing closure, in milliseconds.</param>
        /// <returns>A Task representing the asynchronous override operation.</returns>
        public async Task EmergencyBoatOverrideAsync(int approachDelayMs = 5000)
        {
            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);
            dirA.Color = LightColor.Red;
            dirB.Color = LightColor.Red;
            SendBridgeStates();

            await Task.Delay(approachDelayMs);
            await WaitForPhysicalBridgeState("dicht", CancellationToken.None);
            ChangeCrossingTrafficLights(LightColor.Green);
        }

        // ───────────────────────────────────────────────────────────────
        //  ► CONSTRUCTOR
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of the BridgeController class with dependencies and initial state.
        /// </summary>
        /// <param name="communicator">Interface for sensor and actuator communication.</param>
        /// <param name="directions">List of traffic directions to manage.</param>
        /// <param name="bridge">Bridge sensor interface.</param>
        /// <param name="CombinedPayload">Shared payload dictionary for output states.</param>
        public BridgeController(Communicator communicator, List<Direction> directions, Bridge bridge, Dictionary<string, string> CombinedPayload)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.bridge = bridge;
            this.sharedPayload = CombinedPayload;
            SetInitialBridgeState();
        }

        /// <summary>
        /// Sets the bridge and its associated traffic lights to their initial states (bridge red, crossings green).
        /// </summary>
        private void SetInitialBridgeState()
        {
            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            // Always set the bridge state to red
            currentBridgeState = "rood";

            // Set bridge direction signals to red if they exist
            if (dirA != null) dirA.Color = LightColor.Red;
            if (dirB != null) dirB.Color = LightColor.Red;

            // If directions exist, open their conflicts
            OpenConflicts(dirA);
            OpenConflicts(dirB);

            // Ensure cross traffic flows
            ChangeCrossingTrafficLights(LightColor.Green);
        }

        /// <summary>
        /// Opens all conflicting directions for a given bridge direction by setting them to green.
        /// </summary>
        /// <param name="dir">The bridge direction for which to open conflicts.</param>
        private void OpenConflicts(Direction dir)
        {
            if (dir == null) return;
            foreach (var cid in dir.Intersections)
            {
                var c = directions.FirstOrDefault(d => d.Id == cid);
                if (c != null && c.Id != bridgeDirectionA && c.Id != bridgeDirectionB)
                    c.Color = LightColor.Green;                  // Set conflict direction light to green
            }
        }

        /// <summary>
        /// Parses and processes incoming bridge sensor data to update the physical bridge state.
        /// </summary>
        public void ProcessBridgeSensorData()
        {
            if (string.IsNullOrEmpty(communicator.BridgeSensorData)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(communicator.BridgeSensorData);
                if (data != null
                    && data.TryGetValue(BRIDGE_LIGHT_ID, out var inner)
                    && inner.TryGetValue("state", out var state)
                    && (state == "open" || state == "dicht")
                    && physicalBridgeState != state)
                {
                    physicalBridgeState = state;
                    Console.WriteLine($"Physical bridge state updated → {state}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bridge sensor parse error: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the bridge cycle if the post-bridge normal traffic phase has completed.
        /// </summary>
        public void ResetBridgeCycle()
        {
            if (postBridgeNormalPhaseActive
                && (DateTime.Now - postBridgePhaseStartTime).TotalMilliseconds >= POST_BRIDGE_NORMAL_PHASE_MS)
            {
                Console.WriteLine("Normal phase finished, bridge cycle reset.");
                bridgeUsedThisCycle = false;
                postBridgeNormalPhaseActive = false;
            }
        }

        /// <summary>
        /// Main update loop: checks for priority vehicles first, otherwise triggers a new bridge cycle if conditions are met.
        /// </summary>
        public async Task UpdateAsync()
        {
            if (CheckForPriorityVehicle())
            {
                var priorityToken = bridgeCts?.Token ?? CancellationToken.None;
                await HandlePriorityVehicleAsync(priorityToken);
                return;
            }

            ProcessBridgeSensorData();
            ResetBridgeCycle();

            var elapsed = (DateTime.Now - lastBridgeClosedTime).TotalSeconds;
            if (elapsed < BRIDGE_COOLDOWN_SECONDS || bridgeUsedThisCycle || isBridgeCycleRunning)
                return;

            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);
            if (dirA == null || dirB == null) return;
            if (GetPriority(dirA) <= 0 && GetPriority(dirB) <= 0) return;

            lock (bridgeLock)
            {
                if (isBridgeCycleRunning) return;
                isBridgeCycleRunning = true;
                bridgeCts = new CancellationTokenSource();
                bridgeTask = Task.Run(async () =>
                {
                    bool cancelled = false;
                    try
                    {
                        await HandleBridgeSession(bridgeCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        Console.WriteLine("Bridge session cancelled.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Bridge session error: {ex.Message}");
                        SetInitialBridgeState();
                    }
                    finally
                    {
                        isBridgeCycleRunning = false;
                        if (!cancelled)
                        {
                            bridgeUsedThisCycle = true;
                            postBridgeNormalPhaseActive = true;
                            postBridgePhaseStartTime = DateTime.Now;
                            lastBridgeClosedTime = DateTime.Now;
                        }
                    }
                }, bridgeCts.Token);
            }
        }

        /// <summary>
        /// Checks if a priority vehicle (priority == 1) is waiting to cross the bridge.
        /// </summary>
        /// <returns>True if a priority-one vehicle is queued; otherwise false.</returns>
        public bool CheckForPriorityVehicle()
        {
            if (string.IsNullOrWhiteSpace(communicator.PriorityVehicleData))
                return false;
            try
            {
                var data = JsonConvert.DeserializeObject<PriorityVehicleQueue>(communicator.PriorityVehicleData);
                if (data?.Queue == null || data.Queue.Count == 0) return false;

                return data.Queue.Any(v => v.Priority == 1);
            }
            catch { return false; }
        }

        /// <summary>
        /// Handles the bridge and traffic lights sequence to let a priority vehicle pass safely.
        /// </summary>
        /// <param name="token">CancellationToken to abort if needed.</param>
        private async Task HandlePriorityVehicleAsync(CancellationToken token)
        {
            IsHandlingPriority = true;
            Task running = null;

            try
            {
                if (running != null)
                    await Task.WhenAny(running, Task.Delay(5_000));

                ProcessBridgeSensorData();
                bool bridgeIsClosed = physicalBridgeState == "dicht";

                var dirA = directions.First(d => d.Id == bridgeDirectionA);
                var dirB = directions.First(d => d.Id == bridgeDirectionB);
                dirA.Color = LightColor.Red;
                dirB.Color = LightColor.Red;
                SendBridgeStates();

                if (bridgeIsClosed)
                {
                    Console.WriteLine("Prioriteitsvoertuig verwerken met gesloten brug");
                    activeConflictDirections.Clear();               // Clear active conflict directions

                    currentBridgeState = "rood";
                    SendBridgeStates();

                    await Task.Delay(6_000, token);
                    Console.WriteLine("Slagbomen openen voor prioriteitsvoertuig...");
                    await Task.Delay(6_000, token);

                    Console.WriteLine("Verkeerslichten op groen zetten voor prioriteitsvoertuig");
                    ChangeCrossingTrafficLights(LightColor.Green);

                    isBridgeCycleRunning = false;
                    bridgeUsedThisCycle = false;
                    postBridgeNormalPhaseActive = false;
                    postBridgePhaseStartTime = DateTime.Now.AddSeconds(-50);
                    lastBridgeClosedTime = DateTime.Now.AddSeconds(-50);
                }
                else
                {
                    Console.WriteLine("Prioriteitsvoertuig verwerken met open brug");
                    Console.WriteLine("Wachten tot er geen schepen meer onder de brug zijn...");
                    await WaitUntilNoVesselUnderBridge(token);

                    activeConflictDirections.Clear();               // Clear active conflict directions

                    currentBridgeState = "rood";
                    SendBridgeStates();

                    Console.WriteLine("Wachten tot de brug fysiek gesloten is...");
                    await WaitForPhysicalBridgeState("dicht", token);

                    Console.WriteLine("Slagbomen openen voor prioriteitsvoertuig...");
                    await Task.Delay(6_000, token);

                    Console.WriteLine("Verkeerslichten op groen zetten voor prioriteitsvoertuig");
                    ChangeCrossingTrafficLights(LightColor.Green);

                    isBridgeCycleRunning = false;
                    bridgeUsedThisCycle = false;
                    postBridgeNormalPhaseActive = false;
                    postBridgePhaseStartTime = DateTime.Now.AddSeconds(-50);
                    lastBridgeClosedTime = DateTime.Now.AddSeconds(-50);
                }

                await Task.Delay(10_000, token);
            }
            finally
            {
                IsHandlingPriority = false;
            }
        }

        /// <summary>
        /// Executes a full bridge opening session when traffic priority is detected.
        /// </summary>
        /// <param name="token">CancellationToken to abort if needed.</param>
        private async Task HandleBridgeSession(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);
            bool sideA = GetPriority(dirA) > 0;
            bool sideB = GetPriority(dirB) > 0;
            if (!sideA && !sideB) return;

            ChangeCrossingTrafficLights(LightColor.Red);        // Set all crossings to red

            await Task.Delay(6_000, token);

            Console.WriteLine("Waiting until no vehicle on the bridge...");
            await WaitUntilNoBridgeVehicle(token);

            await Task.Delay(6_000, token);

            currentBridgeState = "groen";
            SendBridgeStates();

            await WaitForPhysicalBridgeState("open", token);

            if (sideA)
            {
                await LetBoatsPass(bridgeDirectionA, token);
                while (bridge.VesselUnderBridge)
                {
                    token.ThrowIfCancellationRequested();
                    Console.WriteLine($"Ship still under bridge, extra wait {BRIDGE_SWITCH_EXTRA_DELAY_MS} ms.");
                    await Task.Delay(BRIDGE_SWITCH_EXTRA_DELAY_MS, token);
                }
            }

            sideB = GetPriority(dirB) > 0 || sideB;
            if (sideB)
                await LetBoatsPass(bridgeDirectionB, token);

            Console.WriteLine("Waiting until no vessel under bridge...");
            await WaitUntilNoVesselUnderBridge(token);

            activeConflictDirections.Clear();                   // Clear active conflict directions

            currentBridgeState = "rood";
            SendBridgeStates();

            await WaitForPhysicalBridgeState("dicht", token);

            await Task.Delay(5_000, token);

            ChangeCrossingTrafficLights(LightColor.Green);      // Restore road traffic
        }

        /// <summary>
        /// Waits until no vehicles are detected on the bridge, up to a retry limit.
        /// </summary>
        /// <param name="token">CancellationToken to abort if needed.</param>
        private async Task WaitUntilNoBridgeVehicle(CancellationToken token)
        {
            int retries = 0, max = 60;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ProcessBridgeSensorData();
                Console.WriteLine(bridge.VehicleOnBridge);
                if (!bridge.VehicleOnBridge)
                {
                    await Task.Delay(SAFETY_CHECK_INTERVAL, token);
                    if (!bridge.VehicleOnBridge) break;
                }
                if (++retries >= max) break;
                await Task.Delay(SAFETY_CHECK_INTERVAL, token);
            }
        }

        /// <summary>
        /// Waits until no vessels are detected under the bridge for a sustained period or retry limit.
        /// </summary>
        /// <param name="token">CancellationToken to abort if needed.</param>
        private async Task WaitUntilNoVesselUnderBridge(CancellationToken token)
        {
            int retries = 0, max = 180;
            int clearCount = 0, required = 4;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ProcessBridgeSensorData();
                if (!bridge.VesselUnderBridge)
                {
                    if (++clearCount >= required) break;
                }
                else clearCount = 0;
                if (++retries >= max) break;
                await Task.Delay(SAFETY_CHECK_INTERVAL, token);
            }
        }

        /// <summary>
        /// Waits until the physical bridge sensor reports the specified target state or retry limit is reached.
        /// </summary>
        /// <param name="target">Desired state ("open" or "dicht").</param>
        /// <param name="token">CancellationToken to abort if needed.</param>
        private async Task WaitForPhysicalBridgeState(string target, CancellationToken token)
        {
            int retries = 0, max = 240;
            while (physicalBridgeState != target)
            {
                token.ThrowIfCancellationRequested();
                ProcessBridgeSensorData();
                await Task.Delay(SAFETY_CHECK_INTERVAL, token);
                if (++retries >= max) break;
            }
        }

        /// <summary>
        /// Manages the bridge green/orange/red sequence to allow boats to pass in one direction.
        /// </summary>
        /// <param name="dirId">The direction ID on which boats are passing.</param>
        /// <param name="token">CancellationToken to abort if needed.</param>
        private async Task LetBoatsPass(int dirId, CancellationToken token)
        {
            var dir = directions.First(d => d.Id == dirId);

            dir.Color = LightColor.Green; SendBridgeStates(); // Open bridge for boat passage
            await Task.Delay(BRIDGE_GREEN_DURATION, token);    // Keep green for boats

            dir.Color = LightColor.Orange; SendBridgeStates(); // Warn that passage will end
            await Task.Delay(BRIDGE_ORANGE_DURATION + 3_000, token); // Orange + buffer

            dir.Color = LightColor.Red; SendBridgeStates();   // Close passage
        }

        /// <summary>
        /// Calculates the priority score for a given direction based on front/back sensor activations.
        /// </summary>
        /// <param name="dir">The traffic direction to evaluate.</param>
        /// <returns>Priority score: 5 if both sensors active, 1 if one sensor, 0 if none.</returns>
        private int GetPriority(Direction dir)
        {
            int p = 0;
            foreach (var tl in dir.TrafficLights)
            {
                bool f = tl.Sensors.Any(s => s.Position == SensorPosition.Front && s.IsActivated);
                bool b = tl.Sensors.Any(s => s.Position == SensorPosition.Back && s.IsActivated);
                p += (f && b) ? 5 : (f || b ? 1 : 0);            // Compute sensor-based priority score
            }
            return p;
        }

        /// <summary>
        /// Updates the shared payload dictionary with current states of all traffic lights and the bridge light.
        /// </summary>
        public void SendBridgeStates()
        {
            foreach (var dir in directions)
            {
                if (dir.TrafficLights == null) continue;
                var color = dir.Color == LightColor.Green ? "groen"
                          : dir.Color == LightColor.Orange ? "oranje"
                          : "rood";
                foreach (var tl in dir.TrafficLights)
                    sharedPayload[tl.Id] = color;             // Update shared payload for each traffic light
            }
            foreach (var cid in activeConflictDirections)
            {
                var conflictColor = currentBridgeState == "rood" ? "groen" : "rood";
                sharedPayload[$"{cid}.1"] = conflictColor;      // Update payload for conflict directions
            }
            sharedPayload[BRIDGE_LIGHT_ID] = currentBridgeState; // Set bridge light state in payload

            Console.WriteLine($"Bridge state: {currentBridgeState}");
        }

        /// <summary>
        /// Changes all crossing traffic lights conflicting with the bridge to the specified color and tracks them.
        /// </summary>
        /// <param name="color">The LightColor to apply to crossing directions.</param>
        private void ChangeCrossingTrafficLights(LightColor color)
        {
            Console.WriteLine($"Changing crossing traffic lights to {color}");
            var conflicts = directions
                .Where(d => d.Id != bridgeDirectionA && d.Intersections.Contains(bridgeDirectionA))
                .ToList();

            activeConflictDirections.Clear();                   // Clear previous conflict tracking

            foreach (var d in conflicts)
            {
                if (d.Id == bridgeDirectionA || d.Id == bridgeDirectionB) continue;

                d.Color = color;
                activeConflictDirections.Add(d.Id);             // Track this direction as an active conflict
            }
            SendBridgeStates();
        }
    }
}
