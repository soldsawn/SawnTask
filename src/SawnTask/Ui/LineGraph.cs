using System.Windows;
using System.Windows.Media;
using SawnTask.Core;

namespace SawnTask.Ui;

// A hand-drawn line graph. Newest sample at the right edge; NaN values end the
// current line segment, so missing data and graph breaks show as gaps.
//
// Smooth motion: data still arrives once per update
// interval, but between updates the whole graph slides left in small steps (see Steps) at up
// to 60 frames per second, so it scrolls continuously instead of jumping. Only the
// drawing moves; no values are invented. With Fps = 0 the graph redraws only when new
// data arrives (less CPU).
public sealed class LineGraph : FrameworkElement
{
    // Shared settings (Settings page): frames per second while sliding and curve smoothing.
    public static int Fps { get; set; } = 60;
    public static bool SmoothCurves { get; set; } = true;

    static readonly Brush SelectionFill = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x2E, 0xB8, 0xAC)));
    static readonly Pen SelectionPen = Frozen(new Pen(Brushes.White, 1.5) { DashStyle = new DashStyle([4, 3], 0) });

    // Theme brushes are looked up once per theme, not on every frame.
    static Brush? backgroundBrush;
    static Pen? gridPenCache;
    static LineGraph() => Appearance.Changed += () => (backgroundBrush, gridPenCache) = (null, null);

    static readonly List<LineGraph> Animating = [];
    static bool hooked;
    static TimeSpan lastFrame;

    readonly List<(Series Series, Pen Pen, double? Max)> lines = [];
    DateTime arrivedUtc = DateTime.MinValue;
    TimeSpan slideDuration;

    public LineGraph()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        // A graph on a page that was not showing did not slide meanwhile; draw it afresh when it appears.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) InvalidateVisual(); };
    }

    public double Max { get; set; } = 100;
    public bool AutoScale { get; set; }
    public double MinAutoMax { get; set; } = 1024;
    public double CurrentMax { get; private set; } = 100;
    public int GridColumns { get; set; } = 10;
    public int GridRows { get; set; } = 4;

    // Axis labels beside the frame (the CPU graph: 0% 50% 100% in green on the left, the
    // temperature scale in orange on the right). Labels run top to bottom; setting an axis
    // makes room for it, the plot shrinks.
    public (Color Color, string[] Labels)? LeftAxis { get; set; }
    public (Color Color, string[] Labels)? RightAxis { get; set; }
    const double AxisWidth = 38;

    // Compressed time axis (a long history without making recent data tiny):
    // the newest samples are drawn wide and older ones are squeezed together towards the
    // left, logarithmically. The vertical grid then marks equal time steps (GridSamples),
    // so its lines crowd together on the left and move along with the data.
    public bool Compress { get; set; }

    // How many of the newest samples the graph spans (null: the whole series). The Performance
    // page shows one minute, like Task Manager, even though the series keeps more.
    public int? VisibleSamples { get; set; }

    int Window() => Math.Max(2, VisibleSamples is int n ? Math.Min(n, Capacity()) : Capacity());
    int Capacity() => lines.Count > 0 ? lines[0].Series.Capacity : 2;

    // A period picked by dragging on the graph (CPU history): the sample numbers of
    // its two ends (the series' Total counts samples, so the period scrolls away with its
    // data). A click without dragging clears it.
    public bool Selectable { get; set; }
    // From == To is a single moment (a click); From < To is a period (a drag).
    public (long From, long To)? Selection { get; set; }
    public event Action? SelectionChanged;

    // The sample under the mouse while it hovers over the graph (null when it does not):
    // drawn as one dashed line, so a moment can be looked at without clicking.
    public long? Hover { get; private set; }
    public event Action? HoverChanged;
    Point downPoint;
    long? dragStart;
    bool dragged;
    public int GridSamples { get; set; } = 30;
    const double CompressK = 12;

    // own scale: this line is drawn against 0..ownMax instead of the graph's Max (e.g. temperature).
    public void AddLine(Series series, Color color, double thickness = 1.5, double? ownMax = null)
    {
        var pen = new Pen(Frozen(new SolidColorBrush(color)), thickness) { LineJoin = PenLineJoin.Bevel };
        lines.Add((series, Frozen(pen), ownMax));
        InvalidateVisual();
    }

    (double Left, double Width) Plot() =>
        (LeftAxis is null ? 0 : AxisWidth, ActualWidth - (LeftAxis is null ? 0 : AxisWidth) - (RightAxis is null ? 0 : AxisWidth));

    // The sample under a mouse position: the opposite of XOfAge in OnRender.
    long SampleAt(double mouseX)
    {
        var (left, w) = Plot();
        var series = lines[0].Series;
        int capacity = Window();
        double x = Math.Clamp(mouseX - left, 0, w), slide = 1 - ShownProgress();
        double age = Compress
            ? (Math.Exp((1 - x / w) * Math.Log(1 + CompressK)) - 1) / CompressK * (capacity - 1) + slide
            : (w - x + w / (capacity - 1) * slide) / (w / (capacity - 1));
        return Math.Clamp(series.Total - 1 - (long)Math.Round(age), Math.Max(0, series.Total - series.Count), series.Total - 1);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!Selectable || lines.Count == 0 || lines[0].Series.Total == 0) return;
        CaptureMouse();
        downPoint = e.GetPosition(this);
        dragStart = SampleAt(downPoint.X);
        dragged = false;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (Selectable && !IsMouseCaptured && lines.Count > 0 && lines[0].Series.Total > 0)
        {
            long hover = SampleAt(e.GetPosition(this).X);
            if (hover != Hover)
            {
                Hover = hover;
                InvalidateVisual();
                HoverChanged?.Invoke();
            }
        }
        if (dragStart is not long start || !IsMouseCaptured) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - downPoint.X) > 3) dragged = true;
        if (!dragged) return;
        long now = SampleAt(p.X);
        Selection = (Math.Min(start, now), Math.Max(start, now));
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (dragStart is null) return;
        ReleaseMouseCapture();
        dragStart = null;
        if (dragged) return;
        // A click pins the moment under the mouse; clicking again clears it.
        Selection = Selection is null ? (SampleAt(downPoint.X), SampleAt(downPoint.X)) : null;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        if (Hover is null || IsMouseCaptured) return;
        Hover = null;
        InvalidateVisual();
        HoverChanged?.Invoke();
    }

    // Called once per new sample. The graph is first drawn one step to the right (so it
    // looks exactly like the previous frame), then slides left over one update interval.
    public void Advance(TimeSpan interval)
    {
        // Nobody sees a hidden graph. Starting its slide would also keep WPF's render loop running at 60 fps for nothing.
        if (!IsVisible) return;
        if (Fps <= 0 || interval <= TimeSpan.Zero)
        {
            arrivedUtc = DateTime.MinValue;
            InvalidateVisual();
            return;
        }
        arrivedUtc = DateTime.UtcNow;
        slideDuration = interval;
        shownStep = 0;
        if (!Animating.Contains(this)) Animating.Add(this);
        if (!hooked)
        {
            CompositionTarget.Rendering += OnFrame;
            hooked = true;
        }
        InvalidateVisual();
    }

    // One handler for all graphs, driven by WPF's render loop (normally 60 Hz). A graph is
    // redrawn only when its picture moves by a whole pixel: the newest sample travels a few
    // pixels per update, so most of the 60 frames would draw exactly the same image again.
    static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (Fps > 0 && Fps < 60 && (now - lastFrame).TotalMilliseconds < 1000.0 / Fps - 2) return;
        lastFrame = now;

        for (int i = Animating.Count - 1; i >= 0; i--)
        {
            var graph = Animating[i];
            int step = graph.StepNow();
            if (graph.IsVisible && step != graph.shownStep)
            {
                graph.shownStep = step;
                graph.InvalidateVisual();
            }
            if (graph.SlideProgress() >= 1) Animating.RemoveAt(i);
        }
        if (Animating.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            hooked = false;
        }
    }

    // The slide is drawn in steps of a whole pixel, or a quarter pixel when the newest sample
    // moves less than a few pixels per update (the 60-second per-core graphs move 1.5 px:
    // whole pixels made them jump twice per update; quarter pixels, smoothed, look continuous).
    int shownStep;
    const double SmallMove = 3, FineSteps = 4;

    int Steps()
    {
        int capacity = Window();
        double w = Plot().Width, pixels = Compress
            ? w * Math.Log(1 + CompressK / (capacity - 1)) / Math.Log(1 + CompressK)
            : w / (capacity - 1);
        return Math.Max(1, (int)Math.Round(pixels < SmallMove ? pixels * FineSteps : pixels));
    }

    int StepNow() => (int)Math.Floor(SlideProgress() * Steps());

    // The progress actually drawn: the step reached, so a frame looks the same until the next step.
    double ShownProgress()
    {
        double progress = SlideProgress();
        if (progress >= 1) return 1;
        int steps = Steps();
        return Math.Min(1, Math.Floor(progress * steps) / steps);
    }

    double SlideProgress() =>
        arrivedUtc == DateTime.MinValue || Fps <= 0 ? 1
        : Math.Clamp((DateTime.UtcNow - arrivedUtc) / slideDuration, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        double left = LeftAxis is null ? 0 : AxisWidth, right = RightAxis is null ? 0 : AxisWidth;
        if (left + right > 0)
        {
            DrawAxis(dc, LeftAxis, 0, left, h, alignRight: true);
            DrawAxis(dc, RightAxis, w - right, right, h, alignRight: false);
            dc.PushTransform(new TranslateTransform(left, 0));
            w -= left + right;
            // The newest sample slides in from beyond the right edge; keep it out of the axis labels.
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        }

        // Colours come from the active theme (Themes/Dark.xaml or Light.xaml).
        var background = backgroundBrush ??= TryFindResource("GraphBg") as Brush ?? Brushes.Black;
        var gridPen = gridPenCache ??= Frozen(new Pen(TryFindResource("GraphGrid") as Brush ?? Brushes.DimGray, 1));
        dc.DrawRectangle(background, gridPen, new Rect(0.5, 0.5, w - 1, h - 1));

        // All grid lines are one geometry: one primitive for the renderer instead of dozens.
        var gridGeometry = new StreamGeometry();
        using var grid = gridGeometry.Open();
        void GridLine(double x1, double y1, double x2, double y2)
        {
            grid.BeginFigure(new Point(x1, y1), false, false);
            grid.LineTo(new Point(x2, y2), true, false);
        }

        double max = Max;
        if (AutoScale)
            max = Math.Max(MinAutoMax, lines.Count == 0 ? 0 : lines.Max(l => l.Series.MaxValue()) * 1.15);
        CurrentMax = max;

        int capacity = Window();
        double dx = w / (capacity - 1);
        double slide = 1 - ShownProgress(); // in samples: 1 = the newest sample was just added
        double offset = dx * slide;
        double ageScale = Math.Log(1 + CompressK);
        // x of a sample that is `age` samples old (0 = newest, negative = not yet slid in).
        double XOfAge(double age) => Compress
            ? w * (1 - Math.Log(1 + CompressK * age / (capacity - 1)) / ageScale)
            : w - age * dx + offset;

        // The vertical grid lines scroll with the data, so the motion is visible even on a flat line.
        if (Compress)
        {
            long total = lines.Count > 0 ? lines[0].Series.Total : 0;
            double first = total == 0 ? 0 : (total - 1) % GridSamples; // age of the newest grid sample
            for (double age = first; age < capacity; age += GridSamples)
            {
                double x = XOfAge(age - slide);
                if (x > 1 && x < w - 1) GridLine(x, 0, x, h);
            }
        }
        else
        {
            double gridStep = w / GridColumns;
            // The grid moves exactly like the data line, so both slide together.
            double gridShift = (offset - (lines.Count > 0 ? lines[0].Series.Total : 0) * dx) % gridStep;
            if (gridShift < 0) gridShift += gridStep;
            for (double x = gridShift; x < w; x += gridStep)
                if (x > 1) GridLine(x, 0, x, h);
        }
        for (int i = 1; i < GridRows; i++) GridLine(0, h * i / GridRows, w, h * i / GridRows);
        grid.Close();
        gridGeometry.Freeze();
        dc.DrawGeometry(null, gridPen, gridGeometry);

        if (Selection is { } sel && lines.Count > 0)
        {
            long total = lines[0].Series.Total;
            double XOfSample(long index) => Math.Clamp(XOfAge(total - 1 - index - (Compress ? slide : 0)), 0, w);
            double x1 = XOfSample(sel.From), x2 = Math.Max(XOfSample(sel.To), x1 + 1);
            if (sel.To > sel.From) dc.DrawRectangle(SelectionFill, null, new Rect(x1, 0, x2 - x1, h));
            dc.DrawLine(SelectionPen, new Point(x1, 0), new Point(x1, h));
            if (sel.To > sel.From) dc.DrawLine(SelectionPen, new Point(x2, 0), new Point(x2, h));
        }
        else if (Hover is long hover && lines.Count > 0)
        {
            double hx = Math.Clamp(XOfAge(lines[0].Series.Total - 1 - hover - (Compress ? slide : 0)), 0, w);
            dc.DrawLine(SelectionPen, new Point(hx, 0), new Point(hx, h));
        }

        // Without Compress the slide is a pure shift, so the lines are built once per sample
        // (at offset 0) and only moved afterwards: the renderer keeps what it made of them.
        long stamp = lines.Count > 0 ? lines[0].Series.Total : 0;
        bool shiftOnly = !Compress;
        if (shiftOnly)
        {
            var key = (stamp, w, h, max, lines.Count * 100000 + capacity);
            if (key != cacheKey || cachedLines is null)
            {
                cachedLines = BuildLines(w, h, max, 0, 0);
                cacheKey = key;
            }
            dc.PushTransform(new TranslateTransform(offset, 0));
            foreach (var (geometry, pen) in cachedLines) dc.DrawGeometry(null, pen, geometry);
            dc.Pop();
        }
        else
            foreach (var (geometry, pen) in BuildLines(w, h, max, slide, 0)) dc.DrawGeometry(null, pen, geometry);
        if (left + right > 0)
        {
            dc.Pop(); // clip
            dc.Pop(); // translate
        }
    }

    (long, double, double, double, int) cacheKey;
    List<(StreamGeometry, Pen)>? cachedLines;

    // One geometry per line. slide matters only on a compressed time axis (see XOfAge).
    List<(StreamGeometry, Pen)> BuildLines(double w, double h, double max, double slide, double offset)
    {
        int capacity = Window();
        double dx = w / (capacity - 1);
        double ageScale = Math.Log(1 + CompressK);
        double XOfAge(double age) => Compress
            ? w * (1 - Math.Log(1 + CompressK * age / (capacity - 1)) / ageScale)
            : w - age * dx + offset;
        var result = new List<(StreamGeometry, Pen)>(lines.Count);
        foreach (var (series, pen, ownMax) in lines)
        {
            if (series.Count < 2) continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var run = new List<Point>();
                // Only the samples inside the window (one more, so the line reaches the left edge).
                for (int i = Math.Max(0, series.Count - capacity - 1); i <= series.Count; i++)
                {
                    double v = i < series.Count ? series[i] : double.NaN;
                    if (double.IsNaN(v))
                    {
                        DrawRun(g, run, h);
                        run.Clear();
                        continue;
                    }
                    run.Add(new Point(XOfAge(series.Count - 1 - i - (Compress ? slide : 0)), h - 1 - Math.Clamp(v / (ownMax ?? max), 0, 1) * (h - 2)));
                }
            }
            geometry.Freeze();
            result.Add((geometry, pen));
        }
        return result;
    }

    // Three labels (top, middle, bottom) in the axis colour, beside the frame.
    void DrawAxis(DrawingContext dc, (Color Color, string[] Labels)? axis, double x, double width, double h, bool alignRight)
    {
        if (axis is not { } a) return;
        var brush = Frozen(new SolidColorBrush(a.Color));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < a.Labels.Length; i++)
        {
            var text = new FormattedText(a.Labels[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, brush, dpi);
            double y = a.Labels.Length < 2 ? 0 : (h - text.Height) * i / (a.Labels.Length - 1);
            dc.DrawText(text, new Point(alignRight ? x + width - text.Width - 5 : x + 5, y));
        }
    }

    // Draws one unbroken run of points, as straight segments or as a smooth curve
    // (Catmull-Rom spline turned into Bezier segments; control points are kept inside
    // the graph so the curve never shows values above the top or below zero).
    //
    // This runs for every graph on every frame, and WPF re-flattens a Bezier into many small
    // segments each time, so it is kept cheap: points closer than a pixel and a half are merged
    // (the compressed old part of a long history packs dozens of samples into a few pixels),
    // and only segments wide enough for the curve to be seen are drawn as Beziers.
    const double MinStep = 1.5, CurveStep = 4;

    static List<Point> Thin(List<Point> p)
    {
        if (p.Count < 3) return p;
        var kept = new List<Point>(p.Count);
        for (int i = 0; i < p.Count;)
        {
            Point first = p[i];
            kept.Add(first);
            int j = i + 1, far = -1;
            double best = 0.5; // a bucket's biggest swing is kept, so peaks survive
            for (; j < p.Count && p[j].X - first.X < MinStep; j++)
                if (Math.Abs(p[j].Y - first.Y) > best) (best, far) = (Math.Abs(p[j].Y - first.Y), j);
            if (far >= 0) kept.Add(p[far]);
            i = j;
        }
        if (kept[^1] != p[^1]) kept.Add(p[^1]); // the newest sample is always exact
        return kept;
    }

    static void DrawRun(StreamGeometryContext g, List<Point> points, double h)
    {
        if (points.Count < 2) return;
        var p = Thin(points);
        g.BeginFigure(p[0], false, false);
        if (!SmoothCurves || p.Count < 3)
        {
            for (int i = 1; i < p.Count; i++) g.LineTo(p[i], true, true);
            return;
        }
        for (int i = 0; i < p.Count - 1; i++)
        {
            if (p[i + 1].X - p[i].X < CurveStep)
            {
                g.LineTo(p[i + 1], true, true);
                continue;
            }
            Point p0 = p[Math.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(p.Count - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, 1, h - 1));
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, 1, h - 1));
            g.BezierTo(c1, c2, p2, true, true);
        }
    }

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
