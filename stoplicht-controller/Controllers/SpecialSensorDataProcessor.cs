using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using stoplicht_controller.Classes;

namespace stoplicht_controller.Managers
{
    /// <summary>
    /// Processes special sensor messages related to bridge conditions in a continuous loop.
    /// </summary>
    public class SpecialSensorDataProcessor
    {
        private readonly Communicator communicator;  // Communicator to receive sensor data messages
        private readonly Bridge bridge;              // Bridge instance to update with sensor states

        /// <summary>
        /// Constructs the processor with a communicator and bridge reference.
        /// </summary>
        /// <param name="communicator">Source of incoming special sensor JSON data.</param>
        /// <param name="bridge">Bridge object whose properties will be updated.</param>
        public SpecialSensorDataProcessor(Communicator communicator, Bridge bridge)
        {
            this.communicator = communicator;
            this.bridge = bridge;
        }

        /// <summary>
        /// Continuously reads raw JSON from the communicator, deserializes it,
        /// and updates the bridge's jam, vehicle, and vessel flags.
        /// </summary>
        /// <param name="token">Cancellation token to stop the loop gracefully.</param>
        public void SpecialSensorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // Retrieve the latest special sensor JSON string
                string rawData = communicator.SpecialSensorData;
                if (!string.IsNullOrEmpty(rawData))
                {
                    // Parse JSON into a dictionary of sensor flags
                    var data = JsonConvert.DeserializeObject<Dictionary<string, bool>>(rawData);
                    if (data != null)
                    {
                        // Update bridge state flags based on sensor keys
                        bridge.TrafficJamNearBridge = data.ContainsKey("brug_file") && data["brug_file"];
                        bridge.VehicleOnBridge = data.ContainsKey("brug_wegdek") && data["brug_wegdek"];
                        bridge.VesselUnderBridge = data.ContainsKey("brug_water") && data["brug_water"];
                    }
                }

                // Small delay to prevent busy-waiting
                Thread.Sleep(100);
            }
        }
    }
}
