using stoplicht_controller.Classes;

public interface IPriorityCalculator
{
    int GetPriority(Direction direction);
}
