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
        //  ► EVENT VOOR STATE‐VERANDERINGEN
        // ───────────────────────────────────────────────────────────────
        /// <summary>
        /// Wordt gevuurd zodra SendBridgeStates wordt aangeroepen,
        /// dus elk moment dat we een nieuwe gecombineerde state willen publiceren.
        /// </summary>
        public event Action StateChanged;

        // ───────────────────────────────────────────────────────────────
        //  ► CONFIG CONSTANTS  ─ timings zijn in milliseconden tenzij anders
        // ───────────────────────────────────────────────────────────────
        private const int BRIDGE_GREEN_DURATION = 20_000;  // boot pass A/B
        private const int BRIDGE_ORANGE_DURATION = 10_000;
        private const int POST_BRIDGE_NORMAL_PHASE_MS = 30_000;  // traffic free-flow
        private const int BRIDGE_COOLDOWN_SECONDS = 180_000;      // min gap between sessions
        private const int SAFETY_CHECK_INTERVAL = 1_000;   // polling
        private const int BRIDGE_SWITCH_EXTRA_DELAY_MS = 10_000;  // ship still under bridge

        // ───────────────────────────────────────────────────────────────
        //  ► RUNTIME FIELDS
        // ───────────────────────────────────────────────────────────────
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly Bridge bridge;
        private HashSet<int> activeConflictDirections = new HashSet<int>();
        private readonly Dictionary<string, string> persistentPayload = new Dictionary<string, string>();
        public bool IsHandlingPriority { get; private set; }
        private readonly object bridgeLock = new();
        private CancellationTokenSource bridgeCts;
        private Task bridgeTask;
        private bool isBridgeCycleRunning;

        public readonly int bridgeDirectionA = 71;   // north side
        public readonly int bridgeDirectionB = 72;   // south side

        private bool bridgeUsedThisCycle;
        private bool postBridgeNormalPhaseActive;
        private DateTime postBridgePhaseStartTime;
        private DateTime lastBridgeClosedTime = DateTime.MinValue;
        private readonly Dictionary<string, string> sharedPayload;
        private string currentBridgeState = "rood";    // software state of TL 81.1
        private string physicalBridgeState = "dicht";  // sensor feedback

        // Bridge traffic light ID (hardcoded)
        public const string BRIDGE_LIGHT_ID = "81.1";

        // ───────────────────────────────────────────────────────────────
        //  ► PUBLIC API
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets the current software state of the bridge traffic light.
        /// </summary>
        public string CurrentBridgeState => currentBridgeState;

        /// <summary>
        /// Retrieves een set met álle richtingen die bij de brug horen
        /// (brug‐zijden én hun kruisende richtingen).
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
        /// Nood‐override voor een boot: zet beide brugrichtingen meteen op rood,
        /// wacht op fysieke sluiting, en opent dan de oversteeklichten.
        /// </summary>
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
        /// Initialisatie ctor.
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
        /// Zet de start‐state van de brug (rood) en laat
        /// kruisende lichten groen zijn.
        /// </summary>
        private void SetInitialBridgeState()
        {
            var dirA = directions.FirstOrDefault(d => d.Id == bridgeDirectionA);
            var dirB = directions.FirstOrDefault(d => d.Id == bridgeDirectionB);

            currentBridgeState = "rood";
            if (dirA != null) dirA.Color = LightColor.Red;
            if (dirB != null) dirB.Color = LightColor.Red;

            // OpenConflicts(dirA);
            // OpenConflicts(dirB);
            ChangeCrossingTrafficLights(LightColor.Green);
        }

        /// <summary>
        /// Verwerkt inkomende sensor‐data om de fysieke state bij te werken.
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
        /// Reset de cycle als de post‐bridge vrije‐door‐phase is verlopen.
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
        /// Hoofd‐update: eerst priority vehicles, anders nieuwe brug‐sessie.
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
        /// Checkt op priority‐1 voertuigen in de queue.
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
        /// Lost priority‐1 voertuigen op, vergelijkbaar met de normale sessie maar korter.
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
                    // OpenConflicts(null); // clear conflicts
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
                    // OpenConflicts(null);
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
        /// Volledige brug‐sessie (groen/oranje/rood + boten doorlaten).
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

        private async Task WaitUntilNoBridgeVehicle(CancellationToken token)
        {
            int retries = 0, max = 60;
            while (bridge.VehicleOnBridge && retries++ < max)
                await Task.Delay(SAFETY_CHECK_INTERVAL, token);
        }

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

        private async Task WaitForPhysicalBridgeState(string target, CancellationToken token)
        {
            int retries = 0, max = 240;
            while (physicalBridgeState != target && retries++ < max)
                await Task.Delay(SAFETY_CHECK_INTERVAL, token);
        }

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
        /// Publish alle huidige staten in één go.
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
            // Console.WriteLine($"Bridge state: {currentBridgeState}");

            // ** NU het event om te publiceren **
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Zet alle kruisende lichten op de opgegeven kleur.
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

            // event vuuren zodat StatePublisher direct pusht
            StateChanged?.Invoke();
        }
    }
}
