using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Maui.Views;
using ElectricalImpedanceTomography.Views;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using ServiceLayer;
using System.Collections.ObjectModel;
using Utility.Classes.Measurement;

namespace ElectricalImpedanceTomography.ViewModels
{
    public partial class DAQPageViewModel : BaseViewModel
    {
        private readonly IDAQService _daqService;

        [ObservableProperty]
        private ObservableCollection<PlotModel> plotModels = []; // Plots that dispaly the voltage measurements

        private const int MaxPoints = 60;   // Max points present on a plot at once
        private const int TimeWindowSecs = 5;
        private readonly double _windowDays = TimeWindowSecs / 86400.0;
        private readonly DateTime _startTime;

        private readonly double[] _channelGains = new double[16];
        private readonly double[] _channelOffsets = new double[16];

        public DAQPageViewModel(IDAQService daqService)
        {
            _daqService = daqService;

            Array.Fill(_channelGains, 1.0);

            _startTime = DateTime.Now;
            InitializePlots();
        }

        private void OnMeasurementReceived(EITMeasurement m)
        {
            if (m is EITMeasurement eitMeasurement)
                UpdatePlots(eitMeasurement);
        }

        // Initializes the plots with the appropriate axes and labels
        private void InitializePlots()
        {
            PlotModels.Clear();
            for (int i = 0; i < 16; i++)
            {
                var plotModel = new PlotModel
                {
                    Title = $"CH{i}-CH{(i + 1) % 16}: Voltage [mV] - t [s]",
                    Background = OxyColors.White,
                    PlotAreaBorderColor = OxyColors.Transparent,
                    TitleColor = OxyColors.DarkSlateGray,
                    IsLegendVisible = true,
                    TitleFontWeight = OxyPlot.FontWeights.Bold,
                    PlotMargins = new OxyThickness(20, 20, 20, 20),
                    DefaultFont = "SF Pro Text",
                    TitleFont = "SF Pro Text"
                };
                var series = new LineSeries
                {
                    StrokeThickness = 2,
                    Color = (i % 2) == 0
                        ? OxyColors.SteelBlue
                        : OxyColors.IndianRed,
                    LineStyle = LineStyle.Solid,
                    CanTrackerInterpolatePoints = false
                };
                var dateAxis = new DateTimeAxis
                {
                    Position = AxisPosition.Bottom,
                    StringFormat = "HH:mm:ss",
                    AxislineStyle = LineStyle.Solid,
                    TextColor = OxyColors.DarkSlateGray,
                    IntervalType = DateTimeIntervalType.Milliseconds,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot,
                    IsZoomEnabled = false,
                    IsPanEnabled = false,
                    Font = "SF Pro Text",
                    TitleFont = "SF Pro Text"
                };
                var valueAxis = new LinearAxis
                {
                    Position = AxisPosition.Left,
                    TextColor = OxyColors.DarkSlateGray,
                    MajorGridlineStyle = LineStyle.Solid,
                    MinorGridlineStyle = LineStyle.Dot,
                    IsZoomEnabled = false,
                    IsPanEnabled = false,
                    Font = "SF Pro Text",
                    TitleFont = "SF Pro Text"
                };
                plotModel.Axes.Add(dateAxis);
                plotModel.Axes.Add(valueAxis);
                plotModel.Series.Add(series);

                dateAxis.Minimum = DateTimeAxis.ToDouble(_startTime.AddSeconds(-TimeWindowSecs));
                dateAxis.Maximum = DateTimeAxis.ToDouble(_startTime);

                PlotModels.Add(plotModel);
            }
        }

        [RelayCommand]
        [Obsolete]
        private async Task ConfigureChannelAsync(PlotModel model)
        {
            int index = PlotModels.IndexOf(model);
            if (index < 0)
                return;

            var popup = new ChannelSettingsPopup(_channelGains[index], _channelOffsets[index]);
            var result = await (Application.Current?.MainPage ?? throw new NullReferenceException()).ShowPopupAsync(popup);
            if (result is ValueTuple<double, double> tuple)
            {
                _channelGains[index] = tuple.Item1;
                _channelOffsets[index] = tuple.Item2;
            }
        }
        // Example measurement data:
        /*
         *    NAN   NAN   +0.014 +0.012 +0.012 +0.012 +0.013 +0.010 +0.013 +0.012 +0.012 +0.014 +0.014 +0.014 +0.013   NAN   
         *    NAN   NAN     NAN  +0.014 +0.012 +0.011 +0.013 +0.012 +0.010 +0.014 +0.013 +0.013 +0.012 +0.012 +0.012 +0.014 
         *  +0.014  NAN     NAN    NAN  +0.010 +0.010 +0.012 +0.012 +0.013 +0.014 +0.015 +0.014 +0.013 +0.014 +0.012 +0.014
         *  +0.013 +0.013   NAN    NAN    NAN  +0.011 +0.013 +0.013 +0.012 +0.013 +0.013 +0.012 +0.014 +0.013 +0.014 +0.014 
         *  +0.014 +0.014 +0.013   NAN    NAN    NAN  +0.010 +0.013 +0.011 +0.013 +0.013 +0.013 +0.013 +0.013 +0.012 +0.011
         *  +0.014 +0.012 +0.014 +0.013   NAN    NAN    NAN  +0.011 +0.013 +0.012 +0.015 +0.013 +0.014 +0.013 +0.014 +0.015 
         *  +0.012 +0.012 +0.012 +0.012 +0.013   NAN    NAN    NAN  +0.012 +0.014 +0.012 +0.013 +0.013 +0.013 +0.013 +0.013 
         *  +0.013 +0.013 +0.012 +0.010 +0.011 +0.014   NAN    NAN    NAN  +0.013 +0.012 +0.016 +0.015 +0.012 +0.013 +0.014
         *  +0.013 +0.012 +0.014 +0.013 +0.013 +0.012 +0.012   NAN    NAN    NAN  +0.015 +0.014 +0.014 +0.013 +0.012 +0.014 
         *  +0.012 +0.012 +0.014 +0.013 +0.012 +0.010 +0.014 +0.013   NAN    NAN    NAN  +0.015 +0.012 +0.012 +0.014 +0.013 
         *  +0.011 +0.012 +0.011 +0.013 +0.015 +0.010 +0.012 +0.015 +0.014   NAN    NAN    NAN  +0.013 +0.013 +0.012 +0.012
         *  +0.011 +0.012 +0.012 +0.012 +0.014 +0.013 +0.013 +0.013 +0.012 +0.014   NAN    NAN    NAN  +0.013 +0.013 +0.012 
         *  +0.012 +0.011 +0.013 +0.012 +0.010 +0.016 +0.013 +0.014 +0.013 +0.015 +0.013   NAN    NAN    NAN  +0.012 +0.014 
         *  +0.013 +0.011 +0.012 +0.015 +0.013 +0.014 +0.012 +0.014 +0.013 +0.014 +0.014 +0.013   NAN    NAN    NAN  +0.013
         *  +0.013 +0.013 +0.014 +0.012 +0.013 +0.014 +0.012 +0.014 +0.012 +0.013 +0.013 +0.014 +0.012   NAN    NAN    NAN   
         *
         */

        private void UpdatePlots(EITMeasurement measurements)
        {
            for(int i = 0; i < 16; i++)
            {
                double[] currentFrame = measurements.Frames[i];

                for(int j = 0; j < currentFrame.Length; j++)
                    if (currentFrame[j] != double.NaN)
                        AppendDataPoint(PlotModels[j], DateTime.Now, currentFrame[j]);
            }

            InvalidatePlots();
        }

        private void AppendDataPoint(PlotModel model, DateTime timestamp, double value)
        {
            var series = model.Series.OfType<LineSeries>().First();
            var axis = model.Axes.OfType<DateTimeAxis>().First();

            // Add new
            double x = DateTimeAxis.ToDouble(timestamp);
            int idx = PlotModels.IndexOf(model);
            double gain = _channelGains[idx];
            double offset = _channelOffsets[idx];
            series.Points.Add(new DataPoint(x, value * gain + offset));

            // Trim old
            if (series.Points.Count > MaxPoints)
                series.Points.RemoveAt(0);

            axis.Minimum = x - _windowDays;
            axis.Maximum = x;
        }

        private void InvalidatePlots()
        {
            foreach (var model in PlotModels)
                model.InvalidatePlot(true);
        }
    }
}
