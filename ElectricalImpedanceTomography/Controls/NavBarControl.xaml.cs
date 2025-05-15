namespace ElectricalImpedanceTomography.Controls;

public partial class NavBarControl : ContentView
{
    // Define Bindable Property to receive the current page name
    public static readonly BindableProperty CurrentPageNameProperty =
        BindableProperty.Create(
            nameof(CurrentPageName),                    // Property name
            typeof(string),                             // Property type
            typeof(NavBarControl),                      // Declaring type
            string.Empty,                               // Default value
            propertyChanged: OnCurrentPageNameChanged); // Action when property changes

    public string CurrentPageName
    {
        get => (string)GetValue(CurrentPageNameProperty);
        set => SetValue(CurrentPageNameProperty, value);
    }

    public NavBarControl()
    {
        InitializeComponent();

        // Initial visibility setup based on default value (can be omitted if default is empty)
        UpdateButtonVisibility(CurrentPageName);
    }

    // Called when the CurrentPageName property changes
    private static void OnCurrentPageNameChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is NavBarControl control && newValue is string pageName)
            control.UpdateButtonVisibility(pageName);
    }

    // Logic to hide the button corresponding to the current page
    private void UpdateButtonVisibility(string currentPageName)
    {
        // Reset all buttons to visible first
        MainPageButton.IsVisible = true;
        DAQPageButton.IsVisible = true;
        ReconstructionPageButton.IsVisible = true;

        // Hide the button matching the current page name (case-insensitive)
        if (string.Equals(currentPageName, "MainPage"))
            MainPageButton.IsVisible = false;
        else if (string.Equals(currentPageName, "DAQPage"))
            DAQPageButton.IsVisible = false;
        else if (string.Equals(currentPageName, "ReconstructionPage"))
            ReconstructionPageButton.IsVisible = false;
    }

    // Handle navigation for ALL buttons
    private async void NavigateButton_Clicked(object sender, EventArgs e)
    {
        if (sender is not Button clickedButton) return;

        string targetPageName = clickedButton.Text; // Use button text to determine target

        // These routes MUST be registered in AppShell.xaml.cs
        // Mapping button texts to navigation strings
        // Using '///' ensures absolute routing from the Shell root
        string route = targetPageName switch
        {
            "Main" => "///MainPage",
            "DAQ" => "//DAQPage",
            "EIT Reconstruction" => "//ReconstructionPage",
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(route) && Shell.Current != null)
        {
            var currentRoute = Shell.Current.CurrentState?.Location?.OriginalString;

            if (currentRoute != null && new Uri(currentRoute, UriKind.Absolute).AbsolutePath == new Uri(route.TrimStart('/'), UriKind.RelativeOrAbsolute).ToString())
            {
                System.Diagnostics.Debug.WriteLine($"Already on page: {route}");
                return; // Don't navigate if already there
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"Navigating to Shell route: {route}");

                await Shell.Current.GoToAsync(route, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Navigation Error: Failed to navigate to {route}. {ex.Message}");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"Navigation Error: Route not defined or Shell not ready for {targetPageName}");
        }
    }
}