namespace MowIT.Application.DTOs;

public record MotorCommandDto(float LinearVel, float AngularVel)
{
    public static MotorCommandDto Stop => new(0f, 0f);

    public MotorCommandDto Clamped() => new(
        Math.Clamp(LinearVel,  -0.5f, 0.5f),
        Math.Clamp(AngularVel, -1.0f, 1.0f));
}
