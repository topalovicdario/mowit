namespace MowIT.Domain.Enums;

public enum RobotState : byte
{
    Idle              = 0,
    Mowing            = 1,
    Paused            = 2,
    Returning         = 3,
    Docking           = 4,
    Charging          = 5,
    Leaving           = 6,
    RecordingBoundary = 7,
    Error             = 255
}
