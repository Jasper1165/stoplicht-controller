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

        // ========= STATE =========
        private DateTime lastSwitchTime = DateTime.Now;
        private DateTime lastOrangeTime = DateTime.Now;

        private List<Direction> currentGreenDirections = new();
        private List<Direction> currentOrangeDirections = new();
        private static readonly Dictionary<int, DateTime> lastGreenTimes = new();

        private readonly Communicator communicator;
        public readonly List<Direction> directions;
        public readonly BridgeController bridgeController;
        private readonly Bridge bridge;

        private PriorityVehicleManager? priorityManager;
        private bool isOverrideActive = false;

        // Maak de event nullable om CS8618 op te lossen
        public event Action? StateChanged;
        public readonly Dictionary<string, string> CombinedPayload = new();

        /// <summary>
        /// Initializes a new instance of the TrafficLightController class.
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
        /// Expose for PriorityVehicleManager
        /// </summary>
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
                try
                {
                    // Await the async update to avoid CS4014
                    await bridgeController.UpdateAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Bridge update error: {ex}");
                }
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
                try
                {
                    // PriorityManager.Update is synchronous; wrap exceptions
                    priorityManager?.Update();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Priority manager error: {ex}");
                }
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
                jamClearedAt = null;
                if (!jamDetectedAt.HasValue)
                    jamDetectedAt = now;
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
                jamDetectedAt = null;
                if (!jamClearedAt.HasValue)
                    jamClearedAt = now;
                else if (jamEngaged
                         && (now - jamClearedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
                {
                    jamEngaged = false;
                    jamClearedAt = null;
                    Console.WriteLine(">>> Jam cleared after threshold");
                }
            }

            var protectedDirections = GetProtectedBridgeCluster();

            var sinceG = (now - lastSwitchTime).TotalMilliseconds;
            var sinceO = (now - lastOrangeTime).TotalMilliseconds;

            if (currentOrangeDirections.Any())
            {
                if (sinceO >= ORANGE_DURATION)
                {
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

            if (currentGreenDirections.Any())
            {
                int sumP = currentGreenDirections.Sum(GetEffectivePriority);
                int dur = sumP >= HIGH_PRIORITY_THRESHOLD
                    ? DEFAULT_GREEN_DURATION + 2000
                    : sumP < PRIORITY_THRESHOLD
                        ? SHORT_GREEN_DURATION
                        : DEFAULT_GREEN_DURATION;

                if (sinceG >= dur)
                {
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

            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        public async Task OverrideWithSingleGreen(int dirId)
        {
            var protect = GetProtectedBridgeCluster();
            if (protect.Contains(dirId)) return;

            var prioDir = directions.First(d => d.Id == dirId);
            var crossingDirections = directions
                .Where(d => HasConflict(d, prioDir)
                            && d.Color == LightColor.Green
                            && !protect.Contains(d.Id))
                .ToList();

            if (crossingDirections.Any())
            {
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

            foreach (var d in directions)
            {
                if (!protect.Contains(d.Id))
                {
                    d.Color = LightColor.Red;
                    currentGreenDirections.Remove(d);
                    currentOrangeDirections.Remove(d);
                }
            }

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

        public async Task ClearOverride()
        {
            if (!isOverrideActive) return;

            var protect = GetProtectedBridgeCluster();

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

            foreach (var orangeDir in currentOrangeDirections.ToList())
            {
                if (!protect.Contains(orangeDir.Id))
                {
                    orangeDir.Color = LightColor.Red;
                    currentOrangeDirections.Remove(orangeDir);
                }
            }

            foreach (var d in directions)
                if (!protect.Contains(d.Id))
                    d.Color = LightColor.Red;

            isOverrideActive = false;

            SendTrafficLightStates();
            Console.WriteLine("Override cleared, all lights set to red");
        }

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

        private bool IsBridgeOpen()
        {
            return bridgeController.CurrentBridgeState != "dicht" &&
                   bridgeController.CurrentBridgeState != "dicht_en_geblokkeerd";
        }

        private void SwitchTrafficLights()
        {
            if (isOverrideActive || priorityManager?.HasActivePrio1 == true)
                return;

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

            foreach (var g in currentGreenDirections)
                g.Color = LightColor.Red;
            currentGreenDirections = pick;
            foreach (var g in pick)
                lastGreenTimes[g.Id] = DateTime.Now;
            foreach (var g in pick)
                g.Color = LightColor.Green;

            lastSwitchTime = DateTime.Now;
        }

        private static bool HasConflict(Direction a, Direction b)
            => a.Intersections.Contains(b.Id) || b.Intersections.Contains(a.Id);

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
                ? baseP + aging + 10
                : baseP + aging;
        }

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
            catch { }
        }

        /// <summary>
        /// Sends the current traffic light states to the communicator
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
