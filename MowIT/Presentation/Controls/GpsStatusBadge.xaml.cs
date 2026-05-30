using MowIT.Domain.Enums;

namespace MowIT.Presentation.Controls;

public partial class GpsStatusBadge : ContentView
{
    public static readonly BindableProperty FixTypeProperty =
        BindableProperty.Create(nameof(FixType), typeof(GpsFixType), typeof(GpsStatusBadge),
            GpsFixType.NoFix, propertyChanged: (b, _, _) => ((GpsStatusBadge)b).Update());

    public static readonly BindableProperty AccuracyProperty =
        BindableProperty.Create(nameof(Accuracy), typeof(string), typeof(GpsStatusBadge),
            string.Empty, propertyChanged: (b, _, _) => ((GpsStatusBadge)b).Update());

    public GpsFixType FixType
    {
        get => (GpsFixType)GetValue(FixTypeProperty);
        set => SetValue(FixTypeProperty, value);
    }

    public string Accuracy
    {
        get => (string)GetValue(AccuracyProperty);
        set => SetValue(AccuracyProperty, value);
    }

    public GpsStatusBadge() => InitializeComponent();

    private void Update()
    {
        (string text, Color color) = FixType switch
        {
            GpsFixType.RtkFixed  => ("RTK Fixed",  Color.FromArgb("#4CAF50")),
            GpsFixType.RtkFloat  => ("RTK Float",  Color.FromArgb("#FF9800")),
            GpsFixType.Standard  => ("GPS",         Color.FromArgb("#2196F3")),
            _                    => ("No Fix",      Color.FromArgb("#F44336"))
        };

        BadgeBorder.BackgroundColor = color;
        FixLabel.Text               = text;
        AccuracyLabel.Text          = Accuracy;
    }
}
