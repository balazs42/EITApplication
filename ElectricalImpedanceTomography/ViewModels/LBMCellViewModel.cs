using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Utility.Classes.Meshing;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class LBMCellViewModel : BaseViewModel
    {
        public LBMElement Model { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsWall))] // This ensures IsWall also raises a change notification
        private bool _isWallProxy;

        public bool IsWall
        {
            get => Model.IsWall;
            set
            {
                if (Model.IsWall != value)
                {
                    Model.IsWall = value;
                    OnPropertyChanged(); // Notify the UI that the color should change
                }
            }
        }

        public LBMCellViewModel(LBMElement model)
        {
            Model = model;
        }

        [RelayCommand]
        private void ToggleWall()
        {
            // This command is triggered by the tap gesture
            IsWall = !IsWall;
        }
    }
}
