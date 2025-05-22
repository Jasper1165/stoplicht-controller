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
        // ========= CONFIG =========
        private const int ORANGE_DURATION = 8_000;
        private const int DEFAULT_GREEN_DURATION = 10_000;
        private const int SHORT_GREEN_DURATION = 10_000;
        private const int PRIORITY_THRESHOLD = 3;
        private const int HIGH_PRIORITY_THRESHOLD = 6;
        private const double AGING_SCALE_SECONDS = 7;

        // Threshold voor jam-detectie (ms)
        private const int JAM_THRESHOLD_MS = 10_000;
        private bool jamEngaged = false;           // track of jam engaged
        private DateTime? jamDetectedAt = null;
        private DateTime? jamClearedAt = null;

        public bool JamEngaged => jamEngaged;     // expose jamEngaged

        private static readonly HashSet<int> JAM_BLOCK_DIRECTIONS = new() { 8, 12, 4 };
        private static readonly HashSet<int> BRIDGE_DIRECTIONS = new() { 71, 72 };

        // ========= STATE =========
        private DateTime lastSwitchTime = DateTime.Now;
        private DateTime lastOrangeTime = DateTime.Now;

        // FIX: Track oranje start tijd per richting
        private readonly Dictionary<int, DateTime> orangeStartTimes = new();

        private List<Direction> currentGreenDirections = new();
        private List<Direction> currentOrangeDirections = new();
        private static readonly Dictionary<int, DateTime> lastGreenTimes = new();

        private readonly Communicator communicator;
        public readonly List<Direction> directions;
        private readonly Bridge bridge;
        public readonly BridgeController bridgeController;
        private PriorityVehicleManager? priorityManager;
        private bool isOverrideActive = false;

        /// <summary>
        /// Wordt getriggerd zodra de gecombineerde state van verkeerslichten (en brug) verandert.
        /// </summary>
        public event Action StateChanged;

        /// <summary>
        /// Shared payload voor zowel verkeerslichten- als brugstaat.
        /// </summary>
        public readonly Dictionary<string, string> CombinedPayload = new();

        // ========= CONSTRUCTOR =========

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
            // Zorg dat een brug-update ook het StateChanged-event vuurt
            bridgeController.StateChanged += () => StateChanged?.Invoke();
        }

        /// <summary>
        /// Richtingen rond de brug die beschermd blijven als de brug open is.
        /// </summary>
        public HashSet<int> ProtectedBridgeCluster => GetProtectedBridgeCluster();

        public void SetPriorityManager(PriorityVehicleManager mgr) => priorityManager = mgr;

        public static void InitializeLastGreenTimes(IEnumerable<Direction> dirs)
        {
            foreach (var d in dirs)
                lastGreenTimes[d.Id] = DateTime.Now;
        }

        // ========= MAIN CYCLE =========

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

                try { await Task.Delay(500, token); }
                catch (TaskCanceledException) { break; }
            }

            Console.WriteLine("Traffic Light Controller stopped");
        }

        private async Task BridgeLoop()
        {
            while (true)
            {
                try
                {
                    bridgeController.ProcessBridgeSensorData();
                    await bridgeController.UpdateAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Bridge update error: {ex}");
                }
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

        // ========= TRAFFIC UPDATE =========

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

            // 2) Oranje-fase: global eight-second delay
            var sinceO = (now - lastOrangeTime).TotalMilliseconds;
            if (currentOrangeDirections.Any())
            {
                if (sinceO >= ORANGE_DURATION)
                {
                    // alle oranje-lichten tegelijk uit
                    foreach (var od in currentOrangeDirections.ToList())
                    {
                        od.Color = LightColor.Red;
                        currentOrangeDirections.Remove(od);
                    }

                    // nieuwe fase
                    SwitchTrafficLights();
                    SendTrafficLightStates();
                }
                return;
            }

            // 3) Groene-fase
            if (currentGreenDirections.Any())
            {
                int sumP = currentGreenDirections.Sum(GetEffectivePriority);
                int dur = sumP >= HIGH_PRIORITY_THRESHOLD
                            ? DEFAULT_GREEN_DURATION + 2000
                            : sumP < PRIORITY_THRESHOLD
                                ? SHORT_GREEN_DURATION
                                : DEFAULT_GREEN_DURATION;

                var sinceG = (now - lastSwitchTime).TotalMilliseconds;
                if (sinceG >= dur)
                {
                    // groen → oranje
                    foreach (var gd in currentGreenDirections.ToList())
                    {
                        gd.Color = LightColor.Orange;
                        currentOrangeDirections.Add(gd);
                        currentGreenDirections.Remove(gd);
                    }
                    lastOrangeTime = now;
                    SendTrafficLightStates();
                }
                else
                {
                    // probeer extra groen
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

                    // jam-block
                    if (jamEngaged)
                    {
                        bool changed = false;
                        foreach (var d in currentGreenDirections.ToList())
                        {
                            if (JAM_BLOCK_DIRECTIONS.Contains(d.Id))
                            {
                                d.Color = LightColor.Orange;
                                currentOrangeDirections.Add(d);
                                currentGreenDirections.Remove(d);
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            lastOrangeTime = now;
                            SendTrafficLightStates();
                        }
                    }
                }
                return;
            }

            // 4) Nieuwe set groen
            SwitchTrafficLights();
            SendTrafficLightStates();
        }



        // FIX: Helper methode om richting op oranje te zetten met juiste timing
        private void SetDirectionToOrange(Direction direction, DateTime time)
        {
            direction.Color = LightColor.Orange;
            orangeStartTimes[direction.Id] = time;

            if (!currentOrangeDirections.Contains(direction))
                currentOrangeDirections.Add(direction);
        }

        public async Task OverrideWithSingleGreen(int dirId)
        {
            var protect = GetProtectedBridgeCluster();
            if (protect.Contains(dirId)) return;

            var prioDir = directions.First(d => d.Id == dirId);
            var conflicts = directions
                .Where(d => HasConflict(d, prioDir) && d.Color == LightColor.Green && !protect.Contains(d.Id))
                .ToList();

            if (conflicts.Any())
            {
                var orangeTime = DateTime.Now;
                foreach (var d in conflicts)
                {
                    SetDirectionToOrange(d, orangeTime);
                    currentGreenDirections.Remove(d);
                }
                // Alleen lastOrangeTime updaten als er geen andere oranje richtingen waren
                if (orangeStartTimes.Count == conflicts.Count)
                    lastOrangeTime = orangeTime;

                SendTrafficLightStates();
                await Task.Delay(ORANGE_DURATION);
            }

            foreach (var d in directions)
            {
                if (!protect.Contains(d.Id))
                {
                    d.Color = LightColor.Red;
                    currentGreenDirections.Remove(d);
                    currentOrangeDirections.Remove(d);
                    orangeStartTimes.Remove(d.Id);
                }
            }

            prioDir.Color = LightColor.Green;
            currentGreenDirections.Clear();
            currentOrangeDirections.Clear();
            orangeStartTimes.Clear();
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

            var orangeTime = DateTime.Now;
            foreach (var gd in currentGreenDirections.ToList())
            {
                if (!protect.Contains(gd.Id))
                {
                    SetDirectionToOrange(gd, orangeTime);
                    currentGreenDirections.Remove(gd);
                }
            }
            // Alleen lastOrangeTime updaten als er geen andere oranje richtingen waren
            if (orangeStartTimes.Count == currentOrangeDirections.Count)
                lastOrangeTime = orangeTime;

            SendTrafficLightStates();
            await Task.Delay(ORANGE_DURATION);

            foreach (var od in currentOrangeDirections.ToList())
            {
                if (!protect.Contains(od.Id))
                {
                    od.Color = LightColor.Red;
                    currentOrangeDirections.Remove(od);
                    orangeStartTimes.Remove(od.Id);
                }
            }
            foreach (var d in directions)
                if (!protect.Contains(d.Id))
                    d.Color = LightColor.Red;

            isOverrideActive = false;
            SendTrafficLightStates();
        }

        // ========= HELPERS =========

        private HashSet<int> GetProtectedBridgeCluster()
        {
            var set = new HashSet<int>(BRIDGE_DIRECTIONS);
            foreach (var id in BRIDGE_DIRECTIONS)
            {
                var dir = directions.FirstOrDefault(d => d.Id == id);
                if (dir == null) continue;
                foreach (var i in dir.Intersections)
                    set.Add(i);
            }
            return set;
        }

        private List<Direction> GetExtraGreenCandidates()
        {
            var bridgeIds = bridgeController.GetBridgeIntersectionSet();
            var avail = directions
                .Where(d =>
                    GetPriority(d) > 0 &&
                    !currentGreenDirections.Contains(d) &&
                    !(bridgeIds.Contains(d.Id) && bridgeController.CurrentBridgeState != "dicht") &&
                    !(jamEngaged && JAM_BLOCK_DIRECTIONS.Contains(d.Id)))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id)
                .ToList();

            var pick = new List<Direction>();
            foreach (var d in avail)
            {
                // CRUCIALE FIX: Controleer conflicten met zowel nieuwe kandidaten als bestaande groene richtingen
                if (!pick.Any(x => HasConflict(x, d)) &&
                    !currentGreenDirections.Any(x => HasConflict(x, d)))
                    pick.Add(d);
            }
            return pick;
        }

        private void SwitchTrafficLights()
        {
            if (isOverrideActive || priorityManager?.HasActivePrio1 == true)
                return;

            if (currentOrangeDirections.Any())
                SetLightsToRed();

            var bridgeIds = bridgeController.GetBridgeIntersectionSet();
            var avail = directions
                .Where(d =>
                    GetPriority(d) > 0 &&
                    !(bridgeIds.Contains(d.Id) && bridgeController.CurrentBridgeState != "dicht") &&
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

        private void SetLightsToRed()
        {
            foreach (var d in currentOrangeDirections)
            {
                d.Color = LightColor.Red;
                orangeStartTimes.Remove(d.Id);
            }
            currentOrangeDirections.Clear();
        }

        private static bool HasConflict(Direction a, Direction b)
        {
            bool conflict = a.Intersections.Contains(b.Id) || b.Intersections.Contains(a.Id);

            return conflict;
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
                    if (prioData != null && prioData.TryGetValue("queue", out var q))
                    {
                        foreach (var v in q)
                        {
                            if (v.TryGetValue("baan", out var lane) &&
                                v.TryGetValue("prioriteit", out var prio) &&
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
        /// Bouwt de CombinedPayload en vuurt StateChanged.
        /// </summary>
        private void SendTrafficLightStates()
        {
            CombinedPayload.Clear();

            // 1) verkeerslichten
            foreach (var d in directions)
            {
                if (d.TrafficLights == null) continue;
                var color = d.Color == LightColor.Green ? "groen"
                          : d.Color == LightColor.Orange ? "oranje"
                          : "rood";
                foreach (var tl in d.TrafficLights)
                    CombinedPayload[tl.Id] = color;
            }

            // 2) laat BridgeController zijn deel toevoegen
            bridgeController.SendBridgeStates();

            // 3) vuurt event zodat StatePublisher enkel bij veranderingen
            StateChanged?.Invoke();
        }
    }
}