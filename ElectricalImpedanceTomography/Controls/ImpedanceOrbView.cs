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
        private double _lastPanX;
        private double _lastPanY;

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

            var pointer = new PointerGestureRecognizer();
            pointer.PointerMoved += OnPointerMoved;
            GestureRecognizers.Add(pointer);

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
            if (e.StatusType == GestureStatus.Started)
            {
                _lastPanX = e.TotalX;
                _lastPanY = e.TotalY;
            }
            else if (e.StatusType == GestureStatus.Running)
            {
                var deltaX = e.TotalX - _lastPanX;
                var deltaY = e.TotalY - _lastPanY;
                _lastPanX = e.TotalX;
                _lastPanY = e.TotalY;

                _drawable.RotationY += (float)deltaX * 0.01f;
                _drawable.RotationX += (float)deltaY * 0.01f;
                Invalidate();
            }
        }

        private void OnPointerMoved(object sender, PointerEventArgs e)
        {
            var position = e.GetPosition(this);
            if (position != null)
            {
                float radius = (float)(Math.Min(Width, Height) / 2f * 0.8f);
                float limit = radius * 0.5f;
                float relativeX = (float)(position.Value.X - Width / 2);
                float relativeY = (float)(position.Value.Y - Height / 2);
                _drawable.HighlightX = Math.Clamp(relativeX, -limit, limit);
                _drawable.HighlightY = Math.Clamp(relativeY, -limit, limit);

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
                    view._drawable.OrbColor = newModel.Color;
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
                _drawable.OrbColor = Model.Color;
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
            public Color OrbColor { get; set; } = Colors.CornflowerBlue;
            public float Intensity { get; set; }
            public float Ripple { get; set; }
            public float Scale { get; set; } = 0.9f;
            public float RotationX { get; set; }
            public float RotationY { get; set; }
            public float HighlightX { get; set; }
            public float HighlightY { get; set; }

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                canvas.SaveState();

                canvas.Translate(dirtyRect.Center.X, dirtyRect.Center.Y);
                canvas.Scale(Scale, Scale);

                float radius = Math.Min(dirtyRect.Width, dirtyRect.Height) / 2f * 0.8f;
                float highlightX = HighlightX + (float)(Math.Sin(RotationY) * radius * 0.5f);
                float highlightY = HighlightY + (float)(Math.Sin(RotationX) * radius * 0.5f);

                float alpha = 0.5f + 0.5f * Intensity;
                var centerColor = Color.FromRgba(
                    OrbColor.Red + (1 - OrbColor.Red) * 0.3f,
                    OrbColor.Green + (1 - OrbColor.Green) * 0.3f,
                    OrbColor.Blue + (1 - OrbColor.Blue) * 0.3f,
                    alpha);
                var edgeColor = Color.FromRgba(
                    OrbColor.Red * 0.6f,
                    OrbColor.Green * 0.6f,
                    OrbColor.Blue * 0.6f,
                    alpha);

                var basePaint = new RadialGradientPaint
                {
                    Center = new Point(highlightX, highlightY),
                    Radius = radius,
                    GradientStops = new[]
                    {
                        new PaintGradientStop(0f, centerColor),
                        new PaintGradientStop(1f, edgeColor)
                    }
                };

                canvas.SetFillPaint(basePaint, new RectF(-radius, -radius, radius * 2, radius * 2));
                canvas.FillCircle(0, 0, radius);

                canvas.SaveState();
                canvas.DrawCircle(0, 0, radius);

                float highlightCenterX = highlightX - radius * 0.3f;
                float highlightCenterY = highlightY - radius * 0.3f;
                float highlightRadius = radius * 0.6f;

                var highlightPaint = new RadialGradientPaint
                {
                    Center = new Point(highlightCenterX, highlightCenterY),
                    Radius = highlightRadius,
                    GradientStops = new[]
                    {
                        new PaintGradientStop(0f, Colors.White.WithAlpha(0.7f)),
                        new PaintGradientStop(1f, Colors.White.WithAlpha(0f))
                    }
                };

                canvas.SetFillPaint(highlightPaint, new RectF(highlightCenterX - highlightRadius, highlightCenterY - highlightRadius, highlightRadius * 2, highlightRadius * 2));
                canvas.FillCircle(highlightCenterX, highlightCenterY, highlightRadius);

                canvas.RestoreState();

                canvas.StrokeColor = Colors.White.WithAlpha(0.3f);
                canvas.StrokeSize = 1;
                canvas.DrawCircle(0, 0, radius);

                canvas.StrokeColor = OrbColor.WithAlpha(0.3f);
                canvas.StrokeSize = 2;
                canvas.DrawCircle(0, 0, radius + Ripple * 20);

                canvas.RestoreState();
            }
        }
    }
}
