using MowIT.Presentation.ViewModels;

namespace MowIT.Presentation.Pages;

public partial class ScanPage : ContentPage
{
    private readonly ScanViewModel _vm;

    public ScanPage(ScanViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _vm.OnDisappearing();
    }
}
