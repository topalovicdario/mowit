namespace MowIT.Domain.Enums;

public enum RobotAction : byte
{
    StartMowing          = 0x01,
    Pause                = 0x02,
    Resume               = 0x03,
    Stop                 = 0x04,
    ReturnToBase         = 0x05,
    BladeOn              = 0x06,
    BladeOff             = 0x07,
    BoundaryRecordStart  = 0x08,
    BoundaryCapturePoint = 0x09,
    BoundaryRecordEnd    = 0x0A,
    BoundaryClear        = 0x0B,
    StartRoute           = 0x0C,
    ManualModeOn         = 0x0D,
    ManualModeOff        = 0x0E,
    CaptureBase          = 0x0F,
    CaptureOutline       = 0x10,
    CaptureExit          = 0x11,
}
