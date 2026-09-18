using System.Drawing;
using PlannerEdge.Helper.Hosting;

namespace PlannerEdge.Helper.Tests;

public sealed class TrayIconTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(null, true)]
    public void ForegroundMatchesSystemTaskbarTheme(int? lightTheme, bool lightForeground) =>
        Assert.Equal(lightForeground, TrayIconTheme.UseLightForeground(lightTheme, false, Color.Black));

    [Fact]
    public void HighContrastOverridesTheNormalTheme()
    {
        Assert.True(TrayIconTheme.UseLightForeground(1, true, Color.Black));
        Assert.False(TrayIconTheme.UseLightForeground(0, true, Color.White));
    }

    [Theory]
    [InlineData("MicrosoftWidgets.Helper.Icon.ico")]
    [InlineData("MicrosoftWidgets.Helper.IconLight.ico")]
    public void IconHasSmallAndLargeFramesWithPurpleAccent(string resource)
    {
        using var stream = typeof(TrayService).Assembly.GetManifestResourceStream(resource);
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        var frames = new List<(int Size, uint Length, uint Offset)>();
        for (var i = 0; i < count; i++)
        {
            var size = reader.ReadByte();
            reader.ReadBytes(7);
            frames.Add((size == 0 ? 256 : size, reader.ReadUInt32(), reader.ReadUInt32()));
        }
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 256 }, frames.Select(f => f.Size));
        foreach (var frame in frames)
        {
            stream.Position = frame.Offset;
            using var png = new MemoryStream(reader.ReadBytes((int)frame.Length));
            using var bitmap = new Bitmap(png);
            Assert.Equal(frame.Size, bitmap.Width);
            Assert.Equal(0, bitmap.GetPixel(0, 0).A);
            var purplePixels = 0;
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.A > 200 && pixel.B > 150 && pixel.R > 100 && pixel.G < 120) purplePixels++;
                }
            Assert.True(purplePixels > 2, $"No readable purple accent in {frame.Size}px frame.");
        }
    }
}
