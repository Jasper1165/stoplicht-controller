using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;
using stoplicht_controller.Managers;

namespace stoplicht_controller.Managers
{
    /// <summary>
    /// Manages incoming priority vehicle data to adjust traffic lights.
    /// Handles both priority-1 (immediate override) and priority-2 (green extension) vehicles.
    /// </summary>
    public class PriorityVehicleManager
    {
        private readonly Communicator communicator;                   // Source of priority vehicle JSON data
        private readonly List<Direction> directions;                  // All traffic directions
        private readonly TrafficLightController trafficLightController; // Controller to override signals
        private readonly HashSet<int> protectedBridgeCluster;         // Directions to exclude (bridge cluster)

        // Currently active priority vehicles list
        private List<PriorityVehicle> activePriorityVehicles = new();

        // Track the single active priority-1 vehicle, if any
        private PriorityVehicle? activePrio1 = null;
        public bool HasActivePrio1 => activePrio1 != null;           // Indicates if an override is active

        /// <summary>
        /// Initializes the manager with communicator, direction list, and traffic controller.
        /// Also retrieves protected bridge cluster and registers back-reference.
        /// </summary>
        public PriorityVehicleManager(
            Communicator communicator,
            List<Direction> directions,
            TrafficLightController trafficLightController)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.trafficLightController = trafficLightController;

            // Get bridge-related directions (and their intersections) to protect
            this.protectedBridgeCluster = trafficLightController.ProtectedBridgeCluster;

            // Link back to the traffic controller for priority callbacks
            this.trafficLightController.SetPriorityManager(this);
        }

        /// <summary>
        /// Periodically called to parse incoming JSON, update the active queue,
        /// and process priority-1 and priority-2 vehicles.
        /// </summary>
        public void Update()
        {
            if (string.IsNullOrEmpty(communicator.PriorityVehicleData))
                return;

            try
            {
                // Deserialize the incoming queue of priority vehicles
                var priorityData = JsonConvert.DeserializeObject<PriorityVehicleQueue>(
                    communicator.PriorityVehicleData);

                // If no vehicles remain, clear any active override
                if (priorityData?.Queue == null || !priorityData.Queue.Any())
                {
                    if (HasActivePrio1)
                        ClearPrio1();
                    activePriorityVehicles.Clear();
                    return;
                }

                // Save previous state and update list
                var previousPriorityVehicles = new List<PriorityVehicle>(activePriorityVehicles);
                activePriorityVehicles = priorityData.Queue;

                // Handle arrivals and departures of priority-1 and priority-2 vehicles
                ProcessPrio1Vehicles();
                ProcessPrio2Vehicles();
                CheckForRemovedVehicles(previousPriorityVehicles);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing priority vehicle data: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds and activates the earliest priority-1 vehicle not in the bridge cluster.
        /// Clears previous override if a new vehicle arrives.
        /// </summary>
        private void ProcessPrio1Vehicles()
        {
            // Gather all priority-1 vehicles in FIFO order
            var prio1Vehicles = activePriorityVehicles
                .Where(v => v.Priority == 1)
                .OrderBy(v => v.SimulationTimeMs)
                .ToList();

            // Exclude any directions that belong to the protected bridge cluster
            prio1Vehicles = prio1Vehicles
                .Where(v =>
                {
                    var dir = GetDirectionFromLane(v.Lane);
                    return dir != null && !protectedBridgeCluster.Contains(dir.Id);
                })
                .ToList();

            // No valid prio-1 vehicles: clear existing override if active
            if (!prio1Vehicles.Any())
            {
                if (HasActivePrio1)
                    ClearPrio1();
                return;
            }

            var firstPrio1 = prio1Vehicles.First();

            // If a different prio-1 vehicle has arrived, restart override
            if (activePrio1 != null && activePrio1.Lane != firstPrio1.Lane)
            {
                ClearPrio1();
                ActivatePrio1(firstPrio1);
            }
            // Otherwise, if no override active yet, activate the first vehicle
            else if (activePrio1 == null)
            {
                ActivatePrio1(firstPrio1);
            }
        }

        /// <summary>
        /// Prepares priority-2 vehicles for green time boosting in the traffic controller.
        /// Actual boosting logic is in TrafficLightController.GetEffectivePriority().
        /// Skipped if a priority-1 override is active.
        /// </summary>
        private void ProcessPrio2Vehicles()
        {
            if (HasActivePrio1)
                return; // Do not boost if a prio-1 override is active

            var prio2Vehicles = activePriorityVehicles
                .Where(v => v.Priority == 2)
                .OrderBy(v => v.SimulationTimeMs)
                .ToList();

            // Exclude bridge cluster directions as well
            prio2Vehicles = prio2Vehicles
                .Where(v =>
                {
                    var dir = GetDirectionFromLane(v.Lane);
                    return dir != null && !protectedBridgeCluster.Contains(dir.Id);
                })
                .ToList();

            // If none remain, nothing further to do
            if (!prio2Vehicles.Any())
                return;

            // No direct action here; boosting occurs during light updates
        }

        /// <summary>
        /// Activates override to give the specified priority-1 vehicle a single green light.
        /// </summary>
        private void ActivatePrio1(PriorityVehicle vehicle)
        {
            var direction = GetDirectionFromLane(vehicle.Lane);
            if (direction == null)
                return;

            // Do not override on the protected bridge directions
            if (direction.Id == 71 || direction.Id == 72)
                return;

            trafficLightController.OverrideWithSingleGreen(direction.Id);
            activePrio1 = vehicle;
        }

        /// <summary>
        /// Clears any active priority-1 override, returning to normal cycle.
        /// </summary>
        private void ClearPrio1()
        {
            trafficLightController.ClearOverride();
            activePrio1 = null;
        }

        /// <summary>
        /// Detects when an active prio-1 vehicle has left the queue and clears override.
        /// </summary>
        private void CheckForRemovedVehicles(List<PriorityVehicle> previousVehicles)
        {
            if (activePrio1 != null &&
                !activePriorityVehicles.Any(v => v.Lane == activePrio1.Lane && v.Priority == 1))
            {
                ClearPrio1();
            }
        }

        /// <summary>
        /// Parses a lane string ("directionId.laneId") and returns the matching Direction.
        /// </summary>
        private Direction? GetDirectionFromLane(string lane)
        {
            if (!lane.Contains('.'))
                return null;

            var parts = lane.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int directionId))
                return null;

            return directions.FirstOrDefault(d => d.Id == directionId);
        }
    }
}
