using System;
using stoplicht_controller.Enums;

public class Sensor
{
	public SensorPosition Position { get; set; } = SensorPosition.Back;
	public bool IsActivated { get; set; }
}

