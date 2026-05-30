namespace MowIT.Application.Messages;

public record RobotConnectedMessage(string DeviceName);
public record RobotDisconnectedMessage(string Reason = "");
public record LowBatteryWarningMessage(int BatteryPct);
public record GeofenceBreachMessage(Domain.Entities.GpsPoint Position, string ZoneName);
public record RobotErrorMessage(string Code);
public record CaptureEndMessage(bool Success, string? FailReason = null);
public record BaseCapturedMessage();
public record BoundaryPointCapturedMessage(int XCm, int YCm);
public record OutlineCapturedMessage();
public record ExitCapturedMessage(int XCm, int YCm);
// Sent on MOWER/CAPTURE/START/OK — firmware sets CONFIG_PATH=false there, so any saved
// boundary is now invalidated. Dashboard uses this to flip HasPath off.
public record BoundaryClearedMessage();
