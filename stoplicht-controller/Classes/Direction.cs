
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

                public Dictionary<string, List<TransitionCondition>> TransitionRequirements { get; set; }
                        = new Dictionary<string, List<TransitionCondition>>();

                // transition_blockers definieert welke condities de overgang (bijvoorbeeld van rood naar groen) blokkeren.
                public Dictionary<string, List<TransitionCondition>> TransitionBlockers { get; set; }
                = new Dictionary<string, List<TransitionCondition>>();

        }
}

