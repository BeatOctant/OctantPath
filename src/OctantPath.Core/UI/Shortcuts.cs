using System.Collections.Generic;
using System.Linq;
using OctantPath.Core.Presets;

namespace OctantPath.Core.UI
{
    // 浮かぶ板の下の段に並べる「推薦の近道」（手札 3 つ）。設定の shortcutPresets で枠ごとに手札の id を決める。
    // 空の枠は「最近使ったもの」で埋める。決めた手札が今の一覧にないとき（OctantCompass の更新で消えたなど）も、
    // その間だけ「最近使ったもの」で埋める。設定は書き換えないので、手札が戻れば元の近道に戻る（2026-11-08）
    public static class Shortcuts
    {
        public const int Count = 3;

        // 覚えておく「最近使ったもの」の数
        public const int RecentLimit = 10;

        // 枠の設定（slots）、最近使った手札の id（新しい順。recent）、今出せる手札（shown。OctantCompass の順）から、並べる手札を決める
        public static List<Preset> Resolve(IReadOnlyList<string> slots, IReadOnlyList<string> recent, IReadOnlyList<Preset> shown)
        {
            var result = new Preset[Count];
            var used = new HashSet<string>();
            var byId = (shown ?? new List<Preset>()).Where(p => p?.Id != null).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
            for (var i = 0; i < Count; i++)
            {
                var id = slots != null && i < slots.Count ? slots[i] : null;
                if (!string.IsNullOrEmpty(id) && byId.TryGetValue(id, out var preset) && used.Add(id))
                {
                    result[i] = preset;
                }
            }
            // 残りの枠: 最近使った順、それでも足りなければ一覧の順
            var fill = (recent ?? new List<string>()).Where(id => !string.IsNullOrEmpty(id) && byId.ContainsKey(id)).Select(id => byId[id])
                .Concat(byId.Values.Count == 0 ? Enumerable.Empty<Preset>() : shown.Where(p => p?.Id != null && byId.ContainsKey(p.Id)));
            using (var next = fill.GetEnumerator())
            {
                for (var i = 0; i < Count; i++)
                {
                    while (result[i] == null && next.MoveNext())
                    {
                        if (used.Add(next.Current.Id))
                        {
                            result[i] = next.Current;
                        }
                    }
                }
            }
            return result.Where(p => p != null).ToList();
        }

        // 枠に決めた手札が今の一覧にない（設定の画面で「今は使えません」と出す）
        public static bool IsMissing(string slot, IReadOnlyList<Preset> shown) =>
            !string.IsNullOrEmpty(slot) && shown != null && !shown.Any(p => p?.Id == slot);

        // 手札を使った: 最近使ったものの先頭に置く
        public static List<string> Touch(IReadOnlyList<string> recent, string id)
        {
            var list = (recent ?? new List<string>()).Where(r => !string.IsNullOrEmpty(r) && r != id).ToList();
            if (!string.IsNullOrEmpty(id))
            {
                list.Insert(0, id);
            }
            return list.Take(RecentLimit).ToList();
        }
    }
}
