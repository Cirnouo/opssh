using Opssh.Tray;

internal static class ActivityWheelChecks
{
    internal static void Run()
    {
        double command = 50, table = 200;
        bool MoveCommand(int delta) => ActivityWheel.Move(delta, command, 75, offset => { command = offset; return true; });
        bool MoveTable(int delta) => ActivityWheel.Move(delta, table, 300, offset => { table = offset; return true; });

        Require(ActivityWheel.Route(120, MoveCommand, MoveTable) && command == 75 && table == 200,
            "the command consumes the event that reaches its right edge");
        Require(!ActivityWheel.Route(120, MoveCommand, MoveTable) && command == 75 && table == 300,
            "the next rightward event moves the table");
        Require(ActivityWheel.Route(-120, MoveCommand, MoveTable) && command == 0 && table == 300,
            "the command consumes the event that reaches its left edge");
        Require(!ActivityWheel.Route(-120, MoveCommand, MoveTable) && command == 0 && table == 180,
            "the next leftward event moves the table");
        Require(!ActivityWheel.Route(-120, delta => ActivityWheel.Move(delta, 0, 0, _ => true), MoveTable) && table == 60,
            "a fitting command never claims the event");
        Require(!ActivityWheel.Route(-120, delta => ActivityWheel.Move(delta, 50, 75, _ => false), MoveTable) && table == 0,
            "an unaccepted command scroll leaves the indicator hidden");
    }

    private static void Require(bool condition, string expectation)
    {
        if (!condition)
            throw new InvalidOperationException("Activity wheel check failed: " + expectation);
    }
}
