namespace MowIT.Domain.Entities;

// A point in the firmware's local NEU frame, units = centimetres relative to the captured datum.
// X and Y match whatever convention the firmware uses (most likely X=East, Y=North).
public readonly record struct LocalPoint(float XCm, float YCm)
{
    public float XMeters => XCm / 100f;
    public float YMeters => YCm / 100f;
}
