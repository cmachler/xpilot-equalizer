using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XpilotAudioEqualizer.Config;
using XpilotAudioEqualizer.Dsp;

namespace XpilotAudioEqualizer.Ui;

internal sealed class NormalizerWindow : Window
{
    private static readonly Color Bg = Color.Parse("#1E222A");
    private static readonly Color Card = Color.Parse("#282C34");
    private static readonly Color Fg = Color.Parse("#E5E7EB");
    private static readonly Color Muted = Color.Parse("#9CA3AF");
    private static readonly Color Accent = Color.Parse("#60A5FA");
    private static readonly Color Green = Color.Parse("#10B981");
    private static readonly Color Gray = Color.Parse("#6B7280");

    private readonly ConfigStore _config;
    private readonly MasterAudioProcessor _processor;
    private readonly DispatcherTimer _timer;
    private bool _allowDestroy;
    private bool _syncing;

    private CheckBox _enabledBox = null!;
    private ComboBox _presetCombo = null!;
    private ProgressBar _inBar = null!;
    private ProgressBar _outBar = null!;
    private ProgressBar _gainBar = null!;
    private TextBlock _inText = null!;
    private TextBlock _outText = null!;
    private TextBlock _gainText = null!;
    private TextBlock _gateBadge = null!;
    private readonly List<Slider> _sliders = [];

    public NormalizerWindow(ConfigStore config, MasterAudioProcessor processor)
    {
        _config = config;
        _processor = processor;

        Title = "xPilot Audio Normalizer";
        Width = 460;
        Height = 620;
        MinWidth = 420;
        MinHeight = 560;
        Background = new SolidColorBrush(Bg);
        Foreground = new SolidColorBrush(Fg);

        try
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        catch
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Content = BuildContent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => RefreshTelemetry();
        _config.SettingsChanged += OnSettingsChanged;
        RefreshFromSettings(_config.Current);

        Closing += (_, e) =>
        {
            if (!_allowDestroy)
            {
                e.Cancel = true;
                Hide();
                _timer.Stop();
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                if (IsVisible) _timer.Start();
                else _timer.Stop();
            }
        };
    }

    public void AllowDestroy() => _allowDestroy = true;

    public new void Show()
    {
        RefreshFromSettings(_config.Current);
        _timer.Start();
        base.Show();
        try { Activate(); } catch { }
    }

    private Control BuildContent()
    {
        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 10,
            Margin = new Thickness(14),
        };

        root.Children.Add(BuildTopCard());
        root.Children.Add(BuildTelemetryCard());

        var scroll = new ScrollViewer
        {
            Content = BuildSlidersCard(),
            MaxHeight = 300,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        root.Children.Add(scroll);
        root.Children.Add(BuildFooter());

        return root;
    }

