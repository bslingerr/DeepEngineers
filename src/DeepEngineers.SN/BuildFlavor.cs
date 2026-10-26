namespace DeepEngineers.SN
{
    /// <summary>
 /// Dev/release gate (CONTRACT §5.1 C11, ROADMAP §5.10). build.ps1 deploys the Release configuration, so the gate
 /// cannot be the DEBUG symbol: the flavour is <c>DEEPENG_RELEASE</c>, defined by <c>build.ps1 -Flavor release</c>
 ///; every other build is a dev build.
 /// <list type="bullet">
 /// <item>Dev: C11c is read whenever <c>devControl</c> is non-zero (the harness or SE's chat relay); mutant switches
 /// (<c>DEEPENGINEERS_MUTANT</c>, <c>de.mutant</c>) and the S1.N11 test hook work.</item>
 /// <item>Release: C11c is read only while SE's chat relay holds it (bit 1, no harness bit 0) and only the user tuning
 /// commands of <see cref="DeepEngineers.Wire.UserCommandRules"/> run; mutants and test hooks are off. The hello line
 /// says <c>build=release flavor=release</c>.</item>
 /// </list>
 /// </summary>
    internal static class BuildFlavor
    {
#if DEEPENG_RELEASE
        public static readonly bool Dev = false;
#else
        public static readonly bool Dev = true;
#endif

        public static string Name { get { return Dev ? "dev" : "release"; } }
    }
}
