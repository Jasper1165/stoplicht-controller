// PriorityVehicleManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

namespace stoplicht_controller.Managers
{
    public class PriorityVehicleManager
    {
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly TrafficLightController trafficLightController;

        // Store processed priority vehicles to track changes
        private List<PriorityVehicle> activePriorityVehicles = new();
        private Dictionary<string, DateTime> lastProcessedTimeByLane = new();

        // Keep track of currently active priority 1 vehicle (if any)
        private PriorityVehicle? activePrio1 = null;

        public bool HasActivePrio1 => activePrio1 != null;

        public PriorityVehicleManager(
            Communicator communicator,
            List<Direction> directions,
            TrafficLightController trafficLightController)
        {
            this.communicator = communicator;
            this.directions = directions;
            this.trafficLightController = trafficLightController;

            // Break circular dependency
            this.trafficLightController.SetPriorityManager(this);
        }

        public void Update()
        {
            if (string.IsNullOrEmpty(communicator.PriorityVehicleData))
                return;

            try
            {
                // Parse the priority vehicle data
                var priorityData = JsonConvert.DeserializeObject<PriorityVehicleQueue>(
                    communicator.PriorityVehicleData);

                if (priorityData == null || priorityData.Queue == null || !priorityData.Queue.Any())
                {
                    // No priority vehicles in queue - clear any active priority status
                    if (HasActivePrio1)
                    {
                        ClearPrio1();
                    }
                    activePriorityVehicles.Clear();
                    return;
                }

                // Store current state for comparison
                var previousPriorityVehicles = new List<PriorityVehicle>(activePriorityVehicles);
                activePriorityVehicles = priorityData.Queue;

                // Process priority 1 vehicles (emergency vehicles)
                ProcessPrio1Vehicles();

                // Process priority 2 vehicles (buses)
                ProcessPrio2Vehicles();

                // Check if any priority vehicles have been removed from the queue
                CheckForRemovedVehicles(previousPriorityVehicles);
            }
            catch (Exception ex)
            {
                // Swallow exceptions but could log them in a production environment
                Console.WriteLine($"Error processing priority vehicle data: {ex.Message}");
            }
        }

        private void ProcessPrio1Vehicles()
        {
            // Get priority 1 vehicles sorted by simulation time (FIFO)
            var prio1Vehicles = activePriorityVehicles
                .Where(v => v.Priority == 1)
                .OrderBy(v => v.SimulationTimeMs)
                .ToList();

            if (!prio1Vehicles.Any())
            {
                if (HasActivePrio1)
                {
                    ClearPrio1();
                }
                return;
            }

            // Take the first priority 1 vehicle (FIFO)
            var firstPrio1 = prio1Vehicles.First();

            // If we already have an active prio1 and it's different, update
            if (activePrio1 != null && activePrio1.Lane != firstPrio1.Lane)
            {
                ClearPrio1();
                ActivatePrio1(firstPrio1);
            }
            // If no active prio1, activate this one
            else if (activePrio1 == null)
            {
                ActivatePrio1(firstPrio1);
            }
            // Otherwise, the same prio1 is still active, no action needed
        }

        private void ActivatePrio1(PriorityVehicle vehicle)
        {
            // Find the direction associated with this lane
            var direction = GetDirectionFromLane(vehicle.Lane);
            if (direction == null)
                return;

            // Activate priority 1 route
            trafficLightController.OverrideWithSingleGreen(direction.Id);
            activePrio1 = vehicle;
        }

        private void ClearPrio1()
        {
            trafficLightController.ClearOverride();
            activePrio1 = null;
        }

        private void ProcessPrio2Vehicles()
        {
            // We don't process prio2 when a prio1 is active
            if (HasActivePrio1)
                return;

            // Get priority 2 vehicles sorted by simulation time (FIFO)
            var prio2Vehicles = activePriorityVehicles
                .Where(v => v.Priority == 2)
                .OrderBy(v => v.SimulationTimeMs)
                .ToList();

            // No Prio2 vehicles to process
            if (!prio2Vehicles.Any())
                return;

            // For each Prio2 vehicle, increment the priority of its direction
            // This will be handled in the traffic light controller's normal cycle logic
            foreach (var vehicle in prio2Vehicles)
            {
                // Find the direction for this lane
                var direction = GetDirectionFromLane(vehicle.Lane);
                if (direction == null)
                    continue;

                // The priority is already boosted in the TrafficLightController
                // through the GetEffectivePriority method
                // We don't need to do anything else here
            }
        }

        private void CheckForRemovedVehicles(List<PriorityVehicle> previousVehicles)
        {
            // Check if the active Prio1 vehicle is no longer in the queue
            if (activePrio1 != null && !activePriorityVehicles.Any(v =>
                v.Lane == activePrio1.Lane && v.Priority == 1))
            {
                ClearPrio1();
            }
        }

        private Direction? GetDirectionFromLane(string lane)
        {
            // Parse the lane ID to get the direction ID
            // Lane format is usually "DirectionId.LaneNumber"
            if (!lane.Contains('.'))
                return null;

            string[] parts = lane.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int directionId))
                return null;

            // Find the direction with this ID
            return directions.FirstOrDefault(d => d.Id == directionId);
        }
    }
}