using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VrmToResonitePackage;

internal static class GuiApp
{
    public static int Run(IReadOnlyList<string> initialFiles)
    {
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        var window = new MainWindow(initialFiles);
        app.Run(window);
        return 0;
    }
}

internal sealed class MainWindow : Window
{
    private readonly Grid _root;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly TextBlock _detail;
    private readonly Button _settingsButton;
    private readonly DispatcherTimer _spinnerTimer;
    private readonly Border _spinner;
    private readonly RotateTransform _spinnerRotation = new();
    private readonly Border _packageIcon;
    private readonly GuiSettings _settings;
    private IReadOnlyList<string> _outputFiles = Array.Empty<string>();
    private string _lastLogPath;
    private bool _isConverting;
    private Point _dragStart;

    public MainWindow(IReadOnlyList<string> initialFiles)
    {
        _settings = GuiSettings.Load();

        Title = "VRM to ResonitePackage";
        Width = 760;
        Height = 500;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        AllowDrop = true;

        _root = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(246, 251, 255))
        };
        Content = _root;

        _settingsButton = new Button
        {
            Content = "⚙",
            Width = 44,
            Height = 44,
            FontSize = 22,
            ToolTip = "設定",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 18, 0),
            Background = Brushes.White,
            Foreground = AccentBrush,
            BorderBrush = new SolidColorBrush(Color.FromRgb(187, 229, 247)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        _settingsButton.Click += (_, _) => OpenSettings();

        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(32),
            MaxWidth = 640
        };
        _title = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            FontSize = 36,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(28, 45, 58)),
            TextWrapping = TextWrapping.Wrap
        };
        _message = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            TextAlignment = TextAlignment.Center,
            FontSize = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(66, 83, 96)),
            TextWrapping = TextWrapping.Wrap
        };
        _detail = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            TextAlignment = TextAlignment.Center,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(96, 111, 123)),
            TextWrapping = TextWrapping.Wrap
        };

        _spinner = new Border
        {
            Width = 68,
            Height = 68,
            Margin = new Thickness(0, 0, 0, 22),
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(6, 6, 6, 1),
            CornerRadius = new CornerRadius(34),
            RenderTransform = _spinnerRotation,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Visibility = Visibility.Collapsed
        };

        _packageIcon = BuildPackageIcon();
        _packageIcon.Visibility = Visibility.Collapsed;
        _packageIcon.MouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        _packageIcon.MouseMove += PackageIconOnMouseMove;

        center.Children.Add(_spinner);
        center.Children.Add(_packageIcon);
        center.Children.Add(_title);
        center.Children.Add(_message);
        center.Children.Add(_detail);
        _root.Children.Add(center);
        _root.Children.Add(_settingsButton);

        DragEnter += WindowOnDragEnter;
        Drop += WindowOnDrop;

        _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinnerTimer.Tick += (_, _) => _spinnerRotation.Angle = (_spinnerRotation.Angle + 7) % 360;

        ShowIdle();

        if (initialFiles.Count > 0)
        {
            Loaded += async (_, _) => await StartConversion(initialFiles);
        }
    }

    private static Brush AccentBrush { get; } = new SolidColorBrush(Color.FromRgb(35, 174, 219));

    private void ShowIdle()
    {
        _isConverting = false;
        _spinnerTimer.Stop();
        _spinner.Visibility = Visibility.Collapsed;
        _packageIcon.Visibility = Visibility.Collapsed;
        _settingsButton.IsEnabled = true;
        _title.Text = "VRM to ResonitePackage";
        _message.Text = "VRMファイルをドラッグアンドドロップしてください。";
        _detail.Text = "";
    }

    private void ShowConverting(string fileName)
    {
        _isConverting = true;
        _spinner.Visibility = Visibility.Visible;
        _packageIcon.Visibility = Visibility.Collapsed;
        _settingsButton.IsEnabled = false;
        _spinnerTimer.Start();
        _title.Text = "変換中...";
        _message.Text = fileName;
        _detail.Text = "ログは実行ファイル横の Logs フォルダに出力されます。";
    }

    private void ShowComplete(ConversionRunResult result)
    {
        _isConverting = false;
        _spinnerTimer.Stop();
        _spinner.Visibility = Visibility.Collapsed;
        _packageIcon.Visibility = Visibility.Visible;
        _settingsButton.IsEnabled = true;
        _outputFiles = result.OutputFiles;
        _lastLogPath = result.LogPath;
        _title.Text = result.ExitCode == 0 ? "変換完了！" : "変換に失敗しました";
        _message.Text = result.ExitCode == 0
            ? "このアイコンをResoniteの画面にドラッグしてください"
            : "ログを確認してください。";
        _detail.Text = result.ExitCode == 0
            ? Path.GetFileName(result.OutputFiles.FirstOrDefault() ?? "")
            : $"ログ: {result.LogPath}";
    }

    private async Task StartConversion(IReadOnlyList<string> files)
    {
        if (_isConverting)
        {
            return;
        }

        string[] vrmFiles = files
            .Where(file => string.Equals(Path.GetExtension(file), ".vrm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (vrmFiles.Length == 0)
        {
            _detail.Text = "VRMファイルを指定してください。";
            return;
        }

        ShowConverting(Path.GetFileName(vrmFiles[0]));
        try
        {
            string resonitePath = ResoniteLocator.Locate(_settings.ResonitePath);
            ResoniteLocator.InstallAssemblyResolver(resonitePath);
            Environment.CurrentDirectory = resonitePath;
            CliOptions options = _settings.ToCliOptions(vrmFiles);
            ConversionRunResult result = await Task.Run(() => Converter.RunDetailed(options, resonitePath));
            ShowComplete(result);
        }
        catch (Exception ex)
        {
            _isConverting = false;
            _spinnerTimer.Stop();
            _spinner.Visibility = Visibility.Collapsed;
            _packageIcon.Visibility = Visibility.Collapsed;
            _settingsButton.IsEnabled = true;
            _title.Text = "変換に失敗しました";
            _message.Text = ex.Message;
            _detail.Text = string.IsNullOrWhiteSpace(_lastLogPath) ? "" : $"ログ: {_lastLogPath}";
        }
    }

    private void WindowOnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_isConverting
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void WindowOnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _isConverting)
        {
            return;
        }
        await StartConversion((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    private void PackageIconOnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _outputFiles.Count == 0)
        {
            return;
        }
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var files = new StringCollection();
        foreach (string file in _outputFiles.Where(File.Exists))
        {
            files.Add(file);
        }
        if (files.Count == 0)
        {
            return;
        }

        var data = new DataObject();
        data.SetFileDropList(files);
        DragDrop.DoDragDrop(_packageIcon, data, DragDropEffects.Copy);
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.CopyFrom(dialog.Settings);
            _settings.Save();
        }
    }

    private static Border BuildPackageIcon()
    {
        var icon = new Border
        {
            Width = 116,
            Height = 132,
            Margin = new Thickness(0, 0, 0, 22),
            Background = Brushes.White,
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand
        };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = "R",
            FontSize = 52,
            FontWeight = FontWeights.Bold,
            Foreground = AccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        var label = new TextBlock
        {
            Text = ".resonitepackage",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(66, 83, 96)),
            Margin = new Thickness(6, 0, 6, 10),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(label, 1);
        panel.Children.Add(label);
        icon.Child = panel;
        return icon;
    }
}

