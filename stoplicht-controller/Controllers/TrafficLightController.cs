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
        // ================================
        //       CONFIG CONSTANTS
        // ================================
        private const int ORANGE_DURATION = 3000;
        private const int DEFAULT_GREEN_DURATION = 6000;
        private const int SHORT_GREEN_DURATION = 3000;
        private const int PRIORITY_THRESHOLD = 3;
        private const int HIGH_PRIORITY_THRESHOLD = 6;
        private const double AGING_SCALE_SECONDS = 7;

        // ================================
        //       RUNTIME FIELDS
        // ================================
        private DateTime lastSwitchTime = DateTime.Now;
        private DateTime lastOrangeTime = DateTime.Now;

        private List<Direction> currentGreenDirections = new List<Direction>();
        private List<Direction> currentOrangeDirections = new List<Direction>();
        private static Dictionary<int, DateTime> lastGreenTimes = new Dictionary<int, DateTime>();
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly BridgeController bridgeController;

        public TrafficLightController(Communicator communicator, List<Direction> directions, Bridge bridge)
        {
            this.communicator = communicator;
            this.directions = directions;
            InitializeLastGreenTimes(directions);
            bridgeController = new BridgeController(communicator, directions, bridge);
        }

        public static void InitializeLastGreenTimes(List<Direction> dirs)
        {
            foreach (var d in dirs) lastGreenTimes[d.Id] = DateTime.Now;
        }

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
                UpdateTrafficLights();
                await Task.Delay(500, token);
            }
        }

        public void UpdateTrafficLights()
        {
            DateTime now = DateTime.Now;
            double sinceG = (now - lastSwitchTime).TotalMilliseconds;
            double sinceO = (now - lastOrangeTime).TotalMilliseconds;

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

            SwitchTrafficLights();
            SendTrafficLightStates();
        }

        private List<Direction> GetExtraGreenCandidates()
        {
            var blocked = bridgeController.GetBridgeIntersectionSet();

            return directions
                .Where(d => GetPriority(d) > 0 && !currentGreenDirections.Contains(d) && !blocked.Contains(d.Id))
                .OrderByDescending(GetEffectivePriority)
                .ThenBy(d => d.Id)
                .Where(d => !currentGreenDirections.Any(g => HasConflict(g, d)))
                .ToList();
        }

        private void SwitchTrafficLights()
        {
            var blocked = bridgeController.GetBridgeIntersectionSet();

            var avail = directions
                .Where(d => GetPriority(d) > 0 && !blocked.Contains(d.Id))
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

        private bool HasConflict(Direction a, Direction b)
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
            return baseP + aging;
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
                    var tl = directions.SelectMany(d => d.TrafficLights).FirstOrDefault(t => t.Id == kv.Key);
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
            catch { /* swallow */ }
        }

        private void SendTrafficLightStates()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData)) return;
            var data = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
            if (data == null) return;

            var dict = data.Keys.ToDictionary(
                id => id,
                id =>
                {
                    var tl = directions.SelectMany(d => d.TrafficLights).First(t => t.Id == id);
                    var dir = directions.First(d => d.TrafficLights.Contains(tl));
                    return dir.Color == LightColor.Green ? "groen"
                         : dir.Color == LightColor.Orange ? "oranje"
                         : "rood";
                });

            dict["81.1"] = bridgeController.CurrentBridgeState;
            communicator.PublishMessage("stoplichten", dict);
        }
    }
}
