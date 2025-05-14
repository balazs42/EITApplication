namespace ElectricalImpedanceTomography
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            Utility.Composition.Container.InitializeContainer();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}