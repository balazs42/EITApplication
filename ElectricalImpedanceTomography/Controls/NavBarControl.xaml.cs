namespace ElectricalImpedanceTomography.Controls;

using Color = Microsoft.Maui.Graphics.Color;
using Workspace = Utility.Classes.Application.Workspace;

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

        // Initialize button states so that the bound page appears pressed
        SetButtonStates(CurrentPageName);
    }

    // Called when the CurrentPageName property changes
    private static void OnCurrentPageNameChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is NavBarControl control)
            control.SetButtonStates(newValue as string);
    }

    // Applies the visual state to all buttons and marks the given page as active
    private void SetButtonStates(string? currentPageName)
    {
        ResetButton(MainPageButton);
        ResetButton(DAQPageButton);
        ResetButton(MeshingPageButton);
        ResetButton(ReconstructionPageButton);

        var active = GetButtonForPage(currentPageName);
        if (active != null)
        {
            active.BackgroundColor = Color.FromRgb(0x44, 0x44, 0x44);
            active.Scale = 0.95;
        }
    }

    private static void ResetButton(Border btn)
    {
        btn.BackgroundColor = Color.FromRgb(0x55, 0x55, 0x55);
        btn.Scale = 1.0;
    }

    private Border? GetButtonForPage(string? pageName)
        => pageName switch
        {
            "MainPage" => MainPageButton,
            "DAQPage" => DAQPageButton,
            "MeshingPage" => MeshingPageButton,
            "ReconstructionPage" => ReconstructionPageButton,
            _ => null
        };

    private async void NavigateButton_Clicked(object sender, EventArgs e)
        => await HandleNavigationAsync(sender as VisualElement);

    private async void NavigateButton_Tapped(object sender, TappedEventArgs e)
        => await HandleNavigationAsync(sender as VisualElement);

    private async Task HandleNavigationAsync(VisualElement? element)
    {
        if (element == null)
            return;

        string targetPage = string.Empty;
        string route = string.Empty;
        if (element == MainPageButton)
        {
            targetPage = "MainPage";
            route = "///MainPage";
        }
        else if (element == DAQPageButton)
        {
            targetPage = "DAQPage";
            route = "//DAQPage";
        }
        else if (element == MeshingPageButton)
        {
            targetPage = "MeshingPage";
            route = "//MeshingPage";
        }
        else if (element == ReconstructionPageButton)
        {
            targetPage = "ReconstructionPage";
            route = "//ReconstructionPage";
        }

        if (string.IsNullOrEmpty(route) || Shell.Current == null)
        {
            string errorMessage = $"Navigation Error: Route not defined or Shell not ready";

            System.Diagnostics.Debug.WriteLine(errorMessage);
            Workspace.AddErrorMessage(errorMessage);
            return;
        }

        if (targetPage == CurrentPageName)
        {
            string warningMessage = $"Already on page: {route}";
            System.Diagnostics.Debug.WriteLine(warningMessage);
            Workspace.AddWarningMessage(warningMessage);
            return;
        }

        var current = CurrentPageName;

        // Animate old/new button states
        await AnimateButtonChangeAsync(current, targetPage);

        try
        {
            string logMessage = $"Navigating to Shell route: {route}";

            System.Diagnostics.Debug.WriteLine(logMessage);
            Workspace.AddLoadingMessage(logMessage);

            await Shell.Current.GoToAsync(route, true);
        }
        catch (Exception ex)
        {
            string errorMessage = $"Navigation Error: Failed to navigate to {route}. {ex.Message}";
            System.Diagnostics.Debug.WriteLine(errorMessage);
            Workspace.AddErrorMessage(errorMessage);
        }
        finally
        {
            // Restore the button state for this page so it remains correct when revisited
            SetButtonStates(current);
        }
    }

    // Resets the CurrentPageName to the page this control is hosted in
    public void RefreshCurrentPage()
    {
        var pageName = GetParentPage()?.GetType().Name;
        if (!string.IsNullOrEmpty(pageName))
        {
            CurrentPageName = pageName;
            SetButtonStates(pageName);
        }
    }

    private Page? GetParentPage()
    {
        Element? parent = Parent;
        while (parent != null && parent is not Page)
            parent = parent.Parent;
        return parent as Page;
    }

    // Performs the unstuck and stuck animations between pages
    private async Task AnimateButtonChangeAsync(string? oldPage, string? newPage)
    {
        var oldBtn = GetButtonForPage(oldPage);
        var newBtn = GetButtonForPage(newPage);

        if (oldBtn != null)
        {
            await oldBtn.ScaleTo(1.0, 50);
            oldBtn.BackgroundColor = Color.FromRgb(0x55, 0x55, 0x55);
        }

        if (newBtn != null)
        {
            await newBtn.ScaleTo(0.95, 50);
            newBtn.BackgroundColor = Color.FromRgb(0x44, 0x44, 0x44);
        }
    }
}