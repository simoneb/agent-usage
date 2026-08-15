using AgentUsage.Widget;
using Xunit;

namespace AgentUsage.Widget.Tests;

/// <summary>
/// The tray gives an icon a box sixteen pixels tall and draws whatever it is handed inside it.
/// Every pixel of that box the layout declines to use is a pixel by which this icon reads as
/// smaller than the ones either side of it, so the rows have to add up to the whole square.
/// </summary>
public class IconBuilderTests
{
    [Theory]
    [InlineData(16, 4, 2, 1)]     // two accounts, session and week each, one gap between them
    [InlineData(16, 2, 2, 0)]     // one account
    [InlineData(16, 3, 1, 2)]     // three accounts, week only
    [InlineData(16, 4, 1, 3)]     // four accounts
    [InlineData(24, 4, 3, 1)]     // the same at 150%
    [InlineData(32, 4, 4, 1)]     // and the window icon
    public void RowsFillTheSquareExactly(int size, int rows, int gap, int gaps)
    {
        var heights = IconBuilder.SplitRows(size, rows, gap, gaps);

        Assert.Equal(rows, heights.Length);
        Assert.Equal(size, Sum(heights) + gap * gaps);
    }

    [Fact]
    public void SpreadsTheRemainderRatherThanLosingIt()
    {
        // 16 pixels less one gap is 14 across four rows: three and a half each. Rounding every
        // row down would leave two pixels of the icon empty.
        var heights = IconBuilder.SplitRows(16, 4, 2, 1);

        Assert.Equal(new[] { 4, 4, 3, 3 }, heights);
    }

    [Fact]
    public void NeverDrawsARowThinnerThanAPixel()
    {
        // More accounts than the icon has pixels for is not a layout this can produce — the
        // builder falls back to digits well before then — but a zero-height row would be an
        // account rendered as nothing at all.
        var heights = IconBuilder.SplitRows(16, 12, 2, 11);

        Assert.All(heights, h => Assert.True(h >= 1));
    }

    private static int Sum(int[] values)
    {
        var total = 0;
        foreach (var value in values) total += value;
        return total;
    }
}
