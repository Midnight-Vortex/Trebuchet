using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DynamicData;
using Humanizer;

namespace tot_gui_lib.Controls;

public partial class Duration : UserControl
{
    public static readonly StyledProperty<TimeSpan> DurationValueProperty =
        AvaloniaProperty.Register<Duration, TimeSpan>(nameof(DurationValue), defaultBindingMode:BindingMode.TwoWay);
    public static readonly StyledProperty<TimeSpan> MinimumDurationProperty =
        AvaloniaProperty.Register<Duration, TimeSpan>(nameof(DurationValue), defaultValue:TimeSpan.MinValue);
    public static readonly StyledProperty<TimeSpan> MaximumDurationProperty =
        AvaloniaProperty.Register<Duration, TimeSpan>(nameof(DurationValue), defaultValue:TimeSpan.MaxValue);
        
    public Duration()
    {
        InitializeComponent();

        _unitSelection = this.Find<Avalonia.Controls.ComboBox>("DurationUnit") ?? throw new NullReferenceException();
        _durationField = this.Find<TextBox>("TextField") ?? throw new NullReferenceException();

        _unitSelection.ItemsSource = new List<string>([
            TimeUnit.Second.Humanize(),
            TimeUnit.Minute.Humanize(),
            TimeUnit.Hour.Humanize(),
            TimeUnit.Day.Humanize()
        ]);
    }

    private readonly TimeUnit[] _availableUnits = [TimeUnit.Second, TimeUnit.Minute, TimeUnit.Hour, TimeUnit.Day];
    private Avalonia.Controls.ComboBox _unitSelection;
    private TextBox _durationField;

    public TimeSpan DurationValue
    {
        get => GetValue(DurationValueProperty);
        set => SetValue(DurationValueProperty, value);
    }
    
    public TimeSpan MinimumDuration
    {
        get => GetValue(MinimumDurationProperty);
        set => SetValue(MinimumDurationProperty, value);
    }
    
    public TimeSpan MaximumDuration
    {
        get => GetValue(MaximumDurationProperty);
        set => SetValue(MaximumDurationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DurationValueProperty)
        {
            if (DurationValue < MinimumDuration)
                DurationValue = MinimumDuration;
            if (DurationValue > MaximumDuration)
                DurationValue = MaximumDuration;

            var unit = GetTimeUnit(DurationValue);
            _unitSelection.SelectedIndex = _availableUnits.IndexOf(unit);
            _durationField.Text = GetTimeValue(DurationValue, unit).ToString(CultureInfo.CurrentCulture);
        }
        else if (change.Property == MinimumDurationProperty)
        {
            if (DurationValue < MinimumDuration)
                DurationValue = MinimumDuration;
        }
        else if (change.Property == MaximumDurationProperty)
        {
            if (DurationValue > MaximumDuration)
                DurationValue = MaximumDuration;
        }
    }

    private TimeUnit GetTimeUnit(TimeSpan duration)
    {
        if (duration.Days > 0)
            return TimeUnit.Day;
        if (duration.Hours > 0)
            return TimeUnit.Hour;
        if (duration.Minutes > 0)
            return TimeUnit.Minute;
        return TimeUnit.Second;
    }

    private double GetTimeValue(TimeSpan duration, TimeUnit unit)
    {
        switch (unit)
        {
            case TimeUnit.Day:
                return duration.TotalDays;
            case TimeUnit.Hour:
                return duration.TotalHours;
            case TimeUnit.Minute:
                return duration.TotalMinutes;
            case TimeUnit.Second:
            default:
                return duration.TotalSeconds;    
        }
    }

    private void DurationUnit_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_unitSelection.SelectedIndex >= _availableUnits.Length || _unitSelection.SelectedIndex < 0) return;
        var unit = _availableUnits[_unitSelection.SelectedIndex];
        _durationField.Text = GetTimeValue(DurationValue, unit).ToString(CultureInfo.CurrentCulture);
    }

    private void TextField_OnTextChanged(object? sender, TextChangedEventArgs e)
    {

    }

    private void TextField_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        ApplyText();
    }

    private void TextField_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyText();
    }

    private void ApplyText()
    {
        if (!double.TryParse(_durationField.Text, out var value)) return;
        if (_unitSelection.SelectedIndex >= _availableUnits.Length || _unitSelection.SelectedIndex < 0) return;
        
        var unit = _availableUnits[_unitSelection.SelectedIndex];

        switch (unit)
        {
            case TimeUnit.Day:
                DurationValue = TimeSpan.FromDays(value);
                break;
            case TimeUnit.Hour:
                DurationValue = TimeSpan.FromHours(value);
                break;
            case TimeUnit.Minute:
                DurationValue = TimeSpan.FromMinutes(value);
                break;
            case TimeUnit.Second:
            default:
                DurationValue = TimeSpan.FromSeconds(value);
                break;
        }
    }
}