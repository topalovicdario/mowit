namespace MowIT
{
    public partial class App : Microsoft.Maui.Controls.Application
    {
        public App()
        {
            InitializeComponent();
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                            {
                                System.Diagnostics.Debug.WriteLine(e.ExceptionObject);
                            };

                TaskScheduler.UnobservedTaskException += (s, e) =>
                            {
                                System.Diagnostics.Debug.WriteLine(e.Exception);
                            };
            MainPage = new AppShell();
            
        }
    }
}
