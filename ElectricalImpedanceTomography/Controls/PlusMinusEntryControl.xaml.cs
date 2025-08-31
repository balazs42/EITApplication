using Microsoft.Maui.Controls;
using System;

namespace ElectricalImpedanceTomography.Controls;

public partial class PlusMinusEntryControl : ContentView
{
    public static readonly BindableProperty LabelTextProperty =
        BindableProperty.Create(nameof(LabelText), typeof(string), typeof(PlusMinusEntryControl), string.Empty);

    public static readonly BindableProperty ValueProperty =
        BindableProperty.Create(nameof(Value), typeof(int), typeof(PlusMinusEntryControl), 0, BindingMode.TwoWay);

    public static readonly BindableProperty MaxProperty =
        BindableProperty.Create(nameof(Max), typeof(int), typeof(PlusMinusEntryControl), int.MaxValue);

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

    public void OnPlusTapped(object sender, EventArgs e)
    {
        if (Value + 1 <= Max)
        {
            Value = Value + 1;
        }
    }

    public void OnMinusTapped(object sender, EventArgs e)
    {
        if (Value - 1 > 0)
        {
            Value = Value - 1;
        }
    }
}
