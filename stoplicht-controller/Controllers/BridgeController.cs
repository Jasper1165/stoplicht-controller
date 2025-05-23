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
        //  ► EVENT FOR STATE CHANGES
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired whenever SendBridgeStates is invoked, i.e. whenever
        /// a combined state update should be published.
        /// </summary>
        public event Action StateChanged;

        // ───────────────────────────────────────────────────────────────
        //  ► CONFIG CONSTANTS
        // ───────────────────────────────────────────────────────────────

        private const int BRIDGE_GREEN_DURATION = 20_000;       // Boat passage A/B
        private const int BRIDGE_ORANGE_DURATION = 10_000;
        private const int POST_BRIDGE_NORMAL_PHASE_MS = 30_000; // Traffic free-flow after bridge session
        private const int BRIDGE_COOLDOWN_SECONDS = 180_000;    // Minimum gap between sessions
        private const int SAFETY_CHECK_INTERVAL = 1_000;        // Polling interval
        private const int BRIDGE_SWITCH_EXTRA_DELAY_MS = 10_000;// Delay while a vessel is still under

        // ───────────────────────────────────────────────────────────────
        //  ► RUNTIME FIELDS
        // ───────────────────────────────────────────────────────────────

        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly Bridge bridge;
        private HashSet<int> activeConflictDirections = new HashSet<int>();
        private readonly Dictionary<string, string> persistentPayload = new Dictionary<string, string>();

        /// <summary>
        /// Indicates whether a priority vehicle is currently being handled.
        /// </summary>
        public bool IsHandlingPriority { get; private set; }

        private readonly object bridgeLock = new();
        private CancellationTokenSource bridgeCts;
        private Task bridgeTask;
        private bool isBridgeCycleRunning;

        /// <summary>
        /// Identifier for the north-side bridge approach.
        /// </summary>
        public readonly int bridgeDirectionA = 71;

        /// <summary>
        /// Identifier for the south-side bridge approach.
        /// </summary>
        public readonly int bridgeDirectionB = 72;

        private bool bridgeUsedThisCycle;
        private bool postBridgeNormalPhaseActive;
        private DateTime postBridgePhaseStartTime;
        private DateTime lastBridgeClosedTime = DateTime.MinValue;
        private readonly Dictionary<string, string> sharedPayload;
        private string currentBridgeState = "rood";    // software state of TL 81.1
        private string physicalBridgeState = "dicht";  // sensor feedback

        /// <summary>
        /// Traffic light ID for the bridge (hardcoded).
        /// </summary>
        public const string BRIDGE_LIGHT_ID = "81.1";

        // ───────────────────────────────────────────────────────────────
        //  ► PUBLIC API
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets the current software state of the bridge traffic light.
        /// </summary>
        public string CurrentBridgeState => currentBridgeState;

        /// <summary>
        /// Retrieves a set containing all direction IDs associated with the bridge
        /// (both bridge approaches and their intersecting directions).
        /// </summary>
        public HashSet<int> GetBridgeIntersectionSet()
        {
            var set = new HashSet<int>();
            void collect(Direction dir)
            {
                if (dir == null) return;
                set.Add(dir.Id);
                foreach (var id in dir.Intersections)
                    set.Add(id);
            }
            collect(directions.FirstOrDefault(d => d.Id == bridgeDirectionA));
            collect(directions.FirstOrDefault(d => d.Id == bridgeDirectionB));
            return set;
        }

        /// <summary>
        /// Emergency override for a vessel: immediately set both bridge
        /// approaches to red, wait for the physical bridge to close,
        /// then allow crossing traffic to go.
        /// </summary>
        /// <param name="approachDelayMs">Delay before checking physical closure.</param>
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

        /// <summary>
        /// Constructor: initializes the bridge controller and sets
        /// the initial bridge and crossing light states.
        /// </summary>
        public BridgeController(
            Communicator communicator,
            List<Direction> directions,
            Bridge bridge,
            Dictionary<string, string> combinedPayload)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.bridge = bridge;
            this.sharedPayload = combinedPayload;
            SetInitialBridgeState();
        }

        /// <summary>
        /// Sets the bridge to its starting state (red) and allows
        /// crossing traffic to flow (green).
        /// </summary>
        private void SetInitialBridgeState()
        {
            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            currentBridgeState = "rood";
            if (dirA != null) dirA.Color = LightColor.Red;
            if (dirB != null) dirB.Color = LightColor.Red;

            ChangeCrossingTrafficLights(LightColor.Green);
        }

        /// <summary>
        /// Processes incoming sensor data from the bridge to update the
        /// physicalBridgeState field.
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bridge sensor parse error: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the bridge cycle flag once the post-bridge normal
        /// flow phase has completed.
        /// </summary>
        public void ResetBridgeCycle()
        {
            if (postBridgeNormalPhaseActive
                && (DateTime.Now - postBridgePhaseStartTime).TotalMilliseconds >= POST_BRIDGE_NORMAL_PHASE_MS)
            {
                bridgeUsedThisCycle = false;
                postBridgeNormalPhaseActive = false;
                StateChanged?.Invoke();
            }
        }

        /// <summary>
        /// Main update loop: first handles priority vehicles, otherwise
        /// potentially starts a new bridge session.
        /// </summary>
        public async Task UpdateAsync()
        {
            if (CheckForPriorityVehicle())
            {
                await HandlePriorityVehicleAsync(bridgeCts?.Token ?? CancellationToken.None);
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
            }

            try
            {
                await HandleBridgeSession(bridgeCts.Token);
            }
            catch (OperationCanceledException)
            {
                /* skip */
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bridge session error: {ex.Message}");
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
        }

        /// <summary>
        /// Checks if there are any emergency (priority-1) vehicles waiting.
        /// </summary>
        public bool CheckForPriorityVehicle()
        {
            if (string.IsNullOrWhiteSpace(communicator.PriorityVehicleData))
                return false;
            try
            {
                var data = JsonConvert.DeserializeObject<PriorityVehicleQueue>(communicator.PriorityVehicleData);
                return data?.Queue?.Any(v => v.Priority == 1) ?? false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Handles a priority-1 vehicle session, similar to a full
        /// bridge session but shorter and interrupt-driven.
        /// </summary>
        private async Task HandlePriorityVehicleAsync(CancellationToken token)
        {
            IsHandlingPriority = true;
            try
            {
                ProcessBridgeSensorData();
                bool bridgeIsClosed = physicalBridgeState == "dicht";

                var dirA = directions.First(d => d.Id == bridgeDirectionA);
                var dirB = directions.First(d => d.Id == bridgeDirectionB);
                dirA.Color = LightColor.Red;
                dirB.Color = LightColor.Red;
                SendBridgeStates();

                if (bridgeIsClosed)
                {
                    currentBridgeState = "rood";
                    SendBridgeStates();

                    await Task.Delay(6_000, token);
                    await Task.Delay(6_000, token);
                    ChangeCrossingTrafficLights(LightColor.Green);
                }
                else
                {
                    dirA.Color = LightColor.Red;
                    dirB.Color = LightColor.Red;
                    SendBridgeStates();

                    await WaitUntilNoVesselUnderBridge(token);
                    currentBridgeState = "rood";
                    SendBridgeStates();

                    await WaitForPhysicalBridgeState("dicht", token);
                    await Task.Delay(6_000, token);
                    ChangeCrossingTrafficLights(LightColor.Green);
                }

                await Task.Delay(10_000, token);
            }
            finally
            {
                IsHandlingPriority = false;
            }
        }

        /// <summary>
        /// Runs a full bridge session cycle: red→green→orange→red,
        /// allows vessels across, then reopens crossing traffic.
        /// </summary>
        private async Task HandleBridgeSession(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var dirA = directions.First(d => d.Id == bridgeDirectionA);
            var dirB = directions.First(d => d.Id == bridgeDirectionB);
            bool sideA = GetPriority(dirA) > 0;
            bool sideB = GetPriority(dirB) > 0;
            if (!sideA && !sideB) return;

            ChangeCrossingTrafficLights(LightColor.Red);
            await Task.Delay(6_000, token);
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
                    await Task.Delay(BRIDGE_SWITCH_EXTRA_DELAY_MS, token);
                }
            }

            sideB = GetPriority(dirB) > 0 || sideB;
            if (sideB)
                await LetBoatsPass(bridgeDirectionB, token);

            await WaitUntilNoVesselUnderBridge(token);

            activeConflictDirections.Clear();
            currentBridgeState = "rood";
            SendBridgeStates();

            await WaitForPhysicalBridgeState("dicht", token);
            await Task.Delay(2_000, token);

            ChangeCrossingTrafficLights(LightColor.Green);
        }

        // ───────────────────────────────────────────────────────────────
        //  ► HELPER METHODS
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Waits until no vehicle is detected on the bridge.
        /// </summary>
        private async Task WaitUntilNoBridgeVehicle(CancellationToken token)
        {
            int retries = 0, max = 60;
            while (bridge.VehicleOnBridge && retries++ < max)
                await Task.Delay(SAFETY_CHECK_INTERVAL, token);
        }

        /// <summary>
        /// Waits until no vessel remains under the bridge for at least
        /// the required number of consecutive checks.
        /// </summary>
        private async Task WaitUntilNoVesselUnderBridge(CancellationToken token)
        {
            int retries = 0, max = 180, clearCount = 0, required = 4;
            while (retries++ < max)
            {
                if (!bridge.VesselUnderBridge && ++clearCount >= required)
                    break;
                if (bridge.VesselUnderBridge)
                    clearCount = 0;
                await Task.Delay(SAFETY_CHECK_INTERVAL, token);
            }
        }

        /// <summary>
        /// Waits for the physical bridge sensor to report the given state.
        /// </summary>
        /// <param name="target">"open" or "dicht"</param>
        private async Task WaitForPhysicalBridgeState(string target, CancellationToken token)
        {
            int retries = 0, max = 240;
            while (physicalBridgeState != target && retries++ < max)
                await Task.Delay(SAFETY_CHECK_INTERVAL, token);
        }

        /// <summary>
        /// Opens the specified bridge approach to allow vessels to pass:
        /// green for a fixed duration, then orange, then red.
        /// </summary>
        /// <param name="dirId">ID of the bridge side to open.</param>
        private async Task LetBoatsPass(int dirId, CancellationToken token)
        {
            var dir = directions.First(d => d.Id == dirId);
            dir.Color = LightColor.Green;
            SendBridgeStates();
            await Task.Delay(BRIDGE_GREEN_DURATION, token);

            dir.Color = LightColor.Orange;
            SendBridgeStates();
            await Task.Delay(BRIDGE_ORANGE_DURATION + 3_000, token);

            dir.Color = LightColor.Red;
            SendBridgeStates();
        }

        /// <summary>
        /// Computes the total priority score for a direction based on
        /// its sensors (front/back activations).
        /// </summary>
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

        /// <summary>
        /// Publishes all current light states (bridge + conflicts)
        /// into the shared payload and fires StateChanged.
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
                    sharedPayload[tl.Id] = color;
            }

            foreach (var cid in activeConflictDirections)
            {
                var conflictColor = currentBridgeState == "rood" ? "groen" : "rood";
                sharedPayload[$"{cid}.1"] = conflictColor;
            }

            sharedPayload[BRIDGE_LIGHT_ID] = currentBridgeState;
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Sets all crossing traffic lights (conflict directions)
        /// to the specified color and fires StateChanged.
        /// </summary>
        private void ChangeCrossingTrafficLights(LightColor color)
        {
            Console.WriteLine($"Changing crossing traffic lights to {color}");
            var conflicts = directions
                .Where(d => d.Id != bridgeDirectionA && d.Intersections.Contains(bridgeDirectionA))
                .ToList();

            activeConflictDirections.Clear();
            foreach (var d in conflicts)
            {
                if (d.Id == bridgeDirectionA || d.Id == bridgeDirectionB) continue;
                d.Color = color;
                activeConflictDirections.Add(d.Id);
            }

            StateChanged?.Invoke();
        }
    }
}
