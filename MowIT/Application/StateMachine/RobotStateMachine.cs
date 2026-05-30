using MowIT.Domain.Enums;
using MowIT.Domain.Interfaces;

namespace MowIT.Application.StateMachine;

public class RobotStateMachine : IDisposable
{
    private RobotState _current = RobotState.Idle;
    private IDisposable? _sub;

    public RobotState CurrentState => _current;

private static readonly HashSet<RobotAction> AlwaysAllowed = new()
    {
        RobotAction.BladeOn,
        RobotAction.BladeOff,
        RobotAction.BoundaryCapturePoint,
        RobotAction.BoundaryClear,
        RobotAction.Stop,
        RobotAction.CaptureBase,
        RobotAction.CaptureOutline,
        RobotAction.CaptureExit,
        RobotAction.ManualModeOn,
        RobotAction.ManualModeOff
    };

private static readonly HashSet<(RobotState, RobotAction)> ValidTransitions = new()
    {
        (RobotState.Idle,              RobotAction.StartMowing),
        (RobotState.Idle,              RobotAction.StartRoute),
        (RobotState.Idle,              RobotAction.BoundaryRecordStart),

        (RobotState.Mowing,            RobotAction.Pause),
        (RobotState.Mowing,            RobotAction.ReturnToBase),

        (RobotState.Paused,            RobotAction.Resume),
        (RobotState.Paused,            RobotAction.StartMowing),
        (RobotState.Paused,            RobotAction.ReturnToBase),

        (RobotState.RecordingBoundary, RobotAction.BoundaryRecordEnd),
        (RobotState.RecordingBoundary, RobotAction.BoundaryCapturePoint),

        (RobotState.Returning,         RobotAction.Stop),
        (RobotState.Docking,           RobotAction.Stop),

        (RobotState.Charging,          RobotAction.StartMowing),
        (RobotState.Charging,          RobotAction.StartRoute),
    };

public void Start(IRobotSensors sensors)
    {
        _sub = sensors.StatusStream.Subscribe(s => _current = s.State);
    }

    public bool CanExecute(RobotAction action)
    {
        if (AlwaysAllowed.Contains(action)) return true;
        return ValidTransitions.Contains((_current, action));
    }

    public void Dispose()
    {
        _sub?.Dispose();
        _sub = null;
    }
}
