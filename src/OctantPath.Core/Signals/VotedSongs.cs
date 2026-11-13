using System.Collections.Generic;
using System.IO;
using OctantPath.Core.Maps;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Signals
{
    // BeatSaverVoting の votedSongs.json（hash ごとの最後の投票）。投票はこのファイルが正本
    public static class VotedSongs
    {
        // hash → upvote / downvote。ファイルがなければ空。形が違えば例外
        public static Dictionary<string, string> Read(string path)
        {
            var votes = new Dictionary<string, string>();
            if (!File.Exists(path))
            {
                return votes;
            }
            foreach (var p in JObject.Parse(File.ReadAllText(path)).Properties())
            {
                var hash = MapHash.Normalize(p.Name);
                // 曲の hash の項目で形が違うものは読み飛ばさない。飛ばすと、その曲の Upvote がなかったことになり、片付けがお試しの曲を
                // 消してしまう（hash でない項目は曲の投票ではないので飛ばす）
                if (hash != null && p.Value.Type != JTokenType.Object)
                {
                    throw new InvalidDataException($"votedSongs.json: the entry for {p.Name} is not an object");
                }
                var type = p.Value.Type == JTokenType.Object ? (string)p.Value["voteType"] : null;
                if (hash != null && (type == "Upvote" || type == "Downvote"))
                {
                    votes[hash] = type == "Upvote" ? SignalTypes.Upvote : SignalTypes.Downvote;
                }
            }
            return votes;
        }
    }
}
