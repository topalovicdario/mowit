namespace MowIT.Domain.Entities;


public readonly record struct LocalPoint(float XCm, float YCm)
{
    public float XMeters => XCm / 100f;
    public float YMeters => YCm / 100f;
}