internal sealed class SettingsWindow : Window
{
    private const string DefaultOutputDirectoryText = "入力ファイルと同じフォルダ";
    private const string AutoDetectText = "自動検出";
    private const string NoRescaleText = "変更しない";
    private const string AutoValueText = "自動";

    private readonly TextBox _outputDirectory = new();
    private readonly TextBox _resonitePath = new();
    private readonly CheckBox _noAvatar = new() { Content = "アバターセットアップを行わない" };
    private readonly CheckBox _faceTracking = new() { Content = "フェイストラッキング用ドライバーを生成" };
    private readonly CheckBox _noProtection = new() { Content = "アバター保護を付けない" };
    private readonly CheckBox _keepWorkingFiles = new() { Content = "作業用一時ファイルを残す" };
    private readonly TextBox _height = new();
    private readonly TextBox _viewForward = new();
    private readonly TextBox _viewUp = new();
    private readonly TextBox _nearClip = new();
    private readonly TextBox _importTimeout = new();

    public SettingsWindow(GuiSettings settings)
    {
        Settings = settings.Clone();
        Title = "設定";
        Width = 560;
        Height = 620;
        MinWidth = 480;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var panel = new StackPanel { Margin = new Thickness(28) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        panel.Children.Add(Header("変換オプション"));
        panel.Children.Add(PathRow("出力先フォルダ", _outputDirectory));
        panel.Children.Add(PathRow("Resoniteフォルダ", _resonitePath));
        panel.Children.Add(_noAvatar);
        panel.Children.Add(_faceTracking);
        panel.Children.Add(_noProtection);
        panel.Children.Add(_keepWorkingFiles);
        panel.Children.Add(Field("身長(m)", _height));
        panel.Children.Add(Field("視点の前方オフセット(m)", _viewForward));
        panel.Children.Add(Field("視点の上方オフセット(m)", _viewUp));
        panel.Children.Add(Field("NearClip(m)", _nearClip));
        panel.Children.Add(Field("インポートタイムアウト(秒)", _importTimeout));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0)
        };
        var cancel = new Button { Content = "キャンセル", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button
        {
            Content = "保存",
            MinWidth = 96,
            Background = MainWindowAccentBrush,
            Foreground = Brushes.White,
            BorderBrush = MainWindowAccentBrush
        };
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) => SaveAndClose();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        LoadValues();
    }

    public GuiSettings Settings { get; private set; }

    private static Brush MainWindowAccentBrush { get; } = new SolidColorBrush(Color.FromRgb(35, 174, 219));

    private void LoadValues()
    {
        _outputDirectory.Text = Settings.OutputDirectory ?? DefaultOutputDirectoryText;
        _resonitePath.Text = Settings.ResonitePath ?? AutoDetectText;
        _noAvatar.IsChecked = Settings.NoAvatar;
        _faceTracking.IsChecked = Settings.FaceTracking;
        _noProtection.IsChecked = Settings.NoProtection;
        _keepWorkingFiles.IsChecked = Settings.KeepWorkingFiles;
        _height.Text = Settings.TargetHeight?.ToString(CultureInfo.InvariantCulture) ?? NoRescaleText;
        _viewForward.Text = Settings.ViewForward?.ToString(CultureInfo.InvariantCulture) ?? AutoValueText;
        _viewUp.Text = Settings.ViewUp?.ToString(CultureInfo.InvariantCulture) ?? AutoValueText;
        _nearClip.Text = Settings.NearClip?.ToString(CultureInfo.InvariantCulture) ?? "0.075";
        _importTimeout.Text = Settings.ImportTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private void SaveAndClose()
    {
        try
        {
            Settings.OutputDirectory = EmptyToNull(_outputDirectory.Text, DefaultOutputDirectoryText);
            Settings.ResonitePath = EmptyToNull(_resonitePath.Text, AutoDetectText);
            Settings.NoAvatar = _noAvatar.IsChecked == true;
            Settings.FaceTracking = _faceTracking.IsChecked == true;
            Settings.NoProtection = _noProtection.IsChecked == true;
            Settings.KeepWorkingFiles = _keepWorkingFiles.IsChecked == true;
            Settings.TargetHeight = ParseNullableFloat(_height.Text, "身長", NoRescaleText);
            Settings.ViewForward = ParseNullableFloat(_viewForward.Text, "視点の前方オフセット", AutoValueText);
            Settings.ViewUp = ParseNullableFloat(_viewUp.Text, "視点の上方オフセット", AutoValueText);
            Settings.NearClip = ParseNullableFloat(_nearClip.Text, "NearClip");
            Settings.ImportTimeoutSeconds = (int)(ParseNullableFloat(_importTimeout.Text, "インポートタイムアウト") ?? 300);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(28, 45, 58)),
        Margin = new Thickness(0, 0, 0, 18)
    };

    private static FrameworkElement Field(string label, TextBox box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(66, 83, 96)) });
        box.Margin = new Thickness(0, 6, 0, 0);
        box.Height = 32;
        panel.Children.Add(box);
        return panel;
    }

    private FrameworkElement PathRow(string label, TextBox box)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(66, 83, 96)) });
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.Height = 32;
        row.Children.Add(box);
        var browse = new Button { Content = "参照", MinWidth = 72, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog();
            if (!string.IsNullOrWhiteSpace(box.Text))
            {
                dialog.InitialDirectory = box.Text;
            }
            if (dialog.ShowDialog(this) == true)
            {
                box.Text = dialog.FolderName;
            }
        };
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        panel.Children.Add(row);
        return panel;
    }

    private static string EmptyToNull(string value, string defaultText = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string trimmed = value.Trim();
        return string.Equals(trimmed, defaultText, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static float? ParseNullableFloat(string value, string label, string defaultText = null)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), defaultText, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
        {
            throw new FormatException($"{label} は数値で入力してください。");
        }
        return result;
    }
}

