using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;
using stoplicht_controller.Managers;

namespace stoplicht_controller.Managers
{
    public class TrafficLightController
    {
        // ========= CONFIG =========
        private const int ORANGE_DURATION = 5_000;         // Duration to show orange light before switching (ms)
        private const int DEFAULT_GREEN_DURATION = 8_000;  // Base green light duration when no priority adjustments (ms)
        private const int SHORT_GREEN_DURATION = 8_000;    // Minimum green light duration for low priority traffic (ms)
        private const int PRIORITY_THRESHOLD = 3;          // Sum of priorities to qualify for DEFAULT_GREEN_DURATION
        private const int HIGH_PRIORITY_THRESHOLD = 6;     // Sum of priorities to add extra green time
        private const double AGING_SCALE_SECONDS = 5;      // Scaling factor for aging-based priority increment

        // Threshold for traffic jam detection (ms)
        private const int JAM_THRESHOLD_MS = 10_000;
        private bool jamEngaged = false;
        private DateTime? jamDetectedAt = null;
        private DateTime? jamClearedAt = null;

        private static readonly HashSet<int> JAM_BLOCK_DIRECTIONS = new() { 8, 12, 4 };

        // ========= STATE =========
        private DateTime lastSwitchTime = DateTime.Now;    // Last time lights switched to green
        private DateTime lastOrangeTime = DateTime.Now;    // Last time lights switched to orange

        private List<Direction> currentGreenDirections = new();   // Directions currently green
        private List<Direction> currentOrangeDirections = new();  // Directions currently orange
        private static readonly Dictionary<int, DateTime> lastGreenTimes = new();  // Last green timestamp per direction

        private readonly Communicator communicator;
        public readonly List<Direction> directions;
        public readonly BridgeController bridgeController;
        private readonly Bridge bridge;

        private PriorityVehicleManager? priorityManager;
        private bool isOverrideActive = false;

        // Event to notify external subscribers when state changes
        public event Action? StateChanged;
        public readonly Dictionary<string, string> CombinedPayload = new();

        /// <summary>
        /// Initializes a new instance of the TrafficLightController class.
        /// Sets up communicator, directions, bridge controller, and priority manager.
        /// </summary>
        public TrafficLightController(
            Communicator communicator,
            List<Direction> directions,
            Bridge bridge,
            PriorityVehicleManager? priorityManager = null)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.bridge = bridge;
            this.priorityManager = priorityManager;

            InitializeLastGreenTimes(directions);

            bridgeController = new BridgeController(communicator, directions, bridge, CombinedPayload);
            bridgeController.StateChanged += () => StateChanged?.Invoke();
        }

        /// <summary>
        /// Exposes the set of directions protected by the bridge.
        /// </summary>
        public HashSet<int> ProtectedBridgeCluster => GetProtectedBridgeCluster();

        /// <summary>
        /// Assigns a PriorityVehicleManager to override default priority behavior.
        /// </summary>
        public void SetPriorityManager(PriorityVehicleManager mgr) => priorityManager = mgr;

        /// <summary>
        /// Initializes the dictionary that tracks when each direction last turned green.
        /// </summary>
        public static void InitializeLastGreenTimes(IEnumerable<Direction> dirs)
        {
            foreach (var d in dirs)
                lastGreenTimes[d.Id] = DateTime.Now;
        }

