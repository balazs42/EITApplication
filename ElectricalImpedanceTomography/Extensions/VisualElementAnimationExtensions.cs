using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace ElectricalImpedanceTomography.Extensions;

public static class VisualElementAnimationExtensions
{
    private const string BackgroundPulseAnimationName = "BackgroundPulse";

    public static void StartBackgroundPulse(this VisualElement element, Color startColor, Color endColor, uint duration = 5000)
    {
        if (element is null)
        {
            return;
        }

        element.AbortAnimation(BackgroundPulseAnimationName);
        element.BackgroundColor = startColor;

        var animation = new Animation();
        animation.Add(0, 0.5, new Animation(v => element.BackgroundColor = InterpolateColor(startColor, endColor, v)));
        animation.Add(0.5, 1, new Animation(v => element.BackgroundColor = InterpolateColor(endColor, startColor, v)));

        animation.Commit(
            element,
            BackgroundPulseAnimationName,
            length: duration,
            easing: Easing.Linear,
            repeat: () => true);
    }

    public static void StopBackgroundPulse(this VisualElement element)
    {
        element?.AbortAnimation(BackgroundPulseAnimationName);
    }

    private static Color InterpolateColor(in Color start, in Color end, double progress)
    {
        float r = (float)(start.Red + (end.Red - start.Red) * progress);
        float g = (float)(start.Green + (end.Green - start.Green) * progress);
        float b = (float)(start.Blue + (end.Blue - start.Blue) * progress);
        float a = (float)(start.Alpha + (end.Alpha - start.Alpha) * progress);
        return new Color(r, g, b, a);
    }
}
