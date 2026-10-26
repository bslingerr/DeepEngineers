using System;
using System.Runtime.CompilerServices;
using UWE;

namespace DeepEngineers.SN
{
    /// <summary>
 /// <see cref="IFreezeApi"/> over <c>UWE.FreezeTime</c> for the id <c>FeedbackPanel</c>
 /// FeedbackPanel is used by the game's feedback panel only (F8 with Subnautica focused, which never happens in a bridged
 /// session).
 /// </summary>
    internal sealed class UweFreezeApi : IFreezeApi
    {
        public const FreezeTime.Id OwnerId = FreezeTime.Id.FeedbackPanel;

        public bool Held { get { return Contains(); } }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Contains() { return FreezeTime.Contains(OwnerId); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Begin() { FreezeTime.Begin(OwnerId); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void End() { FreezeTime.End(OwnerId); }
    }

    /// <summary>
 /// A cheap signature of FreezeTime's state, read every frame: one bit per <c>FreezeTime.Id</c> held. Any change is a
 /// FreezeTime transition (ours or the game's: IngameMenu, WaitScreen, PDA, ApplicationFocus …) after which the audio
 /// mix is re-applied, because FreezeTime writes the <c>bus:/</c> volume on its own transitions (ARCHITECTURE §5.3).
 /// </summary>
    internal static class FreezeSignature
    {
        private static readonly FreezeTime.Id[] Ids =
        {
            FreezeTime.Id.TextInput, FreezeTime.Id.PDA, FreezeTime.Id.HardcoreGameOver, FreezeTime.Id.DevBeaconPanel,
            FreezeTime.Id.IngameMenu, FreezeTime.Id.FeedbackPanel, FreezeTime.Id.WaitScreen, FreezeTime.Id.Save,
            FreezeTime.Id.Quit, FreezeTime.Id.ApplicationFocus,
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint Read()
        {
            if (!FreezeTime.HasFreezers()) return 0;
            uint s = 0;
            for (int i = 0; i < Ids.Length; i++)
                if (FreezeTime.Contains(Ids[i])) s |= 1u << i;
            return s;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Top() { return FreezeTime.GetTopmostId().ToString(); }
    }
}
