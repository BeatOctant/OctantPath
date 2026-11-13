using OctantPath.Core.Connection;
using OctantPath.Core.Languages;
using OctantPath.Core.UI;
using Xunit;

namespace OctantPath.Core.Tests
{
    [Collection("Lang")]
    public sealed class PanelStatusTests : System.IDisposable
    {
        public PanelStatusTests() => Lang.Use("ja", null);

        public void Dispose() => Lang.Use(Lang.Default, null);

        [Theory]
        [InlineData("short", 10, "short")]
        [InlineData("0123456789", 10, "0123456789")]
        [InlineData("0123456789A", 10, "012345678…")]
        [InlineData("あいうえお", 10, "あいうえお")]
        [InlineData("あいうえおか", 10, "あいうえ…")]
        [InlineData("a\r\nb", 10, "a b")]
        [InlineData(null, 10, "")]
        public void ClipsByWidth(string text, int width, string expected)
        {
            Assert.Equal(expected, PanelStatus.Clip(text, width));
        }

        [Fact]
        public void DoesNotCutAnEmojiInHalf()
        {
            // "abc" + 😀（2 文字分）+ "def": 幅 5 で切ると、4 文字目が絵文字の前半になる
            Assert.Equal("abc…", PanelStatus.Clip("abc\U0001F600def", 5));
        }

        [Fact]
        public void ColorsOnlyTheStatePart()
        {
            var ready = StatusText.Colored(new ConnectionStatus { State = ConnectionState.Ready });
            Assert.Equal("<color=#5BD15B>接続済</color>", ready);
            Assert.Contains("#FFC845", StatusText.Colored(new ConnectionStatus { State = ConnectionState.NeedsPairing }));
            Assert.Contains("#FF6B6B", StatusText.Colored(new ConnectionStatus { State = ConnectionState.Unreachable }));
        }

        [Fact]
        public void KeepsALongStateOnOneLine()
        {
            var text = StatusText.Colored(new ConnectionStatus { State = ConnectionState.Degraded, Reason = new string('x', 200) });
            Assert.EndsWith("…</color>", text);
        }
    }
}
