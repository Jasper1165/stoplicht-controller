using System;
namespace stoplicht_controller.Classes
{
	public class TrafficLight
	{
        // De Id wordt hier gedefinieerd als een integer; we berekenen hem door group en lane te combineren
        public string Id { get; set; }
        public List<Sensor> Sensors { get; set; } = new List<Sensor>();
	}
}

