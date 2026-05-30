namespace MowIT.Domain.Entities;

public class MowerDevice
{
    public Guid   Id   { get; set; }
    public string Name { get; set; } = string.Empty;
    public int    Rssi { get; set; }

    public string RssiLabel => $"{Rssi} dBm";
}
