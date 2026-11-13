using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    // MOD の画面（src/OctantPath/UI/*.bsml）で、BSML が整数しか受け付けない属性に小数などを書いていないこと。
    // BSML は整数の属性を Parse.Int（int.TryParse）で読み、"0.35" のような値では例外を投げて画面の組み立てが止まる。
    // ゲームを起動するまで分からないので、ここで先に落とす。
    // どの属性が整数かは、BSML の公開ソースの v1.12.5（manifest.json の依存）で確かめた
    // （https://github.com/monkeymanboy/BeatSaberMarkupLanguage/tree/v1.12.5/BeatSaberMarkupLanguage の TypeHandlers と Parse.cs）:
    //   LayoutGroupHandler の pad / padding（Parse.RectOffset。空白で区切った 1〜4 個の整数）と pad-top / pad-bottom / pad-left / pad-right（Parse.Int）、
    //   TabSelectorHandler の page-count（Parse.Int）。
    // spacing、preferred-width、font-size、visible-cells、cell-size などは Parse.Float なので小数でよい。
    // 値が ~ で始まるもの（C# の値を結ぶもの）は、書いた時点では値が決まらないので見ない。
    public sealed class BsmlIntegerAttributeTests
    {
        private static readonly HashSet<string> IntegerAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "pad", "padding", "pad-top", "pad-bottom", "pad-left", "pad-right", "page-count",
        };

        // 空白で区切って 1〜4 個を読む属性（Parse.RectOffset）
        private static readonly HashSet<string> ListAttributes = new HashSet<string>(StringComparer.Ordinal) { "pad", "padding" };

        [Fact]
        public void WritesOnlyIntegersWhereBsmlWantsThem()
        {
            var ui = Path.Combine(OctantSpecsExamples.RepositoryRoot(), "src", "OctantPath", "UI");
            var files = Directory.GetFiles(ui, "*.bsml", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            var wrong = new List<string>();
            foreach (var file in files)
            {
                var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
                foreach (var attribute in doc.Descendants().SelectMany(e => e.Attributes()))
                {
                    var name = attribute.Name.LocalName;
                    if (!IntegerAttributes.Contains(name) || attribute.Value.StartsWith("~", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var parts = ListAttributes.Contains(name)
                        ? attribute.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        : new[] { attribute.Value };
                    if (parts.Length >= 1 && parts.Length <= 4 && parts.All(IsInteger))
                    {
                        continue;
                    }
                    var line = ((IXmlLineInfo)attribute.Parent).LineNumber;
                    wrong.Add($"{Path.GetFileName(file)}:{line} {name}='{attribute.Value}'（整数で書く）");
                }
            }
            Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        }

        private static bool IsInteger(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
