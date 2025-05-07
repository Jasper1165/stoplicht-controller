// TrafficLightController.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;
using stoplicht_controller.Managers;   // voor PriorityVehicleManager

namespace stoplicht_controller.Managers
{
    public class TrafficLightController
    {
        // ========= CONFIG =========
        private const int ORANGE_DURATION = 3000;
        private const int DEFAULT_GREEN_DURATION = 6000;
        private const int SHORT_GREEN_DURATION = 3000;
        private const int PRIORITY_THRESHOLD = 3;
        private const int HIGH_PRIORITY_THRESHOLD = 6;
        private const double AGING_SCALE_SECONDS = 7;

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

        private PriorityVehicleManager? priorityManager;   // via setter om circular dependency te breken

        // Tracks if an override is currently active
        private bool isOverrideActive = false;

        // ========= CTOR =========
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

        public void SetPriorityManager(PriorityVehicleManager mgr) => priorityManager = mgr;

        public static void InitializeLastGreenTimes(IEnumerable<Direction> dirs)
        {
            foreach (var d in dirs) lastGreenTimes[d.Id] = DateTime.Now;
        }

        // ========= MAIN LOOP =========
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
                bridgeController.ProcessBridgeSensorData();
                bridgeController.Update();

                // Update priority vehicle manager
                priorityManager?.Update();

