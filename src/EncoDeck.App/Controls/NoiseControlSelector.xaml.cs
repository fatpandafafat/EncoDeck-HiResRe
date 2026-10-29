// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using EncoDeck.App.Services;
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace EncoDeck.App.Controls;

/// <summary>
/// Noise cancelling / adaptive / transparency / off selector with a level row for the selected
/// mode. Checked states always mirror the device-confirmed state in <see cref="DeviceViewModel"/>;
/// a click only issues a command and the buttons snap back until the device confirms.
/// </summary>
public sealed partial class NoiseControlSelector : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(DeviceViewModel), typeof(NoiseControlSelector), new PropertyMetadata(null, OnViewModelChanged));

    private readonly List<(NoiseLevelViewModel Level, ToggleButton Button)> _levelButtons = [];
    private ObservableCollection<NoiseLevelViewModel>? _shownLevels;

    public NoiseControlSelector()
    {
        InitializeComponent();
        foreach (var label in new[] { AncLabel, AdaptiveLabel, TransparencyLabel, OffLabel }) FitLabel.Attach(label);
        // Moving the control to another parent (HomePage on wide windows) can raise Unloaded after the
        // new Loaded; detaching then would leave the buttons deaf to the device state.
        Unloaded += (_, _) => { if (!IsLoaded) Detach(ViewModel); };
        Loaded += (_, _) => Attach(ViewModel);
    }

    public DeviceViewModel? ViewModel
    {
        get => (DeviceViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (NoiseControlSelector)d;
        control.Detach(e.OldValue as DeviceViewModel);
        control.Attach(e.NewValue as DeviceViewModel);
    }

    private void Attach(DeviceViewModel? vm)
    {
        if (vm is null) return;
        Detach(vm);
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.NoiseLevels.CollectionChanged += OnLevelsChanged;
        vm.AdaptiveLevels.CollectionChanged += OnLevelsChanged;
        vm.TransparencyLevels.CollectionChanged += OnLevelsChanged;
        _shownLevels = null;
        Sync();
    }

    private void Detach(DeviceViewModel? vm)
    {
        if (vm is null) return;
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.NoiseLevels.CollectionChanged -= OnLevelsChanged;
        vm.AdaptiveLevels.CollectionChanged -= OnLevelsChanged;
        vm.TransparencyLevels.CollectionChanged -= OnLevelsChanged;
        foreach (var (level, _) in _levelButtons) level.PropertyChanged -= OnLevelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void OnLevelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _shownLevels = null;
        Sync();
    }

    private void OnLevelPropertyChanged(object? sender, PropertyChangedEventArgs e) => SyncLevelButtons();

    private void LayoutModes(DeviceViewModel vm)
    {
        var buttons = new (ToggleButton Button, bool Visible)[]
        {
            (AncButton, vm.HasAncMode),
            (AdaptiveButton, vm.HasAdaptiveMode),
            (TransparencyButton, vm.HasTransparencyMode),
            (OffButton, vm.HasOffMode),
        };
        int visible = buttons.Count(b => b.Visible);
        if (ModeGrid.ColumnDefinitions.Count != visible)
        {
            ModeGrid.ColumnDefinitions.Clear();
            for (int i = 0; i < visible; i++) ModeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        int column = 0;
        foreach (var (button, show) in buttons)
        {
            button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) Grid.SetColumn(button, column++);
        }
    }

    private void Sync()
    {
        var vm = ViewModel;
        if (vm is null) return;
        LayoutModes(vm);
        bool enabled = vm.CanSetNoise && !vm.IsNoisePending;

        AncButton.IsChecked = vm.IsAncSelected;
        AdaptiveButton.IsChecked = vm.IsAdaptiveSelected;
        TransparencyButton.IsChecked = vm.IsTransparencySelected;
        OffButton.IsChecked = vm.IsOffSelected;
        AncButton.IsEnabled = AdaptiveButton.IsEnabled = TransparencyButton.IsEnabled = OffButton.IsEnabled = enabled;

        var levels = vm.ShowNoiseLevels ? vm.NoiseLevels : vm.ShowAdaptiveLevels ? vm.AdaptiveLevels
            : vm.ShowTransparencyLevels ? vm.TransparencyLevels : null;
        if (!ReferenceEquals(levels, _shownLevels)) RebuildLevels(levels,
            vm.ShowNoiseLevels ? "NoiseSelector_Level" : vm.ShowAdaptiveLevels ? "NoiseSelector_AdaptiveLevel" : "NoiseSelector_TransparencyLevel");
        LevelsPanel.Visibility = levels is null ? Visibility.Collapsed : Visibility.Visible;
        SyncLevelButtons();
    }

    private void RebuildLevels(ObservableCollection<NoiseLevelViewModel>? levels, string titleKey)
    {
        foreach (var (level, _) in _levelButtons) level.PropertyChanged -= OnLevelPropertyChanged;
        _levelButtons.Clear();
        LevelsHost.Children.Clear();
        _shownLevels = levels;
        if (levels is null) return;

        LevelsTitle.Text = Loc.Get(titleKey);
        LevelsHost.ColumnDefinitions.Clear();
        int column = 0;
        foreach (var level in levels)
        {
            // One equal column per level, so they line up under the mode buttons and never wrap.
            LevelsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new ToggleButton
            {
                Content = FitLabel.Create(level.Label),
                Tag = level.Id,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 36,
                Padding = new Thickness(4, 6, 4, 6),
                CornerRadius = new CornerRadius(6),
            };
            Grid.SetColumn(button, column++);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, level.Label);
            button.Click += OnLevelClick;
            level.PropertyChanged += OnLevelPropertyChanged;
            _levelButtons.Add((level, button));
            LevelsHost.Children.Add(button);
        }
    }

    private void SyncLevelButtons()
    {
        var vm = ViewModel;
        if (vm is null) return;
        bool enabled = vm.CanSetNoise && !vm.IsNoisePending;
        foreach (var (level, button) in _levelButtons)
        {
            button.IsChecked = level.IsSelected;
            button.IsEnabled = enabled;
        }
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string mode } && ViewModel is { } vm)
        {
            Sync(); // revert the local toggle immediately; the device decides
            vm.SetNoiseModeCommand.Execute(mode);
        }
    }

    private void OnLevelClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string id } && ViewModel is { } vm)
        {
            SyncLevelButtons();
            vm.SetNoiseLevelCommand.Execute(id);
        }
    }
}
