using CommunityToolkit.Mvvm.ComponentModel;
using MathNet.Numerics.Statistics;
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
        private ObservableCollection<PlotModel> plotModels = new(); // Plots that dispaly the voltage measurements

        private const int MaxPoints = 60;   // Max points present on a plot at once
        private const int TimeWindowSecs = 5;
        private readonly double _windowDays = TimeWindowSecs / 86400.0;
        private readonly DateTime _startTime;

        public DAQPageViewModel(IDAQService daqService)
        {
            _daqService = daqService;

            _startTime = DateTime.Now;
            InitializePlots();
        }

        private void OnMeasurementReceived(EITMeasurements m)
        {
            if (m is EITMeasurements eitMeasurement)
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
                    PlotMargins = new OxyThickness(20, 20, 20, 20)

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
                    IsPanEnabled = false
                };
                plotModel.Axes.Add(dateAxis);
                plotModel.Series.Add(series);

                dateAxis.Minimum = DateTimeAxis.ToDouble(_startTime.AddSeconds(-TimeWindowSecs));
                dateAxis.Maximum = DateTimeAxis.ToDouble(_startTime);

                PlotModels.Add(plotModel);
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

        private void UpdatePlots(EITMeasurements measurements)
        {
            for(int i = 0; i < 16; i++)
            {
                SixteenElectrodeMeasurement currentMeasurement = measurements.GetMeasurement(i);
                double[] currentData = currentMeasurement.Measurement;

                for(int j = 0; j < 16; j++)
                    if (currentData[j] != double.NaN)
                        AppendDataPoint(PlotModels[j], DateTime.Now, currentData[j]);
            }

            InvalidatePlots();
        }

        private void AppendDataPoint(PlotModel model, DateTime timestamp, double value)
        {
            var series = model.Series.OfType<LineSeries>().First();
            var axis = model.Axes.OfType<DateTimeAxis>().First();

            // Add new
            double x = DateTimeAxis.ToDouble(timestamp);
            series.Points.Add(new DataPoint(x, value));

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
