using EasyTerminal.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EasyTerminal
{
    public partial class App : Application
    {
        private MainWindow? _window;
        public static MainWindow? MainWindowInstance { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            AppServices.Initialize(DispatcherQueue.GetForCurrentThread());
            _window = new MainWindow();
            MainWindowInstance = _window;
            _window.Activate();
        }
    }
}
