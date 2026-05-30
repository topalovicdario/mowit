using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MowIT.Presentation.ViewModels.Base;

namespace MowIT.Presentation.ViewModels;

public partial class LoginViewModel : BaseViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private bool _rememberMe;

    public LoginViewModel()
    {
        Title = "GreenTitan";
        _profileName = Preferences.Get("profile_name", string.Empty);
        _rememberMe  = !string.IsNullOrEmpty(_profileName);
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        if (RememberMe)
            Preferences.Set("profile_name", ProfileName);
        else
            Preferences.Remove("profile_name");

        await Shell.Current.GoToAsync("//scan");
    }

    private bool CanContinue() => !string.IsNullOrWhiteSpace(ProfileName);
}
