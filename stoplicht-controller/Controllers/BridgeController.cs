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
    public class BridgeController
    {
        // ───────────────────────────────────────────────────────────────
        //  ► CONFIG CONSTANTS  ─ timings are in milliseconds unless noted
        // ───────────────────────────────────────────────────────────────
        private const int BRIDGE_GREEN_DURATION = 9_000;     // boat pass A/B
        private const int BRIDGE_ORANGE_DURATION = 10_000;
        private const int POST_BRIDGE_NORMAL_PHASE_MS = 30_000;  // traffic free-flow
        private const int BRIDGE_COOLDOWN_SECONDS = 60;         // min gap between sessions
        private const int SAFETY_CHECK_INTERVAL = 1000;          // polling
        private const int BRIDGE_SWITCH_EXTRA_DELAY_MS = 10000; // ship still under bridge

        // ───────────────────────────────────────────────────────────────
        //  ► RUNTIME FIELDS
        // ───────────────────────────────────────────────────────────────
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly Bridge bridge;

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

        private string currentBridgeState = "rood";   // software state of TL 81.1
        private string physicalBridgeState = "dicht"; // sensor feedback

        // ───────────────────────────────────────────────────────────────
        //  ► PUBLIC API
        // ───────────────────────────────────────────────────────────────
        public string CurrentBridgeState => currentBridgeState;

        /// <summary>
        /// Returns all direction IDs that belong to the bridge itself
        /// (71 + 72) plus every direction that intersects with them.
        /// </summary>
        public HashSet<int> GetBridgeIntersectionSet()
        {
            var set = new HashSet<int>();
            void collect(Direction dir)
            {
                if (dir == null) return;
                set.Add(dir.Id);
                foreach (var id in dir.Intersections) set.Add(id);
            }
            collect(directions.FirstOrDefault(d => d.Id == bridgeDirectionA));
            collect(directions.FirstOrDefault(d => d.Id == bridgeDirectionB));
            return set;
        }

        /// <summary>
        /// Emergency boat override:
        /// 1. Immediately set boat approach lights to red.
        /// 2. Wait a short delay for the boat to approach the bridge.
        /// 3. Wait until the bridge is fully closed.
        /// 4. Restore road traffic lights to green.
        /// </summary>
        public async Task EmergencyBoatOverrideAsync(int approachDelayMs = 5000)
        {
            // 1) Immediately stop boat approaches
            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);
            dirA.Color = LightColor.Red;
            dirB.Color = LightColor.Red;
            SendTrafficLightStates();

            // 2) Allow boat time to approach under closed lights
            await Task.Delay(approachDelayMs);

            // 3) Wait for the physical bridge to be closed
            await WaitForPhysicalBridgeState("dicht");

            // 4) Restore car traffic
            MakeCrossingGreen();
        }

        // ───────────────────────────────────────────────────────────────
        //  ► CONSTRUCTOR
        // ───────────────────────────────────────────────────────────────
        public BridgeController(Communicator communicator, List<Direction> directions, Bridge bridge)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.bridge = bridge;
            SetInitialBridgeState();
        }

        /// <summary>
        /// Resets all lights to their safe default: bridge approaches red,
        /// conflicting directions green.
        /// </summary>
        private void SetInitialBridgeState()
        {
            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            currentBridgeState = "rood";
            if (dirA != null) dirA.Color = LightColor.Red;
            if (dirB != null) dirB.Color = LightColor.Red;

            OpenConflicts(dirA);
            OpenConflicts(dirB);

            SendTrafficLightStates();
        }

        private void OpenConflicts(Direction dir)
        {
            if (dir == null) return;
            foreach (var cid in dir.Intersections)
            {
                var c = directions.FirstOrDefault(d => d.Id == cid);
                if (c != null && c.Id != bridgeDirectionA && c.Id != bridgeDirectionB)
                    c.Color = LightColor.Green;
            }
        }

        // ───────────────────────────────────────────────────────────────
        //  ► SENSOR & STATE HELPERS
        // ───────────────────────────────────────────────────────────────
        public void ProcessBridgeSensorData()
        {
            if (string.IsNullOrEmpty(communicator.BridgeSensorData)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(communicator.BridgeSensorData);
                if (data != null
                    && data.TryGetValue("81.1", out var inner)
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
        /// Ends the 30s “normal traffic” phase after a bridge session.
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

        // ───────────────────────────────────────────────────────────────
        //  ► MAIN LOOP ENTRY
        // ───────────────────────────────────────────────────────────────
        public async Task UpdateAsync()
        {
            // 1. Emergency override has top priority
            if (CheckForPriorityVehicle())
            {
                await HandlePriorityVehicleAsync();
                return;
            }

            // 2. Regular maintenance
            ProcessBridgeSensorData();
            ResetBridgeCycle();

            // 3. Guard clauses
            var elapsed = (DateTime.Now - lastBridgeClosedTime).TotalSeconds;
            if (elapsed < BRIDGE_COOLDOWN_SECONDS || bridgeUsedThisCycle || isBridgeCycleRunning)
                return;

            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);
            if (dirA == null || dirB == null) return;

            if (GetPriority(dirA) <= 0 && GetPriority(dirB) <= 0) return;

            // 4. Launch asynchronous bridge session
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
                        SetInitialBridgeState();
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

        public bool CheckForPriorityVehicle()
        {
            if (string.IsNullOrWhiteSpace(communicator.PriorityVehicleData))
                return false;
            try
            {
                var data = JsonConvert.DeserializeObject<PriorityVehicleQueue>(communicator.PriorityVehicleData);
                if (data?.Queue == null || data.Queue.Count == 0) return false;

                var bridgeDirIds = GetBridgeIntersectionSet();
                var laneSet = directions
                    .Where(d => bridgeDirIds.Contains(d.Id))
                    .SelectMany(d => d.TrafficLights)
                    .Select(tl => tl.Id)
                    .ToHashSet(StringComparer.Ordinal);

                return data.Queue.Any(v => v.Priority >= 1 && laneSet.Contains(v.Lane));
            }
            catch { return false; }
        }

        private async Task HandlePriorityVehicleAsync()
        {
            IsHandlingPriority = true;
            Task running = null;
            lock (bridgeLock)
            {
                if (bridgeTask != null && !bridgeTask.IsCompleted)
                {
                    bridgeCts?.Cancel();
                    running = bridgeTask;
                }
            }
            if (running != null)
                await Task.WhenAny(running, Task.Delay(5_000));

            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);
            dirA.Color = LightColor.Red;
            dirB.Color = LightColor.Red;
            SendTrafficLightStates();

            await Task.Delay(5_000);
            await WaitUntilNoVesselUnderBridge();

            currentBridgeState = "rood";
            SendTrafficLightStates();
            await WaitForPhysicalBridgeState("dicht");
            await Task.Delay(2_000);
            MakeCrossingGreen();
            IsHandlingPriority = false;
        }

        private async Task HandleBridgeSession(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);
            bool sideA = GetPriority(dirA) > 0;
            bool sideB = GetPriority(dirB) > 0;
            if (!sideA && !sideB) return;
            await ForceConflictDirectionsToRed(bridgeDirectionA);
            await ForceConflictDirectionsToRed(bridgeDirectionB);
            Console.WriteLine("Waiting until no vehicle on the bridge...");
            await WaitUntilNoBridgeVehicle();
            currentBridgeState = "groen";
            SendTrafficLightStates();
            await WaitForPhysicalBridgeState("open");
            if (sideA)
            {
                await LetBoatsPass(bridgeDirectionA);
                while (bridge.VesselUnderBridge)
                {
                    Console.WriteLine($"Ship still under bridge, extra wait {BRIDGE_SWITCH_EXTRA_DELAY_MS} ms.");
                    await Task.Delay(BRIDGE_SWITCH_EXTRA_DELAY_MS);
                }
            }
            sideB = GetPriority(dirB) > 0 || sideB;
            if (sideB)
                await LetBoatsPass(bridgeDirectionB);
            Console.WriteLine("Waiting until no vessel under bridge...");
            await WaitUntilNoVesselUnderBridge();
            currentBridgeState = "rood";
            SendTrafficLightStates();
            await WaitForPhysicalBridgeState("dicht");
            await Task.Delay(2_000);
            MakeCrossingGreen();
        }

        private async Task WaitUntilNoBridgeVehicle()
        {
            int retries = 0, max = 60;
            while (true)
            {
                ProcessBridgeSensorData();
                if (!bridge.VehicleOnBridge)
                {
                    await Task.Delay(SAFETY_CHECK_INTERVAL);
                    if (!bridge.VehicleOnBridge) break;
                }
                if (++retries >= max) break;
                await Task.Delay(SAFETY_CHECK_INTERVAL);
            }
        }

        private async Task WaitUntilNoVesselUnderBridge()
        {
            int retries = 0, max = 180;
            int clearCount = 0, required = 4;
            while (true)
            {
                ProcessBridgeSensorData();
                if (!bridge.VesselUnderBridge)
                {
                    if (++clearCount >= required) break;
                }
                else clearCount = 0;
                if (++retries >= max) break;
                await Task.Delay(SAFETY_CHECK_INTERVAL);
            }
        }

        private async Task WaitForPhysicalBridgeState(string target)
        {
            int retries = 0, max = 240;
            while (physicalBridgeState != target)
            {
                ProcessBridgeSensorData();
                await Task.Delay(SAFETY_CHECK_INTERVAL);
                if (++retries >= max) break;
            }
        }

        private async Task LetBoatsPass(int dirId)
        {
            var dir = directions.First(d => d.Id == dirId);
            dir.Color = LightColor.Green; SendTrafficLightStates();
            await Task.Delay(BRIDGE_GREEN_DURATION);
            dir.Color = LightColor.Orange; SendTrafficLightStates();
            await Task.Delay(BRIDGE_ORANGE_DURATION + 3_000);
            dir.Color = LightColor.Red; SendTrafficLightStates();
        }

        private void MakeCrossingGreen()
        {
            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);
            OpenConflicts(dirA);
            OpenConflicts(dirB);
            SendTrafficLightStates();
        }

        private async Task ForceConflictDirectionsToRed(int bridgeDirId)
        {
            var conflicts = directions.Where(d => d.Id != bridgeDirId && d.Intersections.Contains(bridgeDirId)).ToList();
            foreach (var d in conflicts.Where(d => d.Color == LightColor.Green)) d.Color = LightColor.Orange;
            SendTrafficLightStates();
            if (conflicts.Any(d => d.Color == LightColor.Orange)) await Task.Delay(BRIDGE_ORANGE_DURATION);
            foreach (var d in conflicts) d.Color = LightColor.Red;
            SendTrafficLightStates();
        }

        private int GetPriority(Direction dir)
        {
            int p = 0;
            foreach (var tl in dir.TrafficLights)
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
                var laneSensors = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
                if (laneSensors == null) return;
                var payload = laneSensors.Keys.ToDictionary(
                    id => id,
                    id =>
                    {
                        var tl = directions.SelectMany(d => d.TrafficLights).First(t => t.Id == id);
                        var dir = directions.First(d => d.TrafficLights.Contains(tl));
                        return dir.Color == LightColor.Green ? "groen"
                             : dir.Color == LightColor.Orange ? "oranje"
                             : "rood";
                    });
                payload["81.1"] = CurrentBridgeState;
                communicator.PublishMessage("stoplichten", payload);
            }
            catch { }
        }
    }
}