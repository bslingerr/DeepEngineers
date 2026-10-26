using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7a game glue of the picture policy (the rules are in <see cref="PicturePolicy"/>, game-independent): the S5
 /// entity-set radius hook called by <c>GridMirrorProducer.S5</c> each tick before <c>EntitySet.Tick</c>. S7t T3: the
 /// policy test the impostors' vertical reach shares (<see cref="SiteNow"/>); the radii themselves are unchanged (the
 /// horizontal rule; the reach is <c>S7tImpostorRules</c>).
 /// </summary>
    internal static class PictureHooks
    {
        /// <summary>
 /// The entity-set radii for this tick: the configured S5 radii, raised to 1,000 / 1,050 m while the SE camera is
 /// above the sea (C5 <c>camSnY</c> &gt; 0, the C1 camera's SN altitude) and the view machine's effective picture
 /// policy is Site (CONTRACT §5.4 "C5/C7 semantics for S7a", entity set). Before the machine's first step the
 /// selected policy stands in.
 /// </summary>
        public static void EntityRadii(ref double enterM, ref double leaveM)
        {
            double y = FrameWriter.HasC5 ? FrameWriter.C5CamSnY : double.NaN;
            PicturePolicy.EntityRadii(SiteNow(), y, ref enterM, ref leaveM);
        }

        /// <summary>
 /// The view machine's effective picture policy is Site (before its first step: the selected policy). S7t T3: the
 /// impostors' vertical reach (<c>GridMirrorProducer.S7t</c>) exists under Site only.
 /// </summary>
        public static bool SiteNow()
        {
            ViewStateDriver d = S2Host.Driver;
            return d != null ? d.Last.PicturePolicy == ViewPicture.Site : PicturePolicy.Current == ViewPicture.Site;
        }
    }
}
