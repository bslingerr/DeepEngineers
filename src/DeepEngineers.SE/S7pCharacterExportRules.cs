using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.SE
{
    /// <summary>

 /// original"; "it must work for both" the male and the female astronaut): the pure parts of the character export, no
 /// game types (compiled into DeepEngineers.SE.Tests).
 /// <list type="bullet">
 /// <item>The fallback follows the character's gender: <c>MyCharacterDefinition.Gender</c> is the string "Male" or
 /// "Female"
 /// so a female player
 /// whose own export fails is never drawn as the male Engineer.</item>
 /// <item>The held tool's skin: the tool entity's own <c>MyAssetModifierComponent</c>
 /// the first
 /// listed skin decides the tool part's model variant; a change re-sends the part.</item>

 /// model, colour or skin differs.</item>
 /// </list>
 /// </summary>
    internal static class S7pCharacterExportRules
    {
        public const string DefaultMale = "Default_Astronaut", DefaultFemale = "Default_Astronaut_Female";

        /// <summary>Wait at most this long for a tool skin variant (the base model loading) before the plain tool is sent.</summary>
        public const ulong ToolSkinWaitMs = 3000;

        public static bool IsFemale(string gender)
        {
            return gender != null && string.Equals(gender.Trim(), "Female", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The default character definition of the player's gender (the export fallback).</summary>
        public static string FallbackSubtype(string gender) { return IsFemale(gender) ? DefaultFemale : DefaultMale; }

        /// <summary>The tool part's skin: the first non-empty skin subtype of the tool entity's asset modifiers, else null.</summary>
        public static string ToolSkin(IList<string> toolSkins)
        {
            if (toolSkins == null) return null;
            for (int i = 0; i < toolSkins.Count; i++)
            {
                string s = toolSkins[i];
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }

        /// <summary>
 /// What the tool step does this tick: Send (a new tool, or the skin changed and the variant is decided), Wait (the
 /// skin's variant is not decided yet and the wait has not run out), Keep (nothing changed), Remove (no tool any more).
 /// </summary>
        public enum ToolStep { Keep = 0, Send = 1, Wait = 2, Remove = 3 }

        public static ToolStep DecideTool(long toolId, long sentToolId, bool sent, string skin, string sentSkin, bool variantDecided, ulong waitedMs)
        {
            if (toolId == 0) return sent ? ToolStep.Remove : ToolStep.Keep;
            bool same = sent && toolId == sentToolId && string.Equals(skin ?? "", sentSkin ?? "", StringComparison.Ordinal);
            if (same) return ToolStep.Keep;
            if (skin != null && !variantDecided && waitedMs < ToolSkinWaitMs) return ToolStep.Wait;
            return ToolStep.Send;
        }

        /// <summary>The <c>DE.S7p char identity</c> text: definition subtype, gender, model asset, colour mask (h, s, v) and skins.</summary>
        public static string IdentityText(long characterId, string modelName, string gender, string asset, float h, float s, float v, IList<string> skins, string toolSkin)
        {
            var sb = new StringBuilder(256);
            sb.Append("character ").Append(characterId.ToString(CultureInfo.InvariantCulture))
              .Append(" definition ").Append(modelName ?? "?").Append(" gender ").Append(string.IsNullOrEmpty(gender) ? "?" : gender)
              .Append(" model ").Append(asset ?? "?")
              .Append(" colorMaskHsv (").Append(G(h)).Append(", ").Append(G(s)).Append(", ").Append(G(v)).Append(')')
              .Append(" skins [");
            if (skins != null) for (int i = 0; i < skins.Count; i++) { if (i > 0) sb.Append(','); sb.Append(skins[i]); }
            sb.Append(']');
            if (toolSkin != null) sb.Append(" toolSkin ").Append(toolSkin);
            return sb.ToString();
        }

        private static string G(float x) { return x.ToString("0.####", CultureInfo.InvariantCulture); }
    }
}
