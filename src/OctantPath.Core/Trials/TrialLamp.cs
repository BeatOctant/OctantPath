using OctantPath.Core.Maps;

namespace OctantPath.Core.Trials
{
    public enum TrialLampKind
    {
        // ランプを出さない（お試し中の曲ではない。または、お気に入り・Upvote・遊んだ回数でどのみち残る）
        None,
        // お試し中。押すと残す
        Trial,
        // 利用者が自分で残した。次の起動で CustomLevels に移るまでは、押すと戻せる
        Keeping,
    }

    // 浮かぶ板と結果画面に出す「お試し」のランプの状態。ランプは、押して変えられるときだけ出す（2026-11-09）。
    // お気に入り・Upvote・遊んだ回数で残る曲は、押しても変わらず、ほかの方法で取った曲と同じなので出さない
    public sealed class TrialLampState
    {
        public static readonly TrialLampState Hidden = new TrialLampState(TrialLampKind.None, 0, 0);

        public TrialLampState(TrialLampKind kind, int launchDays, int expiryLaunchDays)
        {
            Kind = kind;
            LaunchDays = launchDays;
            ExpiryLaunchDays = expiryLaunchDays;
        }

        public TrialLampKind Kind { get; }

        // 出ているランプは押せる
        public bool CanToggle => Kind != TrialLampKind.None;

        // 押したあとの「残す」の印
        public bool KeepAfterToggle => Kind == TrialLampKind.Trial;

        // 取った日から数えた起動日と、期限の起動日
        public int LaunchDays { get; }

        public int ExpiryLaunchDays { get; }

        // map は台帳でその曲に当たるもの（なければ null）
        public static TrialLampState For(OwnedMap map, TrialPolicy policy, int expiryLaunchDays)
        {
            if (map == null || map.State != MapStates.Trial || policy.KeepsWithoutRequest(map))
            {
                return Hidden;
            }
            var days = policy.LaunchDaysSince(map.AcquiredAt);
            // Downvote した曲もお試し中として出し、押せば残せる（押したほうが新しい意思なので）
            return map.KeepRequested
                ? new TrialLampState(TrialLampKind.Keeping, days, expiryLaunchDays)
                : new TrialLampState(TrialLampKind.Trial, days, expiryLaunchDays);
        }
    }
}
