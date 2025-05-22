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
        private bool jamEngaged = false;
        private DateTime? jamDetectedAt = null;
        private DateTime? jamClearedAt = null;

        public bool JamEngaged => jamEngaged;

        private static readonly HashSet<int> JAM_BLOCK_DIRECTIONS = new() { 8, 12, 4 };
        private static readonly HashSet<int> BRIDGE_DIRECTIONS = new() { 71, 72 };

        // ========= STATE =========
        private DateTime lastSwitchTime = DateTime.Now;

        // GEFIXED: Gebruik alleen per-richting timing, geen globale lastOrangeTime meer
        private readonly Dictionary<int, DateTime> orangeStartTimes = new();
        private readonly Dictionary<int, DateTime> greenStartTimes = new();

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
                    await UpdateTrafficLights();
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

        private async Task UpdateTrafficLights()
        {
            var now = DateTime.Now;
            var protect = GetProtectedBridgeCluster();

            // ─── Auto-clear override ───
            // Als we in override zitten, maar er is geen Prio-1 meer actief,
            // dan resetten we de override en gaan we terug naar normale cyclus.
            if (isOverrideActive && !(priorityManager?.HasActivePrio1 ?? false))
            {
                await ClearOverride();
                return;
            }

            // ─── Prio-1 override via helper ───
            if (await HandlePrio1OverrideAsync())
                return;

            // --- Jam-detectie ---
            bool sensorJam = bridge.TrafficJamNearBridge;
            if (sensorJam)
            {
                jamClearedAt = null;
                if (!jamDetectedAt.HasValue) jamDetectedAt = now;
                else if (!jamEngaged
                         && (now - jamDetectedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
                {
                    jamEngaged = true;
                    jamDetectedAt = null;
                    Console.WriteLine(">>> Jam engaged");
                }
            }
            else
            {
                jamDetectedAt = null;
                if (!jamClearedAt.HasValue) jamClearedAt = now;
                else if (jamEngaged
                         && (now - jamClearedAt.Value).TotalMilliseconds >= JAM_THRESHOLD_MS)
                {
                    jamEngaged = false;
                    jamClearedAt = null;
                    Console.WriteLine(">>> Jam cleared");
                }
            }

            // --- Oranje-fase per richting ---
            if (currentOrangeDirections.Any())
            {
                bool anyOrangeProcessed = false;

                foreach (var od in currentOrangeDirections.ToList())
                {
                    var orangeStart = orangeStartTimes.GetValueOrDefault(od.Id, now);
                    var orangeDuration = (now - orangeStart).TotalMilliseconds;

                    if (orangeDuration >= ORANGE_DURATION)
                    {
                        if (protect.Contains(od.Id))
                        {
                            currentOrangeDirections.Remove(od);
                            orangeStartTimes.Remove(od.Id);
                        }
                        else
                        {
                            SetDirectionToRed(od);
                            anyOrangeProcessed = true;
                        }
                    }
                }

                if (anyOrangeProcessed && !currentOrangeDirections.Any(od => !protect.Contains(od.Id)))
                {
                    SwitchTrafficLights();
                    SendTrafficLightStates();
                }
                return;
            }

            // --- Groene-fase per richting ---
            if (currentGreenDirections.Any())
            {
                var nonProtectedGreen = currentGreenDirections.Where(gd => !protect.Contains(gd.Id)).ToList();

                if (nonProtectedGreen.Any())
                {
                    int sumP = nonProtectedGreen.Sum(GetEffectivePriority);
                    int dur = sumP >= HIGH_PRIORITY_THRESHOLD
                                ? DEFAULT_GREEN_DURATION + 2000
                                : sumP < PRIORITY_THRESHOLD
                                    ? SHORT_GREEN_DURATION
                                    : DEFAULT_GREEN_DURATION;

                    var sinceG = (now - lastSwitchTime).TotalMilliseconds;

                    if (sinceG >= dur)
                    {
                        foreach (var gd in nonProtectedGreen)
                        {
                            SetDirectionToOrange(gd, now);
                            currentGreenDirections.Remove(gd);
                        }
                        SendTrafficLightStates();
                        return;
                    }
                }

                var extra = GetExtraGreenCandidates()
                    .Where(d => !protect.Contains(d.Id))
                    .ToList();
                if (extra.Any())
                {
                    foreach (var e in extra)
                    {
                        SetDirectionToGreen(e, now);
                        currentGreenDirections.Add(e);
                    }
                    lastSwitchTime = now;
                    SendTrafficLightStates();
                }

                if (jamEngaged)
                {
                    bool changed = false;
                    foreach (var d in currentGreenDirections.ToList())
                    {
                        if (protect.Contains(d.Id)) continue;
                        if (JAM_BLOCK_DIRECTIONS.Contains(d.Id))
                        {
                            SetDirectionToOrange(d, now);
                            currentGreenDirections.Remove(d);
                            changed = true;
                        }
                    }
                    if (changed)
                        SendTrafficLightStates();
                }
                return;
            }

            // --- Geen actieve fase: nieuwe set groen (skip protected) ---
            SwitchTrafficLights();
            SendTrafficLightStates();
        }
        /// <summary>
        /// Behandelt de Prio-1 override: oranje → wacht → rood → prio-groen.
        /// Geeft true terug als de override is uitgevoerd.
        /// </summary>
        private async Task<bool> HandlePrio1OverrideAsync()
        {
            if (priorityManager?.HasActivePrio1 != true || isOverrideActive)
                return false;

            int? prioDirId = ParsePrio1DirectionId();
            if (!prioDirId.HasValue)
                return false;

            var now = DateTime.Now;
            var prioDir = directions.First(d => d.Id == prioDirId.Value);
            var protect = GetProtectedBridgeCluster();

            // Zoek eerst écht conflicterende groene richtingen
            var conflicts = currentGreenDirections
                .Where(d => !protect.Contains(d.Id) && HasConflict(d, prioDir))
                .ToList();

            // Fallback: als er géén conflicts zijn, maar wél groene lichten,
            // pak dan alle niet-protected groene richtingen
            if (!conflicts.Any() && currentGreenDirections.Any())
                conflicts = currentGreenDirections
                    .Where(d => !protect.Contains(d.Id))
                    .ToList();

            // Oranje-fase
            if (conflicts.Any())
            {
                foreach (var d in conflicts)
                {
                    SetDirectionToOrange(d, now);
                    currentGreenDirections.Remove(d);
                }
                SendTrafficLightStates();

                // Kort wachten en dan volle oranje-tijd
                await Task.Delay(100);
                await WaitForOrangeCompletion(conflicts);
            }

            // Oranje → rood
            foreach (var d in conflicts)
                SetDirectionToRed(d);

            // Overige niet-prio, niet-protected → rood
            foreach (var d in directions)
            {
                if (d.Id != prioDirId.Value && !protect.Contains(d.Id))
                    SetDirectionToRed(d);
            }

            // Prio-richting op groen
            SetDirectionToGreen(prioDir, now);

            // Reset en doorsturen
            currentGreenDirections.Clear();
            currentOrangeDirections.Clear();
            currentGreenDirections.Add(prioDir);
            lastSwitchTime = now;
            isOverrideActive = true;
            SendTrafficLightStates();

            return true;
        }

        // GEFIXED: Wacht tot oranje richtingen hun volledige duur hebben gehad
        private async Task WaitForOrangeCompletion(List<Direction> orangeDirections)
        {
            if (!orangeDirections.Any()) return;

            var protect = GetProtectedBridgeCluster();

            while (true)
            {
                var now = DateTime.Now;
                bool allCompleted = true;

                foreach (var dir in orangeDirections)
                {
                    if (protect.Contains(dir.Id)) continue;

                    var orangeStart = orangeStartTimes.GetValueOrDefault(dir.Id, now);
                    var orangeDuration = (now - orangeStart).TotalMilliseconds;

                    if (orangeDuration < ORANGE_DURATION)
                    {
                        allCompleted = false;
                        break;
                    }
                }

                if (allCompleted) break;
                await Task.Delay(100);
            }
        }

        // ========= GECENTRALISEERDE STATE-METHODS =========

        private void SetDirectionToOrange(Direction direction, DateTime time)
        {
            direction.Color = LightColor.Orange;
            orangeStartTimes[direction.Id] = time;
            greenStartTimes.Remove(direction.Id);

            if (!currentOrangeDirections.Contains(direction))
                currentOrangeDirections.Add(direction);
        }

        private void SetDirectionToGreen(Direction direction, DateTime time)
        {
            direction.Color = LightColor.Green;
            greenStartTimes[direction.Id] = time;
            lastGreenTimes[direction.Id] = time;
            orangeStartTimes.Remove(direction.Id);
        }

        private void SetDirectionToRed(Direction direction)
        {
            direction.Color = LightColor.Red;
            currentGreenDirections.Remove(direction);
            currentOrangeDirections.Remove(direction);
            orangeStartTimes.Remove(direction.Id);
            greenStartTimes.Remove(direction.Id);
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

                SendTrafficLightStates();
                await WaitForOrangeCompletion(conflicts);
            }

            foreach (var d in directions.Where(d => !protect.Contains(d.Id)))
                SetDirectionToRed(d);

            SetDirectionToGreen(prioDir, DateTime.Now);
            currentGreenDirections.Clear();
            currentOrangeDirections.Clear();
            currentGreenDirections.Add(prioDir);
            lastSwitchTime = DateTime.Now;
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

            SendTrafficLightStates();
            await WaitForOrangeCompletion(currentOrangeDirections.Where(d => !protect.Contains(d.Id)).ToList());

            foreach (var d in directions.Where(d => !protect.Contains(d.Id)))
                SetDirectionToRed(d);

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

        private int? ParsePrio1DirectionId()
        {
            if (string.IsNullOrEmpty(communicator.PriorityVehicleData))
                return null;
            try
            {
                var prioData = JsonConvert.DeserializeObject<
                    Dictionary<string, List<Dictionary<string, object>>>>(
                        communicator.PriorityVehicleData);
                var queue = prioData?["queue"];
                var veh = queue?.FirstOrDefault(v => v["prioriteit"].ToString() == "1");
                if (veh != null
                    && veh.TryGetValue("baan", out var lane)
                    && int.TryParse(lane.ToString().Split('.')[0], out int id))
                    return id;
            }
            catch { }
            return null;
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

            var toCleanup = currentOrangeDirections.ToList();
            foreach (var d in toCleanup)
                SetDirectionToRed(d);

            var protect = GetProtectedBridgeCluster();
            var bridgeIds = bridgeController.GetBridgeIntersectionSet();
            var avail = directions
                .Where(d =>
                    GetPriority(d) > 0 &&
                    !protect.Contains(d.Id) &&
                    !(bridgeIds.Contains(d.Id) && bridgeController.CurrentBridgeState != "dicht") &&
                    !(jamEngaged && JAM_BLOCK_DIRECTIONS.Contains(d.Id)))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id)
                .ToList();

            var pick = new List<Direction>();
            foreach (var d in avail)
                if (!pick.Any(x => HasConflict(x, d)))
                    pick.Add(d);

            foreach (var g in currentGreenDirections.ToList())
                if (!protect.Contains(g.Id))
                    SetDirectionToRed(g);

            var now = DateTime.Now;
            currentGreenDirections = currentGreenDirections.Where(g => protect.Contains(g.Id)).ToList();

            foreach (var g in pick)
            {
                SetDirectionToGreen(g, now);
                currentGreenDirections.Add(g);
            }

            lastSwitchTime = now;
        }

        private static bool HasConflict(Direction a, Direction b)
        {
            return a.Intersections.Contains(b.Id) || b.Intersections.Contains(a.Id);
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
