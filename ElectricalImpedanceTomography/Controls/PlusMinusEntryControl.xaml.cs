using Microsoft.Maui.Controls;
using System;
using System.Threading.Tasks;

namespace ElectricalImpedanceTomography.Controls;
public partial class PlusMinusEntryControl : ContentView
{
    public static readonly BindableProperty LabelTextProperty =
        BindableProperty.Create(nameof(LabelText), typeof(string), typeof(PlusMinusEntryControl), string.Empty);

    public static readonly BindableProperty ValueProperty =
        BindableProperty.Create(nameof(Value), typeof(int), typeof(PlusMinusEntryControl), 0, BindingMode.TwoWay);

    public static readonly BindableProperty MaxProperty =
        BindableProperty.Create(nameof(Max), typeof(int), typeof(PlusMinusEntryControl), int.MaxValue);

    public static readonly BindableProperty MinProperty =
        BindableProperty.Create(nameof(Min), typeof(int), typeof(PlusMinusEntryControl), 0);

    // Keep ImageSource properties for backward compatibility if needed, though mostly unused in new design
    public static readonly BindableProperty PlusImageSourceProperty =
        BindableProperty.Create(nameof(PlusImageSource), typeof(ImageSource), typeof(PlusMinusEntryControl));

    public static readonly BindableProperty MinusImageSourceProperty =
        BindableProperty.Create(nameof(MinusImageSource), typeof(ImageSource), typeof(PlusMinusEntryControl));

    public string LabelText
    {
        get => (string)GetValue(LabelTextProperty);
        set => SetValue(LabelTextProperty, value);
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Max
    {
        get => (int)GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    public int Min
    {
        get => (int)GetValue(MinProperty);
        set => SetValue(MinProperty, value);
    }

    public ImageSource PlusImageSource
    {
        get => (ImageSource)GetValue(PlusImageSourceProperty);
        set => SetValue(PlusImageSourceProperty, value);
    }

    public ImageSource MinusImageSource
    {
        get => (ImageSource)GetValue(MinusImageSourceProperty);
        set => SetValue(MinusImageSourceProperty, value);
    }

    public PlusMinusEntryControl()
    {
        InitializeComponent();
    }

    private async void OnPlusClicked(object sender, EventArgs e)
    {
        if (sender is VisualElement v)
        {
            await v.ScaleTo(0.8, 40);
            await v.ScaleTo(1, 40);
        }

        if (Value + 1 <= Max)
        {
            Value += 1;
        }
    }

    private async void OnMinusClicked(object sender, EventArgs e)
    {
        if (sender is VisualElement v)
        {
            await v.ScaleTo(0.8, 40);
            await v.ScaleTo(1, 40);
        }

        if (Value - 1 >= Min)
        {
            Value -= 1;
        }
    }
}