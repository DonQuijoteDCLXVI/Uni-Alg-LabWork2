using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FractalViewer;

public partial class MainWindow : Window
{
    // XAML raises SelectionChanged/ValueChanged while InitializeComponent() is still creating
    // controls. Until everything exists we ignore those events.
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

        StartColorBox.ItemsSource = ColorPalette.All;
        EndColorBox.ItemsSource = ColorPalette.All;
        BackgroundBox.ItemsSource = ColorPalette.All;

        StartColorBox.SelectedItem = ColorPalette.Find("Sienna");
        EndColorBox.SelectedItem = ColorPalette.Find("LimeGreen");
        BackgroundBox.SelectedItem = ColorPalette.Find("White");

        _ready = true;
        Redraw();
    }

    private FractalType SelectedType() =>
        Enum.Parse<FractalType>((string)((ComboBoxItem)TypeBox.SelectedItem).Tag);

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;

        FractalType type = SelectedType();

        // Changing Maximum/Value fires ValueChanged; suppress it and redraw once at the end.
        _ready = false;
        DepthSlider.Maximum = FractalLimits.MaxDepth(type);   // per-fractal depth cap
        DepthSlider.Value = FractalLimits.DefaultDepth(type);
        TreeOptions.Visibility = type == FractalType.Tree ? Visibility.Visible : Visibility.Collapsed;
        _ready = true;

        Redraw();
    }

    private void OnSettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Redraw();

    private void OnColorChanged(object sender, SelectionChangedEventArgs e) => Redraw();

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "fractal.png" };
        if (dialog.ShowDialog(this) != true) return;

        int w = (int)FractalView.ActualWidth, h = (int)FractalView.ActualHeight;
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(FractalView);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    // ── Push UI state into the canvas ────────────────────────────────────────

    private void Redraw()
    {
        if (!_ready) return;

        FractalType type = SelectedType();
        int depth = FractalLimits.Clamp(type, (int)Math.Round(DepthSlider.Value));

        var start = (NamedColor)StartColorBox.SelectedItem;
        var end = (NamedColor)EndColorBox.SelectedItem;
        var background = (NamedColor)BackgroundBox.SelectedItem;

        FractalView.Update(
            new FractalSettings(type, depth, start.Color, end.Color,
                                AngleSlider.Value, RatioSlider.Value),
            background.Color);

        DepthLabel.Text = $"Recursion depth: {depth} (max {FractalLimits.MaxDepth(type)})";
        AngleLabel.Text = $"Branch angle: {AngleSlider.Value:0}°";
        RatioLabel.Text = $"Branch length ratio: {RatioSlider.Value:0.00}";
        StatsText.Text = $"{FractalLimits.ElementCount(type, depth):N0} {FractalLimits.ElementName(type)} drawn";
    }
}
