using System;
using stoplicht_controller.Enums;
namespace stoplicht_controller.Classes
{
	public class Sensor
	{
        public SensorPosition Position { get; set; } = SensorPosition.Back;
        public bool IsActivated { get; set; }
        public string SensorName { get; set; }
	}
}

