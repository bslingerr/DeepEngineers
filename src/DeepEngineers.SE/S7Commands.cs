using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S7 command dispatcher of the SE side. Each feature keeps its own verb and answers null for any other:
 /// <list type="bullet">
 /// <item><c>de.interior</c>, <c>de.lights</c>: <see cref="InteriorCommands"/> (interiors and lights);</item>
 /// <item><c>de.fx</c>: <see cref="S7Effects"/> (the effect events);</item>
 /// <item><c>de.statics</c>: <see cref="S7Statics.Command"/> (statics hosts, rays, probe, census);</item>
 /// <item><c>de.bones</c>: <see cref="SkinnedExport"/> (the skinned astronaut);</item>
 /// <item><c>de.s7audio</c>: <see cref="AudioPolish"/> (the SE muffle under water).</item>
 /// </list>
 /// The S7a verb <c>de.s7</c> stays with <see cref="S7aCommands"/>. Update thread (C11 and chat).
 /// </summary>
    internal static class S7Commands
    {
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd == null) return null;
            if (cmd == S7Statics.Cmd) return S7Statics.Command(args ?? new Dictionary<string, object>());
            return InteriorCommands.Execute(cmd, args, via)
                ?? S7Effects.Execute(cmd, args, via)
                ?? SkinnedExport.Execute(cmd, args, via)
                ?? AudioPolish.Execute(cmd, args, via)
                ?? S7pSceneSe.Execute(cmd, args, via); // S7p-scene-above: de.s7p {above, projections, subparts}
        }
    }
}
