using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using stoplicht_controller.Classes;

namespace stoplicht_controller.Managers
{
    public class SpecialSensorDataProcessor
    {
        private Communicator communicator;
        private Bridge bridge;

        public SpecialSensorDataProcessor(Communicator communicator, Bridge bridge)
        {
            this.communicator = communicator;
            this.bridge = bridge;
        }

        public void SpecialSensorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                string rawData = communicator.SpecialSensorData;
                if (string.IsNullOrEmpty(rawData))
                {
                    // Console.WriteLine("Geen speciale sensor data ontvangen.");
                }
                else
                {
                    var data = JsonConvert.DeserializeObject<Dictionary<string, bool>>(rawData);
                    if (data != null)
                    {
                        bridge.TrafficJamNearBridge = data.ContainsKey("brug_file") && data["brug_file"];
                        bridge.VehicleOnBridge = data.ContainsKey("brug_wegdek") && data["brug_wegdek"];
                        bridge.VesselUnderBridge = data.ContainsKey("brug_water") && data["brug_water"];
                    }
                }
                Thread.Sleep(100);
            }
        }
    }
}
