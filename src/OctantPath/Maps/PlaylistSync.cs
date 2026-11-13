using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctantPath.Core.Maps;
using OctantPath.Core.Settings;
using OctantPath.Core.Trials;
using BeatSaberPlaylistsLib;
using BeatSaberPlaylistsLib.Types;
using OctantPath.Core.Languages;

namespace OctantPath.Maps
{
    // OctantPath のプレイリスト。台帳から作る表示で、所有の証拠にはしない。
    // OctantPath の印がないファイル、読めないファイルは書き換えない（OctantPath 法 3.7）
    internal sealed class PlaylistSync
    {
        private static void Warn(string error)
        {
            if (error != null)
            {
                Plugin.Log.Warn($"[playlist] {error}");
            }
        }

        public void Rebuild(OwnershipLedger ledgerStore, OctantPathSettings settings)
        {
            var ledger = ledgerStore.Load();
            if (ledger == null)
            {
                Plugin.Log.Warn("[playlist] ledger.json could not be read; playlists are left as they are");
                return;
            }
            var manager = Folder();
            // 永続化された曲（残すになった曲）だけの「Saved」。お試し中の曲はここに出さない（2026-11-10）
            var kept = ledger.Maps.Where(m => m.State == MapStates.Kept).ToList();
            Warn(Write(manager, "OctantPath-saved", "OctantPath Saved", kept, SavedCover));
            // お試しの枠（自動の入れ替え）は枠ごとに「Trial」。枠は初期値で 0 個なので、ふつうは出ない
            foreach (var frame in settings.TrialFrames)
            {
                var maps = ledger.Maps.Where(m => (m.State == MapStates.Trial || m.State == MapStates.Kept) && m.FrameIds().Contains(frame.Id)).ToList();
                Warn(Write(manager, $"OctantPath-{frame.Id}", $"OctantPath {frame.DisplayName}", maps, TrialCover));
            }
            PlaylistManager.DefaultManager.RequestRefresh("OctantPath");
        }

        // ミックスを保存する。OctantPath のお試しの印（PlaylistGuard.MarkKey）は付けないので、
        // ほかのプレイリストと同じように、入っている曲は自動削除から守られる
        public void SaveMix(string title, List<(string Hash, string Name, string Key, string Mapper)> songs)
        {
            var manager = Folder();
            var fileName = "OctantPath-mix-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var playlist = manager.CreatePlaylist(fileName, title, "OctantPath", (string)null, Lang.T("playlist.savedDescription"));
            playlist.SetCustomData(PlaylistFolder.SavedMixKey, true);
            foreach (var song in songs.Where(s => s.Hash != null))
            {
                playlist.Add(song.Hash, song.Name, song.Key, song.Mapper);
            }
            manager.StorePlaylist(playlist);
            PlaylistManager.DefaultManager.RequestRefresh("OctantPath");
        }

        // 同じ曲の組み合わせ（順は問わない）の保存済みミックスが、もうあるか。何度も押して同じプレイリストが増えないようにする（2026-11-10）
        public bool HasSavedMix(IEnumerable<string> hashes)
        {
            var wanted = new HashSet<string>(hashes.Where(h => h != null).Select(h => h.ToLowerInvariant()));
            if (wanted.Count == 0)
            {
                return false;
            }
            foreach (var playlist in Folder().GetAllPlaylists(false))
            {
                if (!playlist.TryGetCustomData(PlaylistFolder.SavedMixKey, out _))
                {
                    continue;
                }
                var have = new HashSet<string>(playlist.Where(s => s.Hash != null).Select(s => s.Hash.ToLowerInvariant()));
                if (have.SetEquals(wanted))
                {
                    return true;
                }
            }
            return false;
        }

