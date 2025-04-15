
using System;
using stoplicht_controller.Enums;
namespace stoplicht_controller.Classes
{
	public class Direction
        {
                public int Id { get; set; }
                public LightColor Color { get; set; } = LightColor.Red;
                public List<int> Intersections { get; set; } = new List<int>();
                public List<TrafficLight> TrafficLights { get; set; } = new List<TrafficLight>();



                public int? Priority { get; set; } = null;

        }
}