        /// <summary>
        /// Main asynchronous loop controlling the traffic lights.
        /// Launches background loops and periodically processes sensor data and updates lights.
        /// </summary>
        public async Task TrafficLightCycleLoop(CancellationToken token)
        {
            // Start background tasks for bridge and priority handling
            _ = Task.Run(BridgeLoop, token);
            _ = Task.Run(PriorityLoop, token);

            Console.WriteLine("Traffic Light Controller started");
            if (directions.Any())
            {
                Console.WriteLine($"Initial directions count: {directions.Count}");
                SwitchTrafficLights();
                SendTrafficLightStates();
            }

            // Continue updating until cancellation is requested
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ProcessSensorMessage();   // Update sensor activations
                    UpdateTrafficLights();    // Evaluate and change light states
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] TrafficLight update failure: {ex}");
                }

                try
                {
                    await Task.Delay(500, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            Console.WriteLine("Traffic Light Controller stopped");
        }

        /// <summary>
        /// Background loop handling bridge sensor readings and state updates.
        /// Runs indefinitely with a fixed delay between iterations.
        /// </summary>
        private async Task BridgeLoop()
        {
            while (true)
            {
                bridgeController.ProcessBridgeSensorData();
                try
                {
                    await bridgeController.UpdateAsync();  // Perform async bridge state update
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Bridge update error: {ex}");
                }
                await Task.Delay(500);
            }
        }

        /// <summary>
        /// Background loop handling priority vehicle manager updates.
        /// Runs indefinitely to adjust internal priority state.
        /// </summary>
        private async Task PriorityLoop()
        {
            while (true)
            {
                try
                {
                    priorityManager?.Update();  // Synchronous update of priority manager
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Priority manager error: {ex}");
                }
                await Task.Delay(500);
            }
        }

        /// <summary>
        /// Evaluates current traffic conditions and decides when to switch lights.
        /// Handles jam detection, orange‐to‐red transitions, green extensions, and jam‐based overrides.
        /// </summary>
        private void UpdateTrafficLights()
        {
            var now = DateTime.Now;

            // 1) Threshold-based jam detection using bridge sensor data
            bool sensorJam = bridge.TrafficJamNearBridge;
            if (sensorJam)
            {
                // Reset clear timestamp and update detection timestamp
                jamClearedAt = null;
                if (!jamDetectedAt.HasValue)
                    jamDetectedAt = now;
                else if (!jamEngaged
                         && (now - jamDetectedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
                {
                    // Engage jam mode after threshold duration
                    jamEngaged = true;
                    jamDetectedAt = null;
                    Console.WriteLine(">>> Jam engaged after threshold");
                }
            }
            else
            {
                // Reset detection timestamp and update clear timestamp
                jamDetectedAt = null;
                if (!jamClearedAt.HasValue)
                    jamClearedAt = now;
                else if (jamEngaged
                         && (now - jamClearedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
                {
                    // Clear jam mode after sustained clear period
                    jamEngaged = false;
                    jamClearedAt = null;
                    Console.WriteLine(">>> Jam cleared after threshold");
                }
            }

            var protectedDirections = GetProtectedBridgeCluster();
            var sinceG = (now - lastSwitchTime).TotalMilliseconds;
            var sinceO = (now - lastOrangeTime).TotalMilliseconds;

            // Handle orange phase completion
            if (currentOrangeDirections.Any())
            {
                if (sinceO >= ORANGE_DURATION)
                {
                    // Turn all orange lights to red, then switch to new green phase
                    foreach (var orangeDir in currentOrangeDirections.ToList())
                    {
                        orangeDir.Color = LightColor.Red;
                        currentOrangeDirections.Remove(orangeDir);
                    }
                    SwitchTrafficLights();
                    SendTrafficLightStates();
                }
                return;
            }

            // Handle active green phase
            if (currentGreenDirections.Any())
            {
                // Determine green duration based on priority sum
                int sumP = currentGreenDirections.Sum(GetEffectivePriority);
                int dur = sumP >= HIGH_PRIORITY_THRESHOLD
                    ? DEFAULT_GREEN_DURATION + 2000
                    : sumP < PRIORITY_THRESHOLD
                        ? SHORT_GREEN_DURATION
                        : DEFAULT_GREEN_DURATION;

                if (sinceG >= dur)
                {
                    // Transition from green to orange after duration
                    foreach (var greenDir in currentGreenDirections.ToList())
                    {
                        greenDir.Color = LightColor.Orange;
                        currentOrangeDirections.Add(greenDir);
                        currentGreenDirections.Remove(greenDir);
                    }
                    lastOrangeTime = now;
                    SendTrafficLightStates();
                }
                else
                {
                    // Attempt to add extra green candidates if possible
                    var extra = GetExtraGreenCandidates();
                    if (extra.Any())
                    {
                        foreach (var e in extra)
                        {
                            e.Color = LightColor.Green;
                            currentGreenDirections.Add(e);
                            lastGreenTimes[e.Id] = now;
                        }
                        lastSwitchTime = now;
                        SendTrafficLightStates();
                    }

                    // If jam is engaged, force conflicting directions to orange
                    if (jamEngaged)
                    {
                        bool statesChanged = false;
                        foreach (var d in currentGreenDirections.ToList())
                        {
                            if (JAM_BLOCK_DIRECTIONS.Contains(d.Id))
                            {
                                d.Color = LightColor.Orange;
                                currentOrangeDirections.Add(d);
                                currentGreenDirections.Remove(d);
                                statesChanged = true;
                            }
                        }
                        if (statesChanged)
                        {
                            lastOrangeTime = now;
                            SendTrafficLightStates();
                        }
                    }
                }
                return;
            }

            // No current green: start new cycle
            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        /// <summary>
        /// Overrides normal cycle to set a single specified direction to green immediately.
        /// Other conflicting directions are transitioned through orange then red.
        /// </summary>
        public async Task OverrideWithSingleGreen(int dirId)
        {
            var protect = GetProtectedBridgeCluster();
            if (protect.Contains(dirId)) return;  // Do not override protected cluster

            var prioDir = directions.First(d => d.Id == dirId);
            var crossingDirections = directions
                .Where(d => HasConflict(d, prioDir) && d.Color == LightColor.Green && !protect.Contains(d.Id))
                .ToList();

            if (crossingDirections.Any())
            {
                // Transition conflicting greens to orange
                foreach (var d in crossingDirections)
                {
                    d.Color = LightColor.Orange;
                    if (currentGreenDirections.Contains(d))
                    {
                        currentGreenDirections.Remove(d);
                        currentOrangeDirections.Add(d);
                    }
                }

                lastOrangeTime = DateTime.Now;
                SendTrafficLightStates();
                Console.WriteLine("Orange phase started for crossing directions");

                try { await Task.Delay(ORANGE_DURATION); }
                catch (Exception ex) { Console.WriteLine($"Error during orange phase: {ex.Message}"); }
            }

            // Turn all non-protected directions red
            foreach (var d in directions)
            {
                if (!protect.Contains(d.Id))
                {
                    d.Color = LightColor.Red;
                    currentGreenDirections.Remove(d);
                    currentOrangeDirections.Remove(d);
                }
            }

            // Activate single green for priority direction
            prioDir.Color = LightColor.Green;
            currentGreenDirections.Clear();
            currentOrangeDirections.Clear();
            currentGreenDirections.Add(prioDir);

            lastSwitchTime = DateTime.Now;
            lastOrangeTime = DateTime.Now;
            isOverrideActive = true;

            SendTrafficLightStates();
            Console.WriteLine($"Priority direction {dirId} set to green");
        }

        /// <summary>
        /// Clears any active single-green override, cycling remaining greens through orange then red.
        /// </summary>
        public async Task ClearOverride()
        {
            if (!isOverrideActive) return;

            var protect = GetProtectedBridgeCluster();

            // Transition current greens to orange
            foreach (var greenDir in currentGreenDirections.ToList())
            {
                if (!protect.Contains(greenDir.Id))
                {
                    greenDir.Color = LightColor.Orange;
                    currentOrangeDirections.Add(greenDir);
                    currentGreenDirections.Remove(greenDir);
                }
            }

            lastOrangeTime = DateTime.Now;
            SendTrafficLightStates();
            Console.WriteLine("Orange phase started in ClearOverride");

            try { await Task.Delay(ORANGE_DURATION); }
            catch (Exception ex) { Console.WriteLine($"Error during orange phase: {ex.Message}"); }

            // Turn orange to red
            foreach (var orangeDir in currentOrangeDirections.ToList())
            {
                if (!protect.Contains(orangeDir.Id))
                {
                    orangeDir.Color = LightColor.Red;
                    currentOrangeDirections.Remove(orangeDir);
                }
            }

            // Ensure all non-protected are red
            foreach (var d in directions)
                if (!protect.Contains(d.Id))
                    d.Color = LightColor.Red;

            isOverrideActive = false;

            SendTrafficLightStates();
            Console.WriteLine("Override cleared, all lights set to red");
        }

        /// <summary>
        /// Builds the set of directions that must remain protected due to bridge interactions.
        /// </summary>
        private HashSet<int> GetProtectedBridgeCluster()
        {
            var cluster = new HashSet<int>(new[] { 71, 72 });
            foreach (int id in new[] { 71, 72 })
            {
                var dir = directions.FirstOrDefault(d => d.Id == id);
                if (dir == null) continue;
                foreach (int inter in dir.Intersections)
                    cluster.Add(inter);
            }
            return cluster;
        }

        /// <summary>
        /// Selects additional directions eligible to join the current green phase without conflicts.
        /// Excludes bridge‐blocked and jam‐blocked directions.
        /// </summary>
        private List<Direction> GetExtraGreenCandidates()
        {
            var bridgeSpecific = bridgeController.GetBridgeIntersectionSet();

            var avail = directions
                .Where(d =>
                    GetPriority(d) > 0 &&
                    !currentGreenDirections.Contains(d) &&
                    !(bridgeSpecific.Contains(d.Id) && IsBridgeOpen()) &&
                    !(jamEngaged && JAM_BLOCK_DIRECTIONS.Contains(d.Id)))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id)
                .ToList();

            var pick = new List<Direction>();
            foreach (var d in avail)
            {
                if (!pick.Any(x => HasConflict(x, d)) &&
                    !currentGreenDirections.Any(x => HasConflict(x, d)))
                    pick.Add(d);
            }
            return pick;
        }

        /// <summary>
        /// Checks whether the bridge is currently open, allowing non‐bridge traffic.
        /// </summary>
        private bool IsBridgeOpen()
        {
            return bridgeController.CurrentBridgeState != "dicht" &&
                   bridgeController.CurrentBridgeState != "dicht_en_geblokkeerd";
        }

        /// <summary>
        /// Selects the next set of green directions based on priority and conflict rules.
        /// Does nothing if an override is active.
        /// </summary>
        private void SwitchTrafficLights()
        {
            if (isOverrideActive || priorityManager?.HasActivePrio1 == true)
                return;

            // Clear any lingering orange lights
            if (currentOrangeDirections.Any())
                foreach (var d in currentOrangeDirections)
                    d.Color = LightColor.Red;
            currentOrangeDirections.Clear();

            var bridgeSpecific = bridgeController.GetBridgeIntersectionSet();

            var avail = directions
                .Where(d =>
                    GetPriority(d) > 0 &&
                    !(bridgeSpecific.Contains(d.Id) && IsBridgeOpen()) &&
                    !(jamEngaged && JAM_BLOCK_DIRECTIONS.Contains(d.Id)))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id)
                .ToList();

            var pick = new List<Direction>();
            foreach (var d in avail)
                if (!pick.Any(x => HasConflict(x, d)))
                    pick.Add(d);

            // Turn previous greens to red
            foreach (var g in currentGreenDirections)
                g.Color = LightColor.Red;

            // Activate new greens
            currentGreenDirections = pick;
            foreach (var g in pick)
                lastGreenTimes[g.Id] = DateTime.Now;
            foreach (var g in pick)
                g.Color = LightColor.Green;

            lastSwitchTime = DateTime.Now;
        }

        /// <summary>
        /// Determines whether two directions' intersection sets conflict.
        /// </summary>
        private static bool HasConflict(Direction a, Direction b)
            => a.Intersections.Contains(b.Id) || b.Intersections.Contains(a.Id);

        /// <summary>
        /// Calculates the base priority for a direction based on sensor activations.
        /// Front+back both active => 5, one active => 1, none => 0.
        /// </summary>
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

        /// <summary>
        /// Adjusts base priority by adding aging factor and any active prio2 vehicles on this direction.
        /// </summary>
        private int GetEffectivePriority(Direction d)
        {
            int baseP = GetPriority(d);
            DateTime last = lastGreenTimes.TryGetValue(d.Id, out var t) ? t : DateTime.Now;
            int aging = (int)((DateTime.Now - last).TotalSeconds / AGING_SCALE_SECONDS);
            bool hasPrio2 = false;

            if (priorityManager != null && !string.IsNullOrEmpty(communicator.PriorityVehicleData))
            {
                var prioData = JsonConvert
                    .DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(communicator.PriorityVehicleData)
                    ?? new Dictionary<string, List<Dictionary<string, object>>>();

                if (prioData.TryGetValue("queue", out var queue))
                {
                    foreach (var veh in queue)
                    {
                        if (veh.TryGetValue("baan", out var lane) &&
                            veh.TryGetValue("prioriteit", out var prio) &&
                            prio.ToString() == "2" &&
                            (lane?.ToString() ?? "").StartsWith($"{d.Id}."))
                        {
                            hasPrio2 = true;
                            break;
                        }
                    }
                }
            }

            return hasPrio2
                ? baseP + aging + 10    // Extra boost for priority 2 vehicles
                : baseP + aging;
        }

        /// <summary>
        /// Parses incoming sensor data JSON and updates sensor activation states.
        /// </summary>
        private void ProcessSensorMessage()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
                if (data == null) return;

                foreach (var kv in data)
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
            catch { /* Ignore malformed messages */ }
        }

        /// <summary>
        /// Aggregates current light colors into the payload and notifies bridge and subscribers.
        /// </summary>
        private void SendTrafficLightStates()
        {
            CombinedPayload.Clear();

            foreach (var d in directions)
            {
                if (d.TrafficLights == null) continue;
                var color = d.Color == LightColor.Green ? "groen"
                          : d.Color == LightColor.Orange ? "oranje"
                          : "rood";
                foreach (var tl in d.TrafficLights)
                    CombinedPayload[tl.Id] = color;
            }

            bridgeController.SendBridgeStates();
            StateChanged?.Invoke();
        }
    }
}
