using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using stoplicht_controller.Classes;
using stoplicht_controller.Enums;
using stoplicht_controller.Managers;

namespace stoplicht_controller.Managers
{
    public class PriorityVehicleManager
    {
        private readonly Communicator communicator;
        private readonly List<Direction> directions;
        private readonly TrafficLightController trafficLightController;
        private readonly HashSet<int> protectedBridgeCluster;

        // Store processed priority vehicles to track changes
        private List<PriorityVehicle> activePriorityVehicles = new();

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

            // Retrieve the bridge-cluster (incl. intersections) from controller
            this.protectedBridgeCluster = trafficLightController.ProtectedBridgeCluster;

            // Break circular dependency
            this.trafficLightController.SetPriorityManager(this);
        }

        public void Update()
        {
            if (string.IsNullOrEmpty(communicator.PriorityVehicleData))
                return;

            try
            {
                var priorityData = JsonConvert.DeserializeObject<PriorityVehicleQueue>(
                    communicator.PriorityVehicleData);

                if (priorityData?.Queue == null || !priorityData.Queue.Any())
                {
                    if (HasActivePrio1)
                        ClearPrio1();
                    activePriorityVehicles.Clear();
                    return;
                }

                var previousPriorityVehicles = new List<PriorityVehicle>(activePriorityVehicles);
                activePriorityVehicles = priorityData.Queue;

                ProcessPrio1Vehicles();
                ProcessPrio2Vehicles();
                CheckForRemovedVehicles(previousPriorityVehicles);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing priority vehicle data: {ex.Message}");
            }
        }

        private void ProcessPrio1Vehicles()
        {
            // FIFO-list of prio-1 vehicles
            var prio1Vehicles = activePriorityVehicles
                .Where(v => v.Priority == 1)
                .OrderBy(v => v.SimulationTimeMs)
                .ToList();

            // Exclude bridge-related directions (and their intersections)
            prio1Vehicles = prio1Vehicles
                .Where(v =>
                {
                    var dir = GetDirectionFromLane(v.Lane);
                    return dir != null && !protectedBridgeCluster.Contains(dir.Id);
                })
                .ToList();

            if (!prio1Vehicles.Any())
            {
                if (HasActivePrio1)
                    ClearPrio1();
                return;
            }

            var firstPrio1 = prio1Vehicles.First();

            if (activePrio1 != null && activePrio1.Lane != firstPrio1.Lane)
            {
                ClearPrio1();
                ActivatePrio1(firstPrio1);
            }
            else if (activePrio1 == null)
            {
                ActivatePrio1(firstPrio1);
            }
        }

        private void ProcessPrio2Vehicles()
        {
            // Skip if prio1 is active
            if (HasActivePrio1) return;

            var prio2Vehicles = activePriorityVehicles
                .Where(v => v.Priority == 2)
                .OrderBy(v => v.SimulationTimeMs)
                .ToList();

            // Exclude bridge-related directions as well
            prio2Vehicles = prio2Vehicles
                .Where(v =>
                {
                    var dir = GetDirectionFromLane(v.Lane);
                    return dir != null && !protectedBridgeCluster.Contains(dir.Id);
                })
                .ToList();

            if (!prio2Vehicles.Any()) return;

            // Priority-2 boosting handled in TrafficLightController.GetEffectivePriority()
        }

        private void ActivatePrio1(PriorityVehicle vehicle)
        {
            var direction = GetDirectionFromLane(vehicle.Lane);
            if (direction == null) return;

            if (direction.Id == 71 || direction.Id == 72) return;

            trafficLightController.OverrideWithSingleGreen(direction.Id);
            activePrio1 = vehicle;
        }

        private void ClearPrio1()
        {
            trafficLightController.ClearOverride();
            activePrio1 = null;
        }

        private void CheckForRemovedVehicles(List<PriorityVehicle> previousVehicles)
        {
            if (activePrio1 != null &&
                !activePriorityVehicles.Any(v => v.Lane == activePrio1.Lane && v.Priority == 1))
            {
                ClearPrio1();
            }
        }

        private Direction? GetDirectionFromLane(string lane)
        {
            if (!lane.Contains('.')) return null;

            var parts = lane.Split('.');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int directionId))
                return null;

            return directions.FirstOrDefault(d => d.Id == directionId);
        }
    }
}