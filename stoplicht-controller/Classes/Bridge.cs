using System;
using Newtonsoft.Json;
using stoplicht_controller.Enums;
using stoplicht_controller.Managers;
namespace stoplicht_controller.Classes
{
	public class Bridge
	{
		public bool VehicleOnBridge { get; set; }
		public bool VesselUnderBridge { get; set; }
		public bool TrafficJamNearBridge { get; set; }
	}
}