internal sealed class GuiSettings
{
    public string OutputDirectory { get; set; }
    public string ResonitePath { get; set; }
    public bool NoAvatar { get; set; }
    public bool FaceTracking { get; set; }
    public bool NoProtection { get; set; }
    public bool KeepWorkingFiles { get; set; }
    public float? TargetHeight { get; set; }
    public float? ViewForward { get; set; }
    public float? ViewUp { get; set; }
    public float? NearClip { get; set; }
    public int ImportTimeoutSeconds { get; set; } = 300;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VrmToResonitePackage",
        "settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(SettingsPath)) ?? new GuiSettings();
            }
        }
        catch
        {
        }
        return new GuiSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public GuiSettings Clone() => new()
    {
        OutputDirectory = OutputDirectory,
        ResonitePath = ResonitePath,
        NoAvatar = NoAvatar,
        FaceTracking = FaceTracking,
        NoProtection = NoProtection,
        KeepWorkingFiles = KeepWorkingFiles,
        TargetHeight = TargetHeight,
        ViewForward = ViewForward,
        ViewUp = ViewUp,
        NearClip = NearClip,
        ImportTimeoutSeconds = ImportTimeoutSeconds
    };

    public void CopyFrom(GuiSettings other)
    {
        OutputDirectory = other.OutputDirectory;
        ResonitePath = other.ResonitePath;
        NoAvatar = other.NoAvatar;
        FaceTracking = other.FaceTracking;
        NoProtection = other.NoProtection;
        KeepWorkingFiles = other.KeepWorkingFiles;
        TargetHeight = other.TargetHeight;
        ViewForward = other.ViewForward;
        ViewUp = other.ViewUp;
        NearClip = other.NearClip;
        ImportTimeoutSeconds = other.ImportTimeoutSeconds;
    }

    public CliOptions ToCliOptions(IEnumerable<string> files)
    {
        var options = new CliOptions
        {
            OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : Path.GetFullPath(OutputDirectory),
            ResonitePath = ResonitePath,
            NoAvatar = NoAvatar,
            FaceTracking = FaceTracking,
            NoProtection = NoProtection,
            KeepWorkingFiles = KeepWorkingFiles,
            TargetHeight = TargetHeight,
            ViewForward = ViewForward,
            ViewUp = ViewUp,
            NearClip = NearClip,
            ImportTimeoutSeconds = ImportTimeoutSeconds <= 0 ? 300 : ImportTimeoutSeconds
        };
        foreach (string file in files)
        {
            options.InputFiles.Add(Path.GetFullPath(file));
        }
        return options;
    }
}