    private Control BuildTopCard()
    {
        var card = CardPanel();
        var stack = new StackPanel { Spacing = 8 };

        _enabledBox = new CheckBox
        {
            Content = "Enable Audio Normalizer",
            Foreground = new SolidColorBrush(Fg),
        };
        // ToggleButton.IsCheckedChanged was trimmed out of xPilot's Avalonia build;
        // PropertyChanged on ToggleButton.IsCheckedProperty survives trimming.
        _enabledBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;
            if (_syncing) return;
            _config.Update(s => s with { Enabled = _enabledBox.IsChecked == true });
        };
        stack.Children.Add(_enabledBox);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = "Preset:",
            Foreground = new SolidColorBrush(Muted),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _presetCombo = new ComboBox
        {
            ItemsSource = new List<string>(NormalizerPresets.Names),
            MinWidth = 220,
        };
        // SelectingItemsControl.SelectionChanged is trimmed from xPilot's build;
        // PropertyChanged on SelectedItemProperty survives trimming.
        _presetCombo.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty) return;
            if (_syncing) return;
            if (_presetCombo.SelectedItem is string name && NormalizerPresets.TryGet(name, out var preset))
            {
                _config.Update(_ => preset);
            }
        };
        row.Children.Add(_presetCombo);
        stack.Children.Add(row);

        card.Child = stack;
        return card;
    }

    private Control BuildTelemetryCard()
    {
        var card = CardPanel();
        var stack = new StackPanel { Spacing = 6 };

        stack.Children.Add(SectionTitle("Live Levels"));
        (_inBar, _inText) = MeterRow(stack, "Input Level", -60, 0);
        (_outBar, _outText) = MeterRow(stack, "Output Level", -60, 0);
        (_gainBar, _gainText) = MeterRow(stack, "Gain Adjustment", -24, 24);

        _gateBadge = new TextBlock
        {
            Text = "GATED",
            Foreground = new SolidColorBrush(Gray),
            FontWeight = FontWeight.Bold,
        };
        stack.Children.Add(_gateBadge);

        card.Child = stack;
        return card;
    }

    private static (ProgressBar Bar, TextBlock Text) MeterRow(StackPanel parent, string label, double min, double max)
    {
        parent.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Muted), FontSize = 12 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var bar = new ProgressBar
        {
            Minimum = min,
            Maximum = max,
            Value = min,
            MinWidth = 240,
            MaxWidth = 300,
            Height = 14,
            Foreground = new SolidColorBrush(Accent),
        };
        var text = new TextBlock
        {
            Text = "--",
            Foreground = new SolidColorBrush(Fg),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
        };
        row.Children.Add(bar);
        row.Children.Add(text);
        parent.Children.Add(row);
        return (bar, text);
    }

    private Control BuildSlidersCard()
    {
        var card = CardPanel();
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(SectionTitle("Parameters"));

        AddSlider(stack, "Target Loudness", "dBFS", -30.0, -6.0, s => s.TargetDbFs,
            (s, v) => s with { TargetDbFs = v, PresetName = "Custom" }, "{0:0.0} dBFS");
        AddSlider(stack, "Max Quiet Boost", "dB", 0.0, 24.0, s => s.MaxBoostDb,
            (s, v) => s with { MaxBoostDb = v, PresetName = "Custom" }, "+{0:0.0} dB");
        AddSlider(stack, "Max Loud Cut", "dB", 0.0, 24.0, s => s.MaxCutDb,
            (s, v) => s with { MaxCutDb = v, PresetName = "Custom" }, "-{0:0.0} dB");
        AddSlider(stack, "Compression Ratio", ":1", 1.0, 10.0, s => s.CompressionRatio,
            (s, v) => s with { CompressionRatio = v, PresetName = "Custom" }, "{0:0.0} : 1");
        AddSlider(stack, "Attack Speed (Loud Catch)", "ms", 1.0, 100.0, s => s.AttackMs,
            (s, v) => s with { AttackMs = v, PresetName = "Custom" }, "{0:0.0} ms");
        AddSlider(stack, "Release Speed (Recovery)", "ms", 50.0, 1500.0, s => s.ReleaseMs,
            (s, v) => s with { ReleaseMs = v, PresetName = "Custom" }, "{0:0} ms");
        AddSlider(stack, "Noise Gate Threshold", "dBFS", -60.0, -25.0, s => s.GateOpenDb,
            (s, v) => s with { GateOpenDb = v, GateCloseDb = v - 5.0, PresetName = "Custom" }, "{0:0.0} dBFS");
        AddSlider(stack, "Peak Limiter Ceiling", "dBFS", -6.0, -0.1, s => s.LimiterCeilingDb,
            (s, v) => s with { LimiterCeilingDb = v, PresetName = "Custom" }, "{0:0.0} dBFS");

        card.Child = stack;
        return card;
    }

    private void AddSlider(StackPanel parent, string title, string unit, double min, double max,
        Func<NormalizerSettings, double> get, Func<NormalizerSettings, double, NormalizerSettings> set, string format)
    {
        _ = unit;
        var label = new TextBlock { Text = title, Foreground = new SolidColorBrush(Fg), FontSize = 13 };
        var value = new TextBlock
        {
            Foreground = new SolidColorBrush(Muted),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(label);
        head.Children.Add(value);
        parent.Children.Add(head);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = (max - min) / 100.0,
        };
        slider.Tag = (get, set, value, format);
        // RangeBase.ValueChanged was trimmed out of xPilot's Avalonia build;
        // PropertyChanged on RangeBase.ValueProperty survives trimming.
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            if (_syncing || slider.Tag is not (Func<NormalizerSettings, double> g,
                    Func<NormalizerSettings, double, NormalizerSettings> st,
                    TextBlock vt, string fmt))
            {
                return;
            }
            vt.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, slider.Value);
            _ = g;
            _config.Update(s => st(s, slider.Value));
        };
        parent.Children.Add(slider);
        _sliders.Add(slider);
    }

    private Control BuildFooter()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var reset = new Button { Content = "Reset to Defaults" };
        reset.Click += (_, _) => _config.Update(_ => NormalizerPresets.Get(NormalizerPresets.Balanced));
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Hide();
        row.Children.Add(reset);
        row.Children.Add(close);
        return row;
    }

    private static Border CardPanel() => new()
    {
        Background = new SolidColorBrush(Card),
        // Single-arg CornerRadius(double) is trimmed from xPilot's Avalonia build.
        CornerRadius = new CornerRadius(8, 8, 8, 8),
        Padding = new Thickness(12),
    };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Fg),
        FontWeight = FontWeight.Bold,
        FontSize = 14,
    };

    private void OnSettingsChanged(NormalizerSettings settings)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                RefreshFromSettings(settings);
                return;
            }
            // Post(Action, DispatcherPriority): 2-arg overload survives trimming.
            Dispatcher.UIThread.Post(() => RefreshFromSettings(settings));
        }
        catch { }
    }

    private void RefreshFromSettings(NormalizerSettings s)
    {
        _syncing = true;
        try
        {
            _enabledBox.IsChecked = s.Enabled;
            var idx = NormalizerPresets.Names.ToList().IndexOf(s.PresetName);
            if (idx >= 0)
            {
                if (!_presetCombo.ItemsSource!.Cast<string>().Contains("Custom"))
                {
                    _presetCombo.ItemsSource = new List<string>(NormalizerPresets.Names);
                }
                _presetCombo.SelectedIndex = idx;
            }
            else
            {
                var items = new List<string>(NormalizerPresets.Names) { "Custom" };
                _presetCombo.ItemsSource = items;
                _presetCombo.SelectedIndex = items.Count - 1;
            }
            foreach (var slider in _sliders)
            {
                if (slider.Tag is (Func<NormalizerSettings, double> g,
                        Func<NormalizerSettings, double, NormalizerSettings>,
                        TextBlock vt, string fmt))
                {
                    slider.Value = g(s);
                    vt.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, slider.Value);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshTelemetry()
    {
        if (!IsVisible)
        {
            return;
        }
        var (inDb, outDb, gainDb, gateOpen) = _processor.GetAggregateTelemetry();
        _inBar.Value = ClampBar(inDb, -60, 0);
        _outBar.Value = ClampBar(outDb, -60, 0);
        _gainBar.Value = ClampBar(gainDb, -24, 24);

        _inText.Text = float.IsNegativeInfinity(inDb) ? "SILENT" : FormattableString.Invariant($"{inDb:0.0} dBFS");
        _outText.Text = float.IsNegativeInfinity(outDb) ? "SILENT" : FormattableString.Invariant($"{outDb:0.0} dBFS");
        _gainText.Text = Math.Abs(gainDb) < 0.05 ? "0.0 dB"
            : gainDb > 0 ? FormattableString.Invariant($"+{gainDb:0.0} dB (Boosting)") : FormattableString.Invariant($"{gainDb:0.0} dB (Attenuating)");
        _gateBadge.Text = gateOpen ? "GATE OPEN" : "GATED";
        _gateBadge.Foreground = new SolidColorBrush(gateOpen ? Green : Gray);
    }

    private static double ClampBar(double v, double min, double max)
    {
        if (double.IsNaN(v) || double.IsNegativeInfinity(v)) return min;
        if (double.IsPositiveInfinity(v)) return max;
        return Math.Clamp(v, min, max);
    }
}
