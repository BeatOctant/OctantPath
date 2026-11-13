namespace OctantPath.Core.UI
{
    // 浮かぶ板の文を 1 行に収める。長い文で板の並びが崩れたので（2026-11-03）、画面の … とは別に、ここでも文字の幅で切っておく
    public static class PanelStatus
    {
        // 幅の目安（半角 1、全角 2 として数える）。推測で決めた値: 大きさ 2.8 で半角が約 1.7 なので、幅 70 のマスに約 40
        public const int NoticeWidth = 40;
        // 接続の状態のマス（知らせと同じマス）
        public const int ConnectionWidth = 40;

        // 全角（漢字・かな・全角記号など）は 2、それ以外は 1
        public static int Width(char c) => c >= '⺀' ? 2 : 1;

        // 1 行にして、width を超えるなら … を付けて切る
        public static string Clip(string text, int width)
        {
            var line = DisplayText.Line(text);
            var used = 0;
            for (var i = 0; i < line.Length; i++)
            {
                used += Width(line[i]);
                if (used > width)
                {
                    // … の分（1）をあける。サロゲートペア（絵文字など）の途中では切らない
                    var cut = i;
                    while (cut > 0 && Measure(line.Substring(0, cut)) > width - 1)
                    {
                        cut--;
                    }
                    if (cut > 0 && char.IsHighSurrogate(line[cut - 1]))
                    {
                        cut--;
                    }
                    return line.Substring(0, cut) + "…";
                }
            }
            return line;
        }

        private static int Measure(string text)
        {
            var sum = 0;
            foreach (var c in text)
            {
                sum += Width(c);
            }
            return sum;
        }
    }
}
