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
        // Configuratie-constanten
        private const int ORANGE_DURATION = 2000;         // 2 seconden
        private const int DEFAULT_GREEN_DURATION = 8000;    // 8 seconden
        private const int SHORT_GREEN_DURATION = 3000;      // 3 seconden
        private const int PRIORITY_THRESHOLD = 3;
        private const int HIGH_PRIORITY_THRESHOLD = 6;
        private const double AGING_SCALE_SECONDS = 7;       // per 7 seconden wachtbonus

        private DateTime lastSwitchTime = DateTime.Now;
        private DateTime lastOrangeTime = DateTime.Now;
        private List<Direction> currentGreenDirections = new List<Direction>();
        private List<Direction> currentOrangeDirections = new List<Direction>();
        private static Dictionary<int, DateTime> lastGreenTimes = new Dictionary<int, DateTime>();

        private bool isHandlingPriorityVehicle = false;
        private Communicator communicator;
        private List<Direction> directions;

        public TrafficLightController(Communicator communicator, List<Direction> directions)
        {
            this.communicator = communicator;
            this.directions = directions;
        }

        // Wordt bij opstarten aangeroepen zodat voor elke richting de lastGreenTime wordt ingesteld.
        public static void InitializeLastGreenTimes(List<Direction> directions)
        {
            foreach (var direction in directions)
                lastGreenTimes[direction.Id] = DateTime.Now;
        }

        public async Task TrafficLightCycleLoop(CancellationToken token)
        {
            // Initiele schakeling
            if (directions.Any())
            {
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

        public void UpdateTrafficLights()
        {
            if (isHandlingPriorityVehicle)
                return;

            DateTime now = DateTime.Now;
            double timeSinceGreen = (now - lastSwitchTime).TotalMilliseconds;
            double timeSinceOrange = (now - lastOrangeTime).TotalMilliseconds;

            if (currentOrangeDirections.Any())
            {
                if (timeSinceOrange >= ORANGE_DURATION)
                {
                    SetLightsToRed();
                    SwitchTrafficLights();
                    SendTrafficLightStates();
                }
                return;
            }

            if (currentGreenDirections.Any())
            {
                int sumEffectivePriority = currentGreenDirections.Sum(d => GetEffectivePriority(d));
                int dynamicGreenDuration = sumEffectivePriority >= HIGH_PRIORITY_THRESHOLD ? DEFAULT_GREEN_DURATION + 2000 :
                                           sumEffectivePriority < PRIORITY_THRESHOLD ? SHORT_GREEN_DURATION : DEFAULT_GREEN_DURATION;

                if (timeSinceGreen >= dynamicGreenDuration)
                {
                    SetLightsToOrange();
                    lastOrangeTime = DateTime.Now;
                    SendTrafficLightStates();
                }
                else
                {
                    var extraCandidates = GetExtraGreenCandidates();
                    if (extraCandidates.Any())
                    {
                        foreach (var extra in extraCandidates)
                        {
                            currentGreenDirections.Add(extra);
                            extra.Color = LightColor.Green;
                            lastGreenTimes[extra.Id] = DateTime.Now;
                            Console.WriteLine($"Extra richting {extra.Id} toegevoegd met effectieve prioriteit {GetEffectivePriority(extra)}.");
                        }
                        lastSwitchTime = DateTime.Now;
                        SendTrafficLightStates();
                    }
                }
                return;
            }

            // Geen groene richtingen? Probeer te schakelen.
            if (!currentGreenDirections.Any())
            {
                SwitchTrafficLights();
                SendTrafficLightStates();
            }
        }

        private List<Direction> GetExtraGreenCandidates()
        {
            var candidates = directions
                .Where(d => GetPriority(d) > 0 && !currentGreenDirections.Contains(d))
                .OrderByDescending(d => GetEffectivePriority(d))
                .ThenBy(d => d.Id)
                .ToList();

            var extraCandidates = new List<Direction>();
            foreach (var candidate in candidates)
            {
                if (!currentGreenDirections.Concat(extraCandidates).Any(green => HasConflict(green, candidate)))
                    extraCandidates.Add(candidate);
            }
            return extraCandidates;
        }

        private void SwitchTrafficLights()
        {
            var availableDirections = directions
                .Where(d => GetPriority(d) > 0)
                .OrderByDescending(d => GetEffectivePriority(d))
                .ThenBy(d => d.Id)
                .ToList();

            if (!availableDirections.Any())
            {
                Console.WriteLine("Geen beschikbare richtingen gevonden voor groen.");
                return;
            }

            var newGreenGroup = new List<Direction>();
            foreach (var candidate in availableDirections)
            {
                if (!newGreenGroup.Any(green => HasConflict(green, candidate)))
                    newGreenGroup.Add(candidate);
            }

            if (!newGreenGroup.Any())
            {
                Console.WriteLine("Geen niet-conflicterende richtingen gevonden.");
                return;
            }

            if (newGreenGroup.SequenceEqual(currentGreenDirections))
            {
                Console.WriteLine("Groene groep blijft ongewijzigd.");
                return;
            }

            // Zet oude groene richtingen op rood.
            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Red;
                Console.WriteLine($"Richting {dir.Id} staat op rood.");
            }
            currentGreenDirections.Clear();

            currentGreenDirections = newGreenGroup;
            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Green;
                lastGreenTimes[dir.Id] = DateTime.Now;
                Console.WriteLine($"Richting {dir.Id} staat op groen met effectieve prioriteit {GetEffectivePriority(dir)}.");
            }
            lastSwitchTime = DateTime.Now;
        }

        private bool HasConflict(Direction d1, Direction d2)
        {
            if (!d1.Intersections.Any() || !d2.Intersections.Any())
                return false;
            return d1.Intersections.Contains(d2.Id) || d2.Intersections.Contains(d1.Id);
        }

        private void SetLightsToOrange()
        {
            foreach (var dir in currentGreenDirections)
            {
                dir.Color = LightColor.Orange;
                Console.WriteLine($"Richting {dir.Id} staat op oranje.");
            }
            currentOrangeDirections = new List<Direction>(currentGreenDirections);
            currentGreenDirections.Clear();
        }

        private void SetLightsToRed()
        {
            foreach (var dir in currentOrangeDirections)
            {
                dir.Color = LightColor.Red;
                Console.WriteLine($"Richting {dir.Id} staat op rood.");
            }
            currentOrangeDirections.Clear();
        }

        private int GetPriority(Direction direction)
        {
            int priority = 0;
            foreach (var trafficLight in direction.TrafficLights)
            {
                bool front = trafficLight.Sensors.Any(s => s.Position == SensorPosition.Front && s.IsActivated);
                bool back = trafficLight.Sensors.Any(s => s.Position == SensorPosition.Back && s.IsActivated);
                priority += front && back ? 5 : (front || back ? 1 : 0);
            }
            return priority;
        }

        private int GetEffectivePriority(Direction direction)
        {
            int basePriority = GetPriority(direction);
            DateTime lastGreen = lastGreenTimes.ContainsKey(direction.Id) ? lastGreenTimes[direction.Id] : DateTime.Now;
            int agingBonus = (int)((DateTime.Now - lastGreen).TotalSeconds / AGING_SCALE_SECONDS);
            return basePriority + agingBonus;
        }

        private void ProcessSensorMessage()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData))
                return;
            Dictionary<string, Dictionary<string, bool>> sensorData = null;
            try
            {
                sensorData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
            }
            catch (JsonReaderException ex)
            {
                Console.WriteLine($"Fout bij het deserialiseren van sensor data: {ex.Message}");
                return;
            }
            if (sensorData == null)
            {
                Console.WriteLine("Sensor data is null.");
                return;
            }

            foreach (var (trafficLightId, sensors) in sensorData)
            {
                var trafficLight = directions.SelectMany(d => d.TrafficLights)
                                             .FirstOrDefault(tl => tl.Id == trafficLightId);
                if (trafficLight == null)
                    continue;

                foreach (var sensor in trafficLight.Sensors)
                {
                    if (sensor.Position == SensorPosition.Front && sensors.TryGetValue("voor", out bool frontValue))
                        sensor.IsActivated = frontValue;
                    else if (sensor.Position == SensorPosition.Back && sensors.TryGetValue("achter", out bool backValue))
                        sensor.IsActivated = backValue;
                }
            }
        }

        private void SendTrafficLightStates()
        {
            if (string.IsNullOrEmpty(communicator.LaneSensorData))
                return;

            var sensorData = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, bool>>>(communicator.LaneSensorData);
            if (sensorData == null)
                return;

            var stateDict = sensorData.Keys
                .Select(tlId => directions.SelectMany(d => d.TrafficLights)
                                            .FirstOrDefault(tl => tl.Id == tlId))
                .Where(tl => tl != null)
                .Select(tl => new
                {
                    Id = tl.Id,
                    State = directions.FirstOrDefault(d => d.TrafficLights.Contains(tl))?.Color == LightColor.Green ? "groen" :
                            directions.FirstOrDefault(d => d.TrafficLights.Contains(tl))?.Color == LightColor.Orange ? "oranje" : "rood"
                })
                .ToDictionary(x => x.Id, x => x.State);

            communicator.PublishMessage("stoplichten", stateDict);
        }
    }
}
