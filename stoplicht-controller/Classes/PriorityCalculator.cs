using stoplicht_controller.Classes;
using stoplicht_controller.Enums;

public class PriorityCalculator : IPriorityCalculator
{
    public int GetPriority(Direction direction)
    {
        int priority = 0;
        foreach (var tl in direction.TrafficLights)
        {
            bool front = tl.Sensors.Any(s => s.Position == SensorPosition.Front && s.IsActivated);
            bool back = tl.Sensors.Any(s => s.Position == SensorPosition.Back && s.IsActivated);
            priority += (front && back) ? 5 : (front || back ? 1 : 0);
        }
        return priority;
    }
}