                UpdateTrafficLights();
                await Task.Delay(500, token);
            }
        }

        // ========= UPDATE =========
        private void UpdateTrafficLights()
        {
            // 0. pauzeer als er prio-1 actief is
            if (priorityManager?.HasActivePrio1 == true)
                return;

            // 1. file-blokkades
            if (bridge.TrafficJamNearBridge)
                ForceJamDirectionsToRed();

            var now = DateTime.Now;
            var sinceG = (now - lastSwitchTime).TotalMilliseconds;
            var sinceO = (now - lastOrangeTime).TotalMilliseconds;

            // 2. oranje-fase
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

            // 3. bestaande groen-fase
            if (currentGreenDirections.Any())
            {
                int sumP = currentGreenDirections.Sum(GetEffectivePriority);
                int dur = sumP >= HIGH_PRIORITY_THRESHOLD ? DEFAULT_GREEN_DURATION + 2000
                         : sumP < PRIORITY_THRESHOLD ? SHORT_GREEN_DURATION
                         : DEFAULT_GREEN_DURATION;

                if (sinceG >= dur)
                {
                    SetLightsToOrange();
                    lastOrangeTime = DateTime.Now;
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
                            lastGreenTimes[e.Id] = DateTime.Now;
                        }
                        lastSwitchTime = DateTime.Now;
                        SendTrafficLightStates();
                    }
                }
                return;
            }

            // 4. niets groen → nieuwe set kiezen
            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        // ========= OVERRIDES VOOR PRIO-1 =========
        public void OverrideWithSingleGreen(int dirId)
        {
            var protect = GetProtectedBridgeCluster();

            // a) zet alles buiten het brug-cluster op rood
            foreach (var d in directions)
                if (!protect.Contains(d.Id))
                    d.Color = LightColor.Red;

            // b) reset interne state
            currentGreenDirections.Clear();
            currentOrangeDirections.Clear();

            // c) activeer de prio-richting
            var prioDir = directions.First(d => d.Id == dirId);
            prioDir.Color = LightColor.Green;
            currentGreenDirections.Add(prioDir);

            lastSwitchTime = lastOrangeTime = DateTime.Now;
            isOverrideActive = true;
            SendTrafficLightStates();
        }

        public void ClearOverride()
        {
            if (!isOverrideActive) return;

            var protect = GetProtectedBridgeCluster();

            foreach (var d in directions)
                if (!protect.Contains(d.Id))
                    d.Color = LightColor.Red;

            currentGreenDirections.Clear();
            currentOrangeDirections.Clear();
            isOverrideActive = false;
            SendTrafficLightStates();
        }

        // ========= LOW-LEVEL HELPERS =========
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

        private void ForceJamDirectionsToRed()
        {
            foreach (var id in JAM_BLOCK_DIRECTIONS)
            {
                var dir = directions.FirstOrDefault(d => d.Id == id);
                if (dir != null && dir.Color != LightColor.Red)
                {
                    dir.Color = LightColor.Red;
                    currentGreenDirections.Remove(dir);
                    currentOrangeDirections.Remove(dir);
                }
            }
        }

        private List<Direction> GetExtraGreenCandidates()
        {
            var blocked = bridgeController.GetBridgeIntersectionSet();

            var avail = directions
                .Where(d => GetPriority(d) > 0 &&
                            !currentGreenDirections.Contains(d) &&
                            !blocked.Contains(d.Id) &&
                            !(bridge.TrafficJamNearBridge && JAM_BLOCK_DIRECTIONS.Contains(d.Id)))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id);

            var pick = new List<Direction>();
            foreach (var d in avail)
                if (!pick.Any(x => HasConflict(x, d)) &&
                    !currentGreenDirections.Any(x => HasConflict(x, d)))
                    pick.Add(d);

            return pick;
        }

        private void SwitchTrafficLights()
        {
            // Don't switch if override is active
            if (isOverrideActive || priorityManager?.HasActivePrio1 == true)
                return;

            var blocked = bridgeController.GetBridgeIntersectionSet();

            var avail = directions
                .Where(d => GetPriority(d) > 0 &&
                            !blocked.Contains(d.Id) &&
                            !(bridge.TrafficJamNearBridge && JAM_BLOCK_DIRECTIONS.Contains(d.Id)))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id)
                .ToList();

            if (!avail.Any()) return;

            var pick = new List<Direction>();
            foreach (var d in avail)
                if (!pick.Any(x => HasConflict(x, d)))
                    pick.Add(d);

            foreach (var d in currentGreenDirections) d.Color = LightColor.Red;
            currentGreenDirections = pick;

            foreach (var d in pick)
            {
                d.Color = LightColor.Green;
                lastGreenTimes[d.Id] = DateTime.Now;
            }
            lastSwitchTime = DateTime.Now;
        }

        private void SetLightsToOrange()
        {
            foreach (var d in currentGreenDirections) d.Color = LightColor.Orange;
            currentOrangeDirections = new List<Direction>(currentGreenDirections);
            currentGreenDirections.Clear();
        }

        private void SetLightsToRed()
        {
            foreach (var d in currentOrangeDirections) d.Color = LightColor.Red;
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

            // Boost priority for directions that might have priority 2 vehicles (buses)
            bool hasPrio2 = false;
            if (priorityManager != null && !string.IsNullOrEmpty(communicator.PriorityVehicleData))
            {
                try
                {
                    var priorityData = JsonConvert.DeserializeObject<Dictionary<string, List<Dictionary<string, object>>>>(
                        communicator.PriorityVehicleData);

                    if (priorityData != null && priorityData.TryGetValue("queue", out var queue))
                    {
                        foreach (var vehicle in queue)
                        {
                            if (vehicle.TryGetValue("baan", out var lane) &&
                                vehicle.TryGetValue("prioriteit", out var prio) &&
                                prio.ToString() == "2")
                            {
                                string laneName = lane.ToString() ?? "";
                                if (laneName.StartsWith($"{d.Id}."))
                                {
                                    hasPrio2 = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Swallow JSON parsing errors
                }
            }

            // Apply priority 2 boost (buses get priority)
            return hasPrio2 ? baseP + aging + 10 : baseP + aging;
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
                        if (s.Position == SensorPosition.Front &&
                            kv.Value.TryGetValue("voor", out bool fv))
                            s.IsActivated = fv;
                        if (s.Position == SensorPosition.Back &&
                            kv.Value.TryGetValue("achter", out bool bv))
                            s.IsActivated = bv;
                    }
                }
            }
            catch { /* swallow */ }
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