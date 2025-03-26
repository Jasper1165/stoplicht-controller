using System;
namespace stoplicht_controller.Classes
{
	public class Lane
	{
		public int Id { get; set; }
		public List<Sensor> Sensors { get; set; } = new List<Sensor>();
	}
}

