using System.Windows.Media;

namespace FractalViewer;

/// <summary>A color plus its display name and a ready-made brush for the ComboBox swatches.</summary>
public sealed class NamedColor
{
    public NamedColor(string name, Color color)
    {
        Name = name;
        Color = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();               // frozen brushes are cheaper and thread-safe
        Brush = brush;
    }

    public string Name { get; }
    public Color Color { get; }
    public Brush Brush { get; }
}

/// <summary>A small, curated palette so the UI needs no external color-picker control.</summary>
public static class ColorPalette
{
    // NOTE: must be declared before 'All' - static initializers run in textual order.
    private static readonly string[] Names =
    {
        "Black", "White", "WhiteSmoke", "Silver", "DimGray",
        "SaddleBrown", "Sienna", "Peru", "Gold", "Orange", "OrangeRed", "Crimson",
        "HotPink", "MediumPurple", "Indigo", "MidnightBlue", "Navy", "DodgerBlue",
        "DeepSkyBlue", "Cyan", "Teal", "SeaGreen", "ForestGreen", "LimeGreen"
    };

    public static IReadOnlyList<NamedColor> All { get; } =
        Names.Select(n => new NamedColor(n, (Color)ColorConverter.ConvertFromString(n)))
             .ToList();

    public static NamedColor Find(string name) => All.First(c => c.Name == name);
}
