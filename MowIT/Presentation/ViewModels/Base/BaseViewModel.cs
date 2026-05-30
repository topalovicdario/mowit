using CommunityToolkit.Mvvm.ComponentModel;

namespace MowIT.Presentation.ViewModels.Base;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool IsNotBusy => !IsBusy;

protected async Task RunSafeAsync(Func<Task> action, string errorPrefix = "")
    {
        if (IsBusy) return;
        IsBusy       = true;   
        ErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            var msg = string.IsNullOrEmpty(errorPrefix) ? ex.Message : $"{errorPrefix}: {ex.Message}";

await MainThread.InvokeOnMainThreadAsync(() => ErrorMessage = msg);
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() => IsBusy = false);
        }
    }

    public virtual Task OnAppearingAsync() => Task.CompletedTask;
    public virtual void OnDisappearing() { }
}
