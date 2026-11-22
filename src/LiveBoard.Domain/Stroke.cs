using System.Text.RegularExpressions;

namespace LiveBoard.Domain;

public enum StrokeTool
{
    Pen,
    Highlighter,
}

/// <summary>
/// One freehand line on a board's whiteboard. Points are stored flattened as [x0, y0, x1, y1, ...] in a fixed
/// logical canvas of <see cref="CanvasWidth"/> x <see cref="CanvasHeight"/>, so every client renders the same
/// drawing whatever its screen size. Strokes are immutable: erasing deletes whole strokes.
/// The ID is chosen by the client so the live preview others saw can be matched to the saved stroke.
/// </summary>
public sealed partial class Stroke
{
    public const double CanvasWidth = 1600;
    public const double CanvasHeight = 1000;
    public const double MinWidth = 1;
    public const double MaxWidth = 64;
    public const int MaxPoints = 5000;
    public const int MaxStrokesPerBoard = 5000;

    private Stroke()
    {
        AuthorId = string.Empty;
        Color = string.Empty;
        Points = [];
    }

    public Guid Id { get; private set; }
    public Guid BoardId { get; private set; }
    public string AuthorId { get; private set; }
    public StrokeTool Tool { get; private set; }
    public string Color { get; private set; }
    public double Width { get; private set; }
    public double[] Points { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static Stroke Create(Guid id, Guid boardId, string authorId, StrokeTool tool, string color, double width, IReadOnlyList<double>? points, DateTime now)
    {
        if (id == Guid.Empty)
            throw new DomainValidationException("Stroke ID is required.");

        return new Stroke
        {
            Id = id,
            BoardId = boardId,
            AuthorId = authorId,
            Tool = Enum.IsDefined(tool) ? tool : throw new DomainValidationException("Unknown drawing tool."),
            Color = ValidColor(color),
            Width = ValidWidth(width),
            Points = NormalizePoints(points, MaxPoints),
            CreatedAt = now,
        };
    }

    public static string ValidColor(string? color) =>
        color is not null && HexColor().IsMatch(color)
            ? color.ToLowerInvariant()
            : throw new DomainValidationException("Color must be a hex value like #1f2937.");

    public static double ValidWidth(double width) =>
        double.IsFinite(width) && width >= MinWidth && width <= MaxWidth
            ? Math.Round(width, 1)
            : throw new DomainValidationException($"Width must be between {MinWidth} and {MaxWidth}.");

    /// <summary>
    /// Checks shape and size, clamps points to the canvas (a pointer dragged past the edge is still a valid stroke)
    /// and rounds to 0.1 units, which is invisible at any zoom but keeps documents and broadcasts small.
    /// </summary>
    public static double[] NormalizePoints(IReadOnlyList<double>? points, int maxPoints)
    {
        if (points is null || points.Count < 2 || points.Count % 2 != 0)
            throw new DomainValidationException("A stroke needs at least one point, as x/y pairs.");
        if (points.Count / 2 > maxPoints)
            throw new DomainValidationException($"A stroke can have at most {maxPoints} points.");

        var result = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var value = points[i];
            if (!double.IsFinite(value))
                throw new DomainValidationException("Stroke points must be numbers.");
            var max = i % 2 == 0 ? CanvasWidth : CanvasHeight;
            result[i] = Math.Round(Math.Clamp(value, 0, max), 1);
        }
        return result;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
