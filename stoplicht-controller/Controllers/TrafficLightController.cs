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
        private const int ORANGE_DURATION = 5_000;
        private const int DEFAULT_GREEN_DURATION = 10_000;
        private const int SHORT_GREEN_DURATION = 10_000;
        private const int PRIORITY_THRESHOLD = 3;
        private const int HIGH_PRIORITY_THRESHOLD = 6;
        private const double AGING_SCALE_SECONDS = 7;

        // Threshold for traffic jam detection (ms)
        private const int JAM_THRESHOLD_MS = 10_000;
        private bool jamEngaged = false;
        private DateTime? jamDetectedAt = null;
        private DateTime? jamClearedAt = null;

        private static readonly HashSet<int> JAM_BLOCK_DIRECTIONS = new() { 8, 12, 4 };
        private static readonly HashSet<int> BRIDGE_DIRECTIONS = new() { 71, 72 };

        // ========= STATE =========
        private DateTime lastSwitchTime = DateTime.Now;
        private DateTime lastOrangeTime = DateTime.Now;

        private List<Direction> currentGreenDirections = new();
        private List<Direction> currentOrangeDirections = new();
        private static readonly Dictionary<int, DateTime> lastGreenTimes = new();

        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        // Combined payload for sending bridge combined with traffic light data
        public readonly Dictionary<string, string> CombinedPayload = new Dictionary<string, string>();
        private readonly BridgeController bridgeController;
        private readonly Bridge bridge;

        private PriorityVehicleManager? priorityManager;
        private bool isOverrideActive = false;

        /// <summary>
        /// Initializes a new instance of the TrafficLightController class.
        /// </summary>
        /// <param name="communicator">Communication manager for sending/receiving messages</param>
        /// <param name="directions">List of all available traffic directions</param>
        /// <param name="bridge">Bridge instance for monitoring bridge status</param>
        /// <param name="priorityManager">Optional priority vehicle manager</param>
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
        }

        // Expose for PriorityVehicleManager
        public HashSet<int> ProtectedBridgeCluster => GetProtectedBridgeCluster();

        /// <summary>
        /// Sets the priority vehicle manager for this controller
        /// </summary>
        public void SetPriorityManager(PriorityVehicleManager mgr) => priorityManager = mgr;

        /// <summary>
        /// Initializes the last green times dictionary for all directions
        /// </summary>
        public static void InitializeLastGreenTimes(IEnumerable<Direction> dirs)
        {
            foreach (var d in dirs)
                lastGreenTimes[d.Id] = DateTime.Now;
        }

        /// <summary>
        /// Main traffic light control loop that runs continuously
        /// </summary>
        public async Task TrafficLightCycleLoop(CancellationToken token)
        {
            _ = Task.Run(BridgeLoop, token);
            _ = Task.Run(PriorityLoop, token);

            Console.WriteLine("Traffic Light Controller started");
            if (directions.Any())
            {
                Console.WriteLine($"Initial directions count: {directions.Count}");
                SwitchTrafficLights();
                SendTrafficLightStates();
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    ProcessSensorMessage();
                    UpdateTrafficLights();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] TrafficLight update failure: {ex}");
                    continue;
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
        /// Background loop for processing bridge-related functions
        /// </summary>
        private async Task BridgeLoop()
        {
            while (true)
            {
                bridgeController.ProcessBridgeSensorData();
                try { bridgeController.UpdateAsync(); }
                catch (Exception ex) { Console.WriteLine($"Bridge update error: {ex}"); }
                await Task.Delay(500);
            }
        }

        /// <summary>
        /// Background loop for processing priority vehicle functions
        /// </summary>
        private async Task PriorityLoop()
        {
            while (true)
            {
                try { priorityManager?.Update(); }
                catch (Exception ex) { Console.WriteLine($"Priority manager error: {ex}"); }
                await Task.Delay(500);
            }
        }

        /// <summary>
        /// Updates the traffic light states based on traffic conditions
        /// </summary>
        private void UpdateTrafficLights()
        {
            var now = DateTime.Now;

            // 1) Threshold-based jam detection
            bool sensorJam = bridge.TrafficJamNearBridge;

            if (sensorJam)
            {
                // Reset "cleared" timer
                jamClearedAt = null;

                // Start "jam" timer when we first detect a vehicle
                if (!jamDetectedAt.HasValue)
                    jamDetectedAt = now;
                // Set jamEngaged to true only after threshold passed
                else if (!jamEngaged
                         && (now - jamDetectedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
                {
                    jamEngaged = true;
                    jamDetectedAt = null;
                    Console.WriteLine(">>> Jam engaged after threshold");
                }
            }
            else
            {
                // Reset "jam" timer
                jamDetectedAt = null;

                // Start "cleared" timer when sensor is free again
                if (!jamClearedAt.HasValue)
                    jamClearedAt = now;
                // Set jamEngaged to false only after threshold passed
                else if (jamEngaged
                         && (now - jamClearedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
                {
                    jamEngaged = false;
                    jamClearedAt = null;
                    Console.WriteLine(">>> Jam cleared after threshold");
                }
            }

            // 2) Get bridge cluster that should be protected
            var protectedDirections = GetProtectedBridgeCluster();

            // 3) Check orange phase first - this has highest priority
            var sinceG = (now - lastSwitchTime).TotalMilliseconds;
            var sinceO = (now - lastOrangeTime).TotalMilliseconds;

            if (currentOrangeDirections.Any())
            {
                if (sinceO >= ORANGE_DURATION)
                {
                    // Orange phase complete, set to red
                    foreach (var orangeDir in currentOrangeDirections.ToList())
                    {
                        orangeDir.Color = LightColor.Red;
                        currentOrangeDirections.Remove(orangeDir);
                    }

                    // Now switch to a new green set
                    SwitchTrafficLights();
                    SendTrafficLightStates();
                }
                return;
            }

            // 4) Process green phase
            if (currentGreenDirections.Any())
            {
                int sumP = currentGreenDirections.Sum(GetEffectivePriority);
                int dur = sumP >= HIGH_PRIORITY_THRESHOLD
                    ? DEFAULT_GREEN_DURATION + 2000
                    : sumP < PRIORITY_THRESHOLD
                        ? SHORT_GREEN_DURATION
                        : DEFAULT_GREEN_DURATION;

                // Check if we need to end the green phase
                if (sinceG >= dur)
                {
                    // First set current green directions to orange
                    foreach (var greenDir in currentGreenDirections.ToList())
                    {
                        // FIX: Don't directly set jam directions to red,
                        // let them follow normal orange->red sequence
                        greenDir.Color = LightColor.Orange;
                        currentOrangeDirections.Add(greenDir);
                        currentGreenDirections.Remove(greenDir);
                    }

                    // Start orange phase
                    lastOrangeTime = now;
                    SendTrafficLightStates();

                    // Red phase will be handled in the next tick
                    // when sinceO >= ORANGE_DURATION
                }
                else
                {
                    // Check if we can add more green directions
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

                    // Apply jam blocking only to green directions that aren't in orange phase
                    if (jamEngaged)
                    {
                        bool statesChanged = false;

                        // Only block jam directions that are currently green
                        // Don't touch directions that are already in orange phase
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

            // 5) No green or orange directions active - switch to new green set
            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        /// <summary>
        /// Override normal traffic light cycles to give priority to a specific direction
        /// </summary>
        /// <param name="dirId">Direction ID to prioritize</param>
        public async Task OverrideWithSingleGreen(int dirId)
        {
            var protect = GetProtectedBridgeCluster();
            if (protect.Contains(dirId)) return;

            // Find the priority direction
            var prioDir = directions.First(d => d.Id == dirId);

            // Find all crossing directions (directions that conflict with the priority direction)
            var crossingDirections = directions
                .Where(d => HasConflict(d, prioDir) && d.Color == LightColor.Green && !protect.Contains(d.Id))
                .ToList();

            // If there are crossing directions on green, set them to orange first
            if (crossingDirections.Any())
            {
                // Set crossing directions to orange
                foreach (var d in crossingDirections)
                {
                    d.Color = LightColor.Orange;
                    if (currentGreenDirections.Contains(d))
                    {
                        currentGreenDirections.Remove(d);
                        currentOrangeDirections.Add(d);
                    }
                }

                // Start orange phase
                lastOrangeTime = DateTime.Now;
                SendTrafficLightStates();
                Console.WriteLine("Orange phase started for crossing directions");

                try
                {
                    // Wait during orange phase
                    await Task.Delay(ORANGE_DURATION);
                    Console.WriteLine($"Orange phase completed after {ORANGE_DURATION}ms");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during orange phase: {ex.Message}");
                }
            }

            // Set all required directions to red
            foreach (var d in directions)
            {
                if (!protect.Contains(d.Id))
                {
                    d.Color = LightColor.Red;
                    if (currentGreenDirections.Contains(d))
                        currentGreenDirections.Remove(d);
                    if (currentOrangeDirections.Contains(d))
                        currentOrangeDirections.Remove(d);
                }
            }

            // Now we can set the priority direction to green
            prioDir.Color = LightColor.Green;
            currentGreenDirections.Clear(); // For safety
            currentOrangeDirections.Clear();
            currentGreenDirections.Add(prioDir);

            lastSwitchTime = DateTime.Now;
            lastOrangeTime = DateTime.Now;
            isOverrideActive = true;

            SendTrafficLightStates();
            Console.WriteLine($"Priority direction {dirId} set to green");
        }

        /// <summary>
        /// Clear the override state and return to normal traffic light cycles
        /// </summary>
        public async Task ClearOverride()
        {
            if (!isOverrideActive) return;

            var protect = GetProtectedBridgeCluster();

            // First set current green directions to orange
            foreach (var greenDir in currentGreenDirections.ToList())
            {
                if (!protect.Contains(greenDir.Id))
                {
                    greenDir.Color = LightColor.Orange;
                    currentOrangeDirections.Add(greenDir);
                    currentGreenDirections.Remove(greenDir);
                }
            }

            // Start orange phase
            lastOrangeTime = DateTime.Now;
            SendTrafficLightStates();
            Console.WriteLine("Orange phase started in ClearOverride");

            try
            {
                // Wait during orange phase
                await Task.Delay(ORANGE_DURATION);
                Console.WriteLine($"Orange phase completed after {ORANGE_DURATION}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during orange phase: {ex.Message}");
            }

            // Set orange directions to red
            foreach (var orangeDir in currentOrangeDirections.ToList())
            {
                if (!protect.Contains(orangeDir.Id))
                {
                    orangeDir.Color = LightColor.Red;
                    currentOrangeDirections.Remove(orangeDir);
                }
            }

            // Make sure all non-protected directions are red
            foreach (var d in directions)
                if (!protect.Contains(d.Id))
                    d.Color = LightColor.Red;

            isOverrideActive = false;

            SendTrafficLightStates();
            Console.WriteLine("Override cleared, all lights set to red");
        }

        // ========== HELPERS ==========

        /// <summary>
        /// Gets the cluster of protected directions related to the bridge
        /// </summary>
        private HashSet<int> GetProtectedBridgeCluster()
        {
            var cluster = new HashSet<int>(BRIDGE_DIRECTIONS);
            foreach (int id in BRIDGE_DIRECTIONS)
            {
                var dir = directions.FirstOrDefault(d => d.Id == id);
                if (dir == null) continue;
                foreach (int inter in dir.Intersections)
                    cluster.Add(inter);
            }
            return cluster;
        }

        /// <summary>
        /// Gets additional directions that can be set to green without conflicts
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
        /// Checks if the bridge is currently open
        /// </summary>
        private bool IsBridgeOpen()
        {
            return bridgeController.CurrentBridgeState != "dicht" &&
                   bridgeController.CurrentBridgeState != "dicht_en_geblokkeerd";
        }

        /// <summary>
        /// Switches to a new set of green traffic lights based on priorities
        /// </summary>
        private void SwitchTrafficLights()
        {
            if (isOverrideActive || priorityManager?.HasActivePrio1 == true)
                return;

            if (currentOrangeDirections.Any())
                SetLightsToRed();

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

            foreach (var g in currentGreenDirections)
                g.Color = LightColor.Red;
            currentGreenDirections = pick;
            foreach (var g in pick)
                lastGreenTimes[g.Id] = DateTime.Now;
            foreach (var g in pick)
                g.Color = LightColor.Green;

            lastSwitchTime = DateTime.Now;
        }

        /// <summary>
        /// Sets all green lights to orange
        /// </summary>
        private void SetLightsToOrange()
        {
            currentOrangeDirections = new List<Direction>(currentGreenDirections);
            foreach (var d in currentGreenDirections)
                d.Color = LightColor.Orange;
            currentGreenDirections.Clear();
        }

        /// <summary>
        /// Sets all orange lights to red
        /// </summary>
        private void SetLightsToRed()
        {
            foreach (var d in currentOrangeDirections)
                d.Color = LightColor.Red;
            currentOrangeDirections.Clear();
        }

        /// <summary>
        /// Checks if two directions have a conflict (crossing paths)
        /// </summary>
        private static bool HasConflict(Direction a, Direction b)
            => a.Intersections.Contains(b.Id) || b.Intersections.Contains(a.Id);

        /// <summary>
        /// Gets the base priority for a direction based on sensor data
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
        /// Gets the effective priority for a direction based on sensor data, aging, and priority vehicles
        /// </summary>
        private int GetEffectivePriority(Direction d)
        {
            int baseP = GetPriority(d);
            DateTime last = lastGreenTimes.TryGetValue(d.Id, out var t) ? t : DateTime.Now;
            int aging = (int)((DateTime.Now - last).TotalSeconds / AGING_SCALE_SECONDS);
            bool hasPrio2 = false;

            if (priorityManager != null && !string.IsNullOrEmpty(communicator.PriorityVehicleData))
            {
                try
                {
                    var prioData = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(
                        communicator.PriorityVehicleData);
                    if (prioData != null && prioData.TryGetValue("queue", out var queue))
                    {
                        foreach (var veh in queue)
                        {
                            if (veh.TryGetValue("baan", out var lane) &&
                                veh.TryGetValue("prioriteit", out var prio) &&
                                prio.ToString() == "2" &&
                                (lane.ToString() ?? "").StartsWith($"{d.Id}."))
                            {
                                hasPrio2 = true;
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            return hasPrio2
                ? baseP + aging + 10
                : baseP + aging;
        }

        /// <summary>
        /// Processes sensor data from the communicator
        /// </summary>
        private void ProcessSensorMessage()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData)) return;
            try
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(
                    communicator.LaneSensorData);
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
            catch { }
        }

        /// <summary>
        /// Sends the current traffic light states to the communicator
        /// </summary>
        private void SendTrafficLightStates()
        {
            // 1) update de TL-staten in CombinedPayload
            foreach (var dir in directions)
            {
                if (dir.TrafficLights == null) continue;
                var color = dir.Color == LightColor.Green ? "groen"
                          : dir.Color == LightColor.Orange ? "oranje"
                          : "rood";
                foreach (var tl in dir.TrafficLights)
                    CombinedPayload[tl.Id] = color;
            }

            // 2) vraag BridgeController om wél zijn deel te updaten
            bridgeController.SendBridgeStates();

            // 3) publiceer uiteindelijk maar één keer het totaal
            communicator.PublishMessage("stoplichten", CombinedPayload);
        }
    }
}