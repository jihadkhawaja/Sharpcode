using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Sharpcode.Core;

namespace Sharpcode;

internal sealed class ProblemAdornments : IBackgroundRenderer
{
    private (SimpleSegment Range, bool Error)[] marks = [];

    public KnownLayer Layer => KnownLayer.Background;

    public void SetProblems(TextView view, IEnumerable<CodeProblem> problems)
    {
        var document = view.Document;
        marks = problems.Where(problem => problem.Line > 0 && problem.Line <= document.LineCount).Select(problem =>
        {
            var line = document.GetLineByNumber(problem.Line);
            var start = Math.Clamp(problem.Column - 1, 0, Math.Max(0, line.Length - 1));
            var length = Math.Min(Math.Max(1, problem.Length), line.Length - start);
            return (new SimpleSegment(line.Offset + start, length), problem.IsError);
        }).Distinct().ToArray();
        view.InvalidateVisual();
    }

    public void Draw(TextView view, DrawingContext context)
    {
        if (marks.Length == 0 || !view.VisualLinesValid || view.VisualLines.Count == 0)
            return;
        var first = view.VisualLines[0].FirstDocumentLine.Offset;
        var last = view.VisualLines[^1].LastDocumentLine.EndOffset;
        var error = new Pen(App.Brush("Danger"), 1.3);
        var warning = new Pen(App.Brush("Warning"), 1.3);
        foreach (var mark in marks)
        {
            if (mark.Range.EndOffset < first || mark.Range.Offset > last)
                continue;
            foreach (var bounds in BackgroundGeometryBuilder.GetRectsForSegment(view, mark.Range))
            {
                var y = bounds.Bottom - 2.5;
                var end = Math.Min(Math.Max(bounds.Left + 4, bounds.Right), view.Bounds.Width);
                var down = false;
                for (var x = Math.Max(0, bounds.Left); x < end; x += 2)
                {
                    context.DrawLine(mark.Error ? error : warning, new Point(x, y + (down ? 1.5 : 0)),
                        new Point(Math.Min(x + 2, end), y + (down ? 0 : 1.5)));
                    down = !down;
                }
            }
        }
    }
}
