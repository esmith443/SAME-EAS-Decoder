using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SameEasDecoder.ViewModels;

namespace SameEasDecoder.Controls;

public sealed class SegmentText : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<LogSegment>), typeof(SegmentText),
        new PropertyMetadata(null, (d, _) => ((SegmentText)d).Rebuild()));

    public IReadOnlyList<LogSegment>? Segments
    {
        get => (IReadOnlyList<LogSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private void Rebuild()
    {
        Inlines.Clear();
        if (Segments is null) return;
        foreach (var s in Segments)
        {
            var run = new Run(s.Text);
            switch (s.Style)
            {
                case SegmentStyle.Dim:
                    run.Foreground = Res("Text3");
                    break;
                case SegmentStyle.Good:
                    run.Foreground = Res("Green");
                    break;
                case SegmentStyle.Code:
                    run.Foreground = Res("Accent");
                    break;
                case SegmentStyle.Mismatch:
                    run.Foreground = Res("Amber");
                    run.Background = Res("AmberSoft");
                    break;
                case SegmentStyle.Error:
                    run.Foreground = Res("Red");
                    run.Background = Res("RedSoft");
                    break;
                default:
                    run.Foreground = Res("Text");
                    break;
            }
            Inlines.Add(run);
        }
    }

    private Brush Res(string key) => (Brush)FindResource(key);
}
