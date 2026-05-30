using MowIT.Domain.Enums;

namespace MowIT.Application.Pipeline;

public class RobotCommandContext
{
    public RobotAction? Action     { get; set; }
    public byte         Param      { get; set; }
    public float        LinearVel  { get; set; }
    public float        AngularVel { get; set; }

    public bool    IsAborted    { get; private set; }
    public string? AbortReason  { get; private set; }

    public bool IsMotorCommand => Action is null;

    public void Abort(string reason)
    {
        IsAborted   = true;
        AbortReason = reason;
    }
}
