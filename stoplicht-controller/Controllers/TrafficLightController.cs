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
        private const int ORANGE_DURATION = 4_500;
        private const int DEFAULT_GREEN_DURATION = 10_000;
        private const int SHORT_GREEN_DURATION = 10_000;
        private const int PRIORITY_THRESHOLD = 3;
        private const int HIGH_PRIORITY_THRESHOLD = 6;
        private const double AGING_SCALE_SECONDS = 7;

        // Nieuwe threshold voor file-detectie (ms)
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
        private readonly BridgeController bridgeController;
        private readonly Bridge bridge;

        private PriorityVehicleManager? priorityManager;
        private bool isOverrideActive = false;

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
            bridgeController = new BridgeController(communicator, directions, bridge);
        }

        // Expose for PriorityVehicleManager
        public HashSet<int> ProtectedBridgeCluster => GetProtectedBridgeCluster();
        public void SetPriorityManager(PriorityVehicleManager mgr) => priorityManager = mgr;

        public static void InitializeLastGreenTimes(IEnumerable<Direction> dirs)
        {
            foreach (var d in dirs)
                lastGreenTimes[d.Id] = DateTime.Now;
        }

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
                ProcessSensorMessage();
                UpdateTrafficLights();
                await Task.Delay(500, token);
            }
        }

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

        private async Task PriorityLoop()
        {
            while (true)
            {
                try { priorityManager?.Update(); }
                catch (Exception ex) { Console.WriteLine($"Priority manager error: {ex}"); }
                await Task.Delay(500);
            }
        }

        private void UpdateTrafficLights()
        {
            var now = DateTime.Now;

            // 1) Threshold-gebaseerde jam-detectie
            bool sensorJam = bridge.TrafficJamNearBridge;

            // if (sensorJam)
            // {
            //     // reset “cleared”-timer
            //     jamClearedAt = null;

            //     // start “jam”-timer zodra we voor het eerst een auto meten
            //     if (!jamDetectedAt.HasValue)
            //         jamDetectedAt = now;
            //     // pas na threshold pas écht jamEngaged = true
            //     else if (!jamEngaged
            //              && (now - jamDetectedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
            //     {
            //         jamEngaged = true;
            //         jamDetectedAt = null;
            //         Console.WriteLine(">>> Jam engaged after threshold");
            //     }
            // }
            // else
            // {
            //     // reset “jam”-timer
            //     jamDetectedAt = null;

            //     // start “cleared”-timer zodra sensor weer vrij is
            //     if (!jamClearedAt.HasValue)
            //         jamClearedAt = now;
            //     // pas na threshold pas écht jamEngaged = false
            //     else if (jamEngaged
            //              && (now - jamClearedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
            //     {
            //         jamEngaged = false;
            //         jamClearedAt = null;
            //         Console.WriteLine(">>> Jam cleared after threshold");
            //     }
            // }

            // // 2) Eenvoudige jam-handling: directe roodfase voor blokkade-rijbanen
            // if (jamEngaged)
            // {
            //     foreach (var d in directions.Where(d => JAM_BLOCK_DIRECTIONS.Contains(d.Id)))
            //         d.Color = LightColor.Red;

            //     currentGreenDirections.Clear();
            //     currentOrangeDirections.Clear();

            //     SendTrafficLightStates();
            //     return;
            // }

            // 3) Normale verkeerslicht-flow als er geen jam is:
            var sinceG = (now - lastSwitchTime).TotalMilliseconds;
            var sinceO = (now - lastOrangeTime).TotalMilliseconds;

            // Oranje-fase
            if (currentOrangeDirections.Any())
            {
                if (sinceO >= ORANGE_DURATION)
                {
                    SetLightsToRed();
                    SwitchTrafficLights();
                    SendTrafficLightStates();
                }
                return;
            }

            // Groen-fase (+ extra)
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
                    SetLightsToOrange();
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
                }
                return;
            }

            // Nieuwe groene set
            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        public void OverrideWithSingleGreen(int dirId)
        {
            var protect = GetProtectedBridgeCluster();
            if (protect.Contains(dirId)) return;

            foreach (var d in directions)
                if (!protect.Contains(d.Id))
                    d.Color = LightColor.Red;

            currentGreenDirections.Clear();
            currentOrangeDirections.Clear();

            var prioDir = directions.First(d => d.Id == dirId);
            prioDir.Color = LightColor.Green;
            currentGreenDirections.Add(prioDir);

            lastSwitchTime = DateTime.Now;
            lastOrangeTime = DateTime.Now;
            isOverrideActive = true;

            SendTrafficLightStates();
        }

        public async Task ClearOverride()
        {
            if (!isOverrideActive) return;

            var protect = GetProtectedBridgeCluster();

            // Eerst de huidige groene richtingen naar oranje zetten
            foreach (var greenDir in currentGreenDirections.ToList())
            {
                if (!protect.Contains(greenDir.Id))
                {
                    greenDir.Color = LightColor.Orange;
                    currentOrangeDirections.Add(greenDir);
                    currentGreenDirections.Remove(greenDir);
                }
            }

            // Oranje fase zichtbaar maken
            lastOrangeTime = DateTime.Now;
            SendTrafficLightStates();
            Console.WriteLine("Oranje fase gestart in ClearOverride");

            try
            {
                // Wachten tijdens oranje fase
                await Task.Delay(ORANGE_DURATION);
                Console.WriteLine($"Oranje fase voltooid na {ORANGE_DURATION}ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fout tijdens oranje fase: {ex.Message}");
            }

            // Oranje richtingen naar rood zetten
            foreach (var orangeDir in currentOrangeDirections.ToList())
            {
                if (!protect.Contains(orangeDir.Id))
                {
                    orangeDir.Color = LightColor.Red;
                    currentOrangeDirections.Remove(orangeDir);
                }
            }

            // Zeker weten dat alle niet-beschermde richtingen rood zijn
            foreach (var d in directions)
                if (!protect.Contains(d.Id))
                    d.Color = LightColor.Red;

            isOverrideActive = false;

            SendTrafficLightStates();
            Console.WriteLine("Override opgeheven, alle lichten op rood gezet");
        }

        // ========== HELPERS ==========

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

        private List<Direction> GetExtraGreenCandidates()
        {
            var bridgeSpecific = bridgeController.GetBridgeIntersectionSet();

            var avail = directions
                .Where(d =>
                    GetPriority(d) > 0 &&
                    !currentGreenDirections.Contains(d) &&
                    // gebruik jamEngaged ipv sensor
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

        private void SetLightsToOrange()
        {
            currentOrangeDirections = new List<Direction>(currentGreenDirections);
            foreach (var d in currentGreenDirections)
                d.Color = LightColor.Orange;
            currentGreenDirections.Clear();
        }

        private void SetLightsToRed()
        {
            foreach (var d in currentOrangeDirections)
                d.Color = LightColor.Red;
            currentOrangeDirections.Clear();
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

        private void SendTrafficLightStates()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData)) return;
            var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(
                communicator.LaneSensorData);
            if (data == null) return;

            var dict = data.Keys.ToDictionary(
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

            dict["81.1"] = bridgeController.CurrentBridgeState;
            communicator.PublishMessage("stoplichten", dict);
        }
    }
}
