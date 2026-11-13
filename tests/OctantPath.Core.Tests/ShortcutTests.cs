using System.Collections.Generic;
using System.Linq;
using OctantPath.Core.Presets;
using OctantPath.Core.UI;
using Xunit;

namespace OctantPath.Core.Tests
{
    public sealed class ShortcutTests
    {
        private static List<Preset> Shown(params string[] ids) => ids.Select(id => new Preset { Id = id, Label = id }).ToList();

        private static List<string> Ids(List<Preset> presets) => presets.Select(p => p.Id).ToList();

        [Fact]
        public void UsesTheChosenSlots()
        {
            var result = Shortcuts.Resolve(new[] { "b", "c", "a" }, new string[0], Shown("a", "b", "c", "d"));
            Assert.Equal(new[] { "b", "c", "a" }, Ids(result));
        }

        [Fact]
        public void FillsEmptySlotsWithRecentThenTheListOrder()
        {
            var result = Shortcuts.Resolve(new[] { "", "c", "" }, new[] { "d", "c" }, Shown("a", "b", "c", "d"));
            Assert.Equal(new[] { "d", "c", "a" }, Ids(result));
        }

        [Fact]
        public void AMissingPresetFallsBackToRecentWithoutTouchingTheSlot()
        {
            var slots = new[] { "gone", "b", "" };
            var result = Shortcuts.Resolve(slots, new[] { "a" }, Shown("a", "b", "c"));
            Assert.Equal(new[] { "a", "b", "c" }, Ids(result));
            Assert.Equal("gone", slots[0]);
            Assert.True(Shortcuts.IsMissing("gone", Shown("a", "b")));
            Assert.False(Shortcuts.IsMissing("", Shown("a")));
        }

        [Fact]
        public void ANullOrEmptyRecentIdIsSkipped()
        {
            // 設定の "recentPresets": [null] を読んだとき。辞書を null で引くと ArgumentNullException になる
            var result = Shortcuts.Resolve(new[] { "", "", "" }, new[] { null, "", "b" }, Shown("a", "b"));
            Assert.Equal(new[] { "b", "a" }, Ids(result));
        }

        [Fact]
        public void DoesNotRepeatAPreset()
        {
            var result = Shortcuts.Resolve(new[] { "a", "a", "" }, new[] { "a", "b" }, Shown("a", "b"));
            Assert.Equal(new[] { "a", "b" }, Ids(result));
        }

        [Fact]
        public void IsEmptyWithoutPresets()
        {
            Assert.Empty(Shortcuts.Resolve(new[] { "a", "", "" }, new[] { "a" }, null));
        }

        [Fact]
        public void TouchMovesToTheFrontAndKeepsTheLimit()
        {
            var recent = Enumerable.Range(0, 12).Select(i => i.ToString()).ToList();
            var touched = Shortcuts.Touch(recent, "5");
            Assert.Equal("5", touched[0]);
            Assert.Equal(Shortcuts.RecentLimit, touched.Count);
            Assert.Single(touched, t => t == "5");
        }
    }
}