        // ミックスの作成で開く一時的なプレイリスト。作るたびに中身を置き換える。
        // OctantPath の印を付けるので、このプレイリストに入っているだけでは自動削除から守らない（保存したミックスとは違う）
        public BeatmapLevelPack WriteCurrentMix(string title, List<(string Hash, string Name, string Key, string Mapper)> songs)
        {
            const string fileName = "OctantPath-mix-current";
            var manager = Folder();
            var playlist = manager.GetPlaylist(fileName, false);
            if (playlist == null && File.Exists(Path.Combine(manager.PlaylistPath, fileName + ".bplist")))
            {
                Plugin.Log.Warn($"[playlist] {fileName}.bplist が読めないので書き換えません");
                return null;
            }
            if (playlist != null && !playlist.TryGetCustomData(PlaylistGuard.MarkKey, out _))
            {
                Plugin.Log.Warn($"[playlist] {fileName}.bplist は OctantPath が作ったものではないので書き換えません");
                return null;
            }
            if (playlist == null)
            {
                playlist = manager.CreatePlaylist(fileName, title, "OctantPath", (string)null, Lang.T("playlist.mixDescription"));
                playlist.SetCustomData(PlaylistGuard.MarkKey, "mix");
            }
            playlist.Title = title;
            SetCover(playlist, MixCover);
            foreach (var song in playlist.ToList())
            {
                playlist.TryRemove(song);
            }
            foreach (var song in songs.Where(s => s.Hash != null))
            {
                playlist.Add(song.Hash, song.Name, song.Key, song.Mapper);
            }
            manager.StorePlaylist(playlist);
            PlaylistManager.DefaultManager.RequestRefresh("OctantPath");
            return playlist.PlaylistLevelPack;
        }

        // 表紙で区別する（2026-11-10。名前だけでは区別がつかなかった）。
        // 永続化された曲は「Saved」、ミックスの作成で開く一時的なものは「Mix」、お試しの枠は「Trial」。
        // ミックスから保存したプレイリストはふつうの表紙のまま
        private const string SavedCover = "cover-saved";
        private const string MixCover = "cover-mix";
        private const string TrialCover = "cover-trial";

        private static void SetCover(IPlaylist playlist, string name)
        {
            using (var stream = typeof(PlaylistSync).Assembly.GetManifestResourceStream($"OctantPath.Icons.{name}.png"))
            {
                if (stream == null)
                {
                    Plugin.Log.Warn($"[playlist] {name}.png is not in the MOD; the cover is left as it is");
                    return;
                }
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    playlist.SetCover(memory.ToArray());
                }
            }
        }

        // OctantPath のプレイリストは Playlists/OctantPath/ に置き、ほかのプレイリストと混ぜない（推測: PlaylistManager では 1 つのフォルダに見える）
        private static PlaylistManager Folder() => PlaylistManager.DefaultManager.CreateChildManager(PlaylistFolder.Name);

        private static string Write(PlaylistManager manager, string fileName, string title, List<OwnedMap> maps, string cover)
        {
            var playlist = manager.GetPlaylist(fileName, false);
            if (playlist == null && File.Exists(Path.Combine(manager.PlaylistPath, fileName + ".bplist")))
            {
                return Lang.T("playlist.unreadable", new { file = fileName });
            }
            if (playlist == null)
            {
                if (maps.Count == 0)
                {
                    return null;
                }
                playlist = manager.CreatePlaylist(fileName, title, "OctantPath", (string)null, Lang.T("playlist.trialDescription"));
                playlist.SetCustomData(PlaylistGuard.MarkKey, "trial");
            }
            else if (!playlist.TryGetCustomData(PlaylistGuard.MarkKey, out _))
            {
                return Lang.T("playlist.notOurs", new { file = fileName });
            }

            SetCover(playlist, cover);
            var wanted = new HashSet<string>(maps.Select(m => m.Hash));
            foreach (var song in playlist.ToList())
            {
                if (song.Hash == null || !wanted.Contains(song.Hash.ToLowerInvariant()))
                {
                    playlist.TryRemove(song);
                }
            }
            var present = new HashSet<string>(playlist.Where(s => s.Hash != null).Select(s => s.Hash.ToLowerInvariant()));
            foreach (var map in maps.Where(m => !present.Contains(m.Hash)))
            {
                playlist.Add(map.Hash, map.Folder, map.MapKey, null);
            }
            manager.StorePlaylist(playlist);
            return null;
        }
    }
}
