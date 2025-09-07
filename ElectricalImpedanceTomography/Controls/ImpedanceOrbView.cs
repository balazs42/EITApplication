using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace ElectricalImpedanceTomography.Controls
{
    public partial class CurrentImpedanceModel : ObservableObject
    {
        [ObservableProperty]
        private float intensity;

        [ObservableProperty]
        private Color color = Colors.CornflowerBlue;
    }

    public class ImpedanceOrbView : GraphicsView
    {
        private readonly OrbDrawable _drawable;

        public static readonly BindableProperty ModelProperty =
            BindableProperty.Create(nameof(Model), typeof(CurrentImpedanceModel), typeof(ImpedanceOrbView), propertyChanged: OnModelChanged);

        public CurrentImpedanceModel Model
        {
            get => (CurrentImpedanceModel)GetValue(ModelProperty);
            set => SetValue(ModelProperty, value);
        }

        public ImpedanceOrbView()
        {
            _drawable = new OrbDrawable();
            Drawable = _drawable;

            var pinch = new PinchGestureRecognizer();
            pinch.PinchUpdated += OnPinchUpdated;
            GestureRecognizers.Add(pinch);

            var pan = new PanGestureRecognizer();
            pan.PanUpdated += OnPanUpdated;
            GestureRecognizers.Add(pan);

            StartRippleAnimation();
        }

        private void OnPinchUpdated(object sender, PinchGestureUpdatedEventArgs e)
        {
            if (e.Status == GestureStatus.Running)
            {
                _drawable.Scale *= (float)e.Scale;
                Invalidate();
            }
        }

        private void OnPanUpdated(object sender, PanUpdatedEventArgs e)
        {
            if (e.StatusType == GestureStatus.Running)
            {
                _drawable.Rotation += (float)e.TotalX * 0.1f;
                Invalidate();
            }
        }

        private static void OnModelChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is ImpedanceOrbView view)
            {
                if (oldValue is CurrentImpedanceModel oldModel)
                    oldModel.PropertyChanged -= view.OnModelPropertyChanged;

                if (newValue is CurrentImpedanceModel newModel)
                {
                    newModel.PropertyChanged += view.OnModelPropertyChanged;
                    view._drawable.Color = newModel.Color;
                    view._drawable.Intensity = newModel.Intensity;
                }

                view.Invalidate();
            }
        }

        private void OnModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (Model == null)
                return;

            if (e.PropertyName == nameof(CurrentImpedanceModel.Color))
                _drawable.Color = Model.Color;
            else if (e.PropertyName == nameof(CurrentImpedanceModel.Intensity))
                _drawable.Intensity = Model.Intensity;

            Invalidate();
        }

        private void StartRippleAnimation()
        {
            var animation = new Animation(v =>
            {
                _drawable.Ripple = (float)v;
                Invalidate();
            }, 0, 1);

            animation.Commit(this, "RippleAnim", 16, 2500, Easing.Linear, (v, c) => StartRippleAnimation());
        }

        private class OrbDrawable : IDrawable
        {
            public Color Color { get; set; } = Colors.CornflowerBlue;
            public float Intensity { get; set; }
            public float Ripple { get; set; }
            public float Scale { get; set; } = 0.9f;
            public float Rotation { get; set; }

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                canvas.SaveState();

                canvas.Translate(dirtyRect.Center.X, dirtyRect.Center.Y);
                canvas.Rotate(Rotation);
                canvas.Scale(Scale, Scale);

                float radius = Math.Min(dirtyRect.Width, dirtyRect.Height) / 2f * 0.8f;
                var fillColor = Color.WithAlpha(0.5f + 0.5f * Intensity);
                canvas.FillColor = fillColor;
                canvas.FillCircle(0, 0, radius);

                canvas.StrokeColor = Color.WithAlpha(0.3f);
                canvas.StrokeSize = 2;
                canvas.DrawCircle(0, 0, radius + Ripple * 20);

                canvas.RestoreState();
            }
        }
    }
}

