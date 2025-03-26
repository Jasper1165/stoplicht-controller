
using System;
using stoplicht_controller.Enums;
namespace stoplicht_controller.Classes
{
	public class Direction
	{
		public string Id { get; set; }

		public LightColor TrafficLightState { get; set; }
		public List<int> IntersectsWith { get; set; }
		public List<TrafficLight> trafficLights { get; set; }
	}
}

