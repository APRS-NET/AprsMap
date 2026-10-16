// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Mapsui;
using Mapsui.Projections;
using SkiaSharp;

namespace AprsMap.UI.Map;

/// <summary>How one station's track is drawn (docs/features/F1.5-tracks.md, behavior 6).</summary>
internal enum TrackStyle
{
    /// <summary>Thin, in the track colour at reduced opacity.</summary>
    Other,

    /// <summary>Full opacity, with a dot at each transmitted point.</summary>
    Selected,

    /// <summary>Thicker, in the accent colour.</summary>
    Followed,
}

/// <summary>
/// The stations' tracks in map coordinates (docs/features/F1.5-tracks.md), and the paths that draw them. Built on the UI
/// thread each frame and handed to the render thread finished, so drawing reads nothing from the control.
/// </summary>
internal sealed class TrackLayer
{
    /// <summary>The age buckets a track is split into, oldest first, and how opaque each is (behavior 7).</summary>
    private static readonly float[] AgeAlpha = [0.35f, 0.55f, 0.8f, 1f];

    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);

    /// <summary>How many tracks are held.</summary>
    public int Count => _tracks.Count;

    /// <summary>The points held for <paramref name="name"/>, in map coordinates, oldest first; empty for none.</summary>
    public IReadOnlyList<MPoint> PointsOf(string name) => _tracks.TryGetValue(name, out var track) ? track.Points : [];

    public void Clear() => _tracks.Clear();

    public void Remove(string name) => _tracks.Remove(name);

    /// <summary>Applies one change from the station stream: reset, then append, then drop the oldest.</summary>
    public void Apply(TrackUpdate update)
    {
        if (!_tracks.TryGetValue(update.Name, out var track))
        {
            _tracks[update.Name] = track = new Track();
        }
        TrackUpdates.Apply(track.Points, update, ToMap);
        track.Dirty = true;
        if (track.Points.Count == 0)
        {
            _tracks.Remove(update.Name);
        }
    }

    private static MPoint ToMap(TrackPoint point)
    {
        var (x, y) = SphericalMercator.FromLonLat(point.Longitude, point.Latitude);
        return new MPoint(x, y);
    }

    /// <summary>
    /// The paths for this frame: every track <paramref name="styleOf"/> gives a style, that crosses the view, in screen
    /// coordinates. Points closer than a pixel to the last one kept are merged (behavior 9), so a long track at a wide
    /// zoom costs little.
    /// </summary>
    public TrackFrame Build(
        Mapsui.Viewport viewport,
        Func<string, (TrackStyle Style, bool Stale)?> styleOf,
        TrackColours colours
    )
    {
        var frame = new TrackFrame(colours);
        var half = new MPoint(viewport.Width / 2 * viewport.Resolution, viewport.Height / 2 * viewport.Resolution);
        var view = new MRect(
            viewport.CenterX - half.X,
            viewport.CenterY - half.Y,
            viewport.CenterX + half.X,
            viewport.CenterY + half.Y
        );
        foreach (var (name, track) in _tracks)
        {
            if (track.Points.Count < 2 || styleOf(name) is not var (style, stale) || !track.Bounds.Intersects(view))
            {
                continue;
            }
            var points = track.Points;
            var last = Screen(viewport, points[0]);
            var bucketOf = points.Count;
            var dots = style == TrackStyle.Selected ? new List<SKPoint> { last } : null;
            for (var i = 1; i < points.Count; i++)
            {
                var at = Screen(viewport, points[i]);
                if (i < points.Count - 1 && Math.Abs(at.X - last.X) < 1 && Math.Abs(at.Y - last.Y) < 1)
                {
                    continue;
                }
                var bucket = (i - 1) * AgeAlpha.Length / (bucketOf - 1);
                frame.Segment(style, stale, bucket, last, at);
                dots?.Add(at);
                last = at;
            }
            if (dots is not null)
            {
                frame.Dots.AddRange(dots);
            }
        }
        return frame;
    }

    private static SKPoint Screen(Mapsui.Viewport viewport, MPoint point) =>
        new(
            (float)((point.X - viewport.CenterX) / viewport.Resolution + viewport.Width / 2),
            (float)((viewport.CenterY - point.Y) / viewport.Resolution + viewport.Height / 2)
        );

    private sealed class Track
    {
        private MRect _bounds = new(0, 0, 0, 0);

        public List<MPoint> Points { get; } = [];

        public bool Dirty { get; set; } = true;

        public MRect Bounds
        {
            get
            {
                if (Dirty && Points.Count > 0)
                {
                    double minX = double.MaxValue,
                        minY = double.MaxValue,
                        maxX = double.MinValue,
                        maxY = double.MinValue;
                    foreach (var p in Points)
                    {
                        minX = Math.Min(minX, p.X);
                        minY = Math.Min(minY, p.Y);
                        maxX = Math.Max(maxX, p.X);
                        maxY = Math.Max(maxY, p.Y);
                    }
                    _bounds = new MRect(minX, minY, maxX, maxY);
                    Dirty = false;
                }
                return _bounds;
            }
        }
    }

    /// <summary>One frame's track paths, grouped by how they're drawn, and the selected track's dots.</summary>
    internal sealed class TrackFrame(TrackColours colours) : IDisposable
    {
        private readonly Dictionary<(TrackStyle, bool, int), (SKPath Path, SKPoint End)> _paths = [];

        public TrackColours Colours { get; } = colours;

        public List<SKPoint> Dots { get; } = [];

        /// <summary>How many paths the frame holds, for tests.</summary>
        public int PathCount => _paths.Count;

        /// <summary>Every segment drawn, for tests.</summary>
        public int Segments { get; private set; }

        public void Segment(TrackStyle style, bool stale, int bucket, SKPoint from, SKPoint to)
        {
            var key = (style, stale, bucket);
            if (!_paths.TryGetValue(key, out var entry))
            {
                entry = (new SKPath(), new SKPoint(float.NaN, float.NaN));
            }
            if (entry.End != from)
            {
                entry.Path.MoveTo(from);
            }
            entry.Path.LineTo(to);
            _paths[key] = (entry.Path, to);
            Segments++;
        }

        /// <summary>Draws the tracks: a casing in the surface colour under every line, then the lines, then the dots.</summary>
        public void Draw(SKCanvas canvas)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            foreach (var casing in new[] { true, false })
            {
                foreach (var ((style, stale, bucket), (path, _)) in _paths)
                {
                    var (colour, width, alpha) = style switch
                    {
                        TrackStyle.Followed => (Colours.Followed, 4f, 1f),
                        TrackStyle.Selected => (Colours.Track, 2.5f, 1f),
                        _ => (Colours.Track, 2f, 0.6f),
                    };
                    alpha *= AgeAlpha[bucket] * (stale ? 0.5f : 1f);
                    paint.Color = (casing ? Colours.Casing : colour).WithAlpha(
                        (byte)(alpha * (casing ? 0.6f : 1f) * 255)
                    );
                    paint.StrokeWidth = casing ? width + 2 : width;
                    canvas.DrawPath(path, paint);
                }
            }
            if (Dots.Count > 0)
            {
                paint.Style = SKPaintStyle.Fill;
                foreach (var dot in Dots)
                {
                    paint.Color = Colours.Casing;
                    canvas.DrawCircle(dot, 4f, paint);
                    paint.Color = Colours.Track;
                    canvas.DrawCircle(dot, 2.75f, paint);
                }
            }
        }

        public void Dispose()
        {
            foreach (var (path, _) in _paths.Values)
            {
                path.Dispose();
            }
            _paths.Clear();
        }
    }
}

/// <summary>The track colours from the theme: the track colour, the accent for followed, the surface for casings.</summary>
internal readonly record struct TrackColours(SKColor Track, SKColor Followed, SKColor Casing);

/// <summary>Applies a <see cref="TrackUpdate"/> the way the engine defines it: reset, append, then drop the oldest.</summary>
internal static class TrackUpdates
{
    public static void Apply<T>(List<T> points, TrackUpdate update, Func<TrackPoint, T> convert)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(update);
        if (update.Reset)
        {
            points.Clear();
        }
        foreach (var point in update.Added)
        {
            points.Add(convert(point));
        }
        points.RemoveRange(0, Math.Min(Math.Max(0, update.Dropped), points.Count));
    }
}
