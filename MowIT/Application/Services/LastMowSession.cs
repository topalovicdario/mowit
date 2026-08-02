using Microsoft.Maui.Storage;

namespace MowIT.Application.Services;

public sealed class LastMowSession
{
    private const string Key = "mowit.last_mow_at_ticks";

    public event EventHandler? Changed;

    public DateTime? LastMowAtLocal
    {
        get
        {
            var ticks = Preferences.Default.Get<long>(Key, 0L);
            return ticks == 0L ? null : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
        }
    }

    public void MarkMowedNow()
    {
        Preferences.Default.Set(Key, DateTime.UtcNow.Ticks);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
