using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// </summary>
    internal static class RespawnRules
    {
        /// <summary>Subnautica's lifepod point in SN metres: the site centre at the surface, head out.</summary>
        public static readonly Vec3 LifepodSn = new Vec3(0.0, 0.6, 0.0);
        /// <summary>A respawned character whose origin is below this SN altitude is under the waterline.</summary>
        public const double UnderwaterSnY = -0.5;
        /// <summary>A pressurized room around the character (SE environment oxygen above this): an underwater base, no lift.</summary>
        public const float RoomOxygen = 0.5f;
        /// <summary>Give up after this many physics steps without the character coming free under water (the drop pod descends for minutes).</summary>
        public const int GiveUpSteps = 60 * 60 * 10;

        /// <summary>
 /// True when the lift fires: a character that appeared after a death, now free (not seated, not dead, with
 /// physics), inside the site, not in a pressurized room, with its origin under the waterline.
 /// </summary>
        public static bool Fire(bool pendingAfterDeath, bool seated, bool dead, bool hasPhysics, bool onSite, float roomOxygen, double snY)
        {
            return pendingAfterDeath && !seated && !dead && hasPhysics && onSite && roomOxygen < RoomOxygen && snY < UnderwaterSnY;
        }

        /// <summary>The watch ends without a lift: the character is free out of the water (a medical room on a ship, a kit on land).</summary>
        public static bool Settled(bool seated, bool dead, bool hasPhysics, double snY)
        {
            return !seated && !dead && hasPhysics && snY >= UnderwaterSnY;
        }
    }
}
