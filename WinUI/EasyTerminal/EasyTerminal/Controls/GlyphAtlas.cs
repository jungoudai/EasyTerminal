using System.Numerics;
using EasyTerminal.Core.Terminal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;

namespace EasyTerminal.Controls;

/// <summary>
/// Glyphs rasterized once into a texture and drawn as tinted sprites. A full screen becomes a single
/// sprite batch, which is what keeps fast output cheap regardless of how many cells change.
/// </summary>
internal sealed class GlyphAtlas : IDisposable
{
    private const int AtlasPixels = 2048;

    private readonly Dictionary<long, Rect> _slots = new();
    private readonly List<(long Key, int Codepoint, int Style, bool Wide)> _pending = new();
    private readonly CanvasTextFormat[] _formats = new CanvasTextFormat[4];
    private CanvasRenderTarget? _texture;
    private int _nextX, _nextY;
    private readonly float _dpi;
    private readonly float _cellWidth, _cellHeight;
    private readonly int _cellWidthPixels, _cellHeightPixels;
    private readonly float _baselineOffset;

    public CanvasRenderTarget Texture => _texture!;

    public GlyphAtlas(CanvasDevice device, string fontFamily, float fontSize, float dpi, float cellWidth, float cellHeight, float baselineOffset)
    {
        _dpi = dpi;
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;
        _baselineOffset = baselineOffset;
        float scale = dpi / 96f;
        _cellWidthPixels = (int)MathF.Round(cellWidth * scale);
        _cellHeightPixels = (int)MathF.Round(cellHeight * scale);

        for (int style = 0; style < 4; style++)
        {
            _formats[style] = new CanvasTextFormat
            {
                FontFamily = fontFamily,
                FontSize = fontSize,
                FontWeight = (style & 1) != 0 ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                FontStyle = (style & 2) != 0 ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                WordWrapping = CanvasWordWrapping.NoWrap,
                Options = CanvasDrawTextOptions.EnableColorFont,
                LocaleName = "zh-CN",
            };
        }
        CreateTexture(device);
    }

    private void CreateTexture(CanvasDevice device)
    {
        _texture?.Dispose();
        float dips = AtlasPixels / (_dpi / 96f);
        _texture = new CanvasRenderTarget(device, dips, dips, _dpi);
        using var ds = _texture.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
        _nextX = 0;
        _nextY = 0;
    }

    private static long Key(int codepoint, int style) => ((long)codepoint << 2) | (long)style;

    /// <summary>Queues a glyph if needed. Returns false when the frame must flush before drawing it.</summary>
    public void Request(int codepoint, int style, bool wide)
    {
        long key = Key(codepoint, style);
        if (_slots.ContainsKey(key)) return;
        foreach (var pending in _pending)
            if (pending.Key == key) return;
        _pending.Add((key, codepoint, style, wide));
    }

    /// <summary>Rasterizes everything queued this frame in one drawing session.</summary>
    public void Flush(CanvasDevice device)
    {
        if (_pending.Count == 0) return;

        // A full atlas is rare and cheap to rebuild from what the current frame still needs.
        int widest = _cellWidthPixels * 2 + 2;
        int rowsLeft = (AtlasPixels - _nextY) / (_cellHeightPixels + 2);
        int perRow = AtlasPixels / widest;
        if ((rowsLeft - 1) * perRow < _pending.Count)
        {
            _slots.Clear();
            CreateTexture(device);
        }

        float scale = _dpi / 96f;
        using var ds = _texture!.CreateDrawingSession();
        ds.Antialiasing = CanvasAntialiasing.Antialiased;
        ds.TextAntialiasing = CanvasTextAntialiasing.Grayscale;
        foreach (var (key, codepoint, style, wide) in _pending)
        {
            int widthPixels = (wide ? _cellWidthPixels * 2 : _cellWidthPixels);
            if (_nextX + widthPixels + 2 > AtlasPixels)
            {
                _nextX = 0;
                _nextY += _cellHeightPixels + 2;
            }
            var rect = new Rect(_nextX / scale, _nextY / scale, widthPixels / scale, _cellHeightPixels / scale);
            _slots[key] = rect;
            _nextX += widthPixels + 2;

            using (ds.CreateLayer(1f, rect))
            {
                ds.DrawText(ClusterTable.ToText(codepoint), new Vector2((float)rect.X, (float)rect.Y + _baselineOffset), Colors.White, _formats[style]);
            }
        }
        _pending.Clear();
    }

    public Rect Slot(int codepoint, int style) => _slots[Key(codepoint, style)];

    public void Dispose()
    {
        _texture?.Dispose();
        foreach (var format in _formats) format?.Dispose();
    }
}
