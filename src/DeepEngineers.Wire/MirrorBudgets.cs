using System;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S5 budgets, back-pressure, the entity-set radii and the C2 row order (CONTRACT §5.4 "Entity set (SE)", "C2
 // subpart table (S5 rows)", "Budgets and back-pressure (S5)"). Numbers only plus the two pure decisions both sides
 // and the stand-ins share (C4 admission, C2 row ordering). Not part of the pinned conventions text: budgets may be
 // tuned after the real runs (S5.N6, T-P1) without a re-pin.
 // =================================================================================================================

 /// <summary>S5 budget numbers (CONTRACT §5.4).</summary>
    public static class MirrorBudgets
    {
        // ---- entity set (SE)
 /// <summary>An entity enters the mirrored set within 250 m of the SE camera (water visibility fallback 200 m + 50 m).</summary>
        public const double EnterRadiusMetres = 250.0;
        /// <summary>It starts leaving beyond 300 m.</summary>
        public const double LeaveRadiusMetres = 300.0;
        /// <summary>EntityRemove after 60 s outside the leave radius (or when SE closes it).</summary>
        public const double KeepOutsideSeconds = 60.0;
        /// <summary>C2 entity records (layout 1.0).</summary>
        public const int MaxEntities = SeEntitiesLimits.MaxEntities;
        /// <summary>C2 subpart rows per tick (layout 1.0); rows past it are dropped and counted (<c>c2Dropped</c>).</summary>
        public const int MaxSubpartRows = SeEntitiesLimits.MaxSubparts;

        // ---- SE producer
 /// <summary>C3 serialisation per tick, ms.</summary>
        public const double C3MillisecondsPerTick = 2.0;
        /// <summary>C3 bytes written per second.</summary>
        public const long C3BytesPerSecond = 16L << 20;
        /// <summary>C4 per tick, ms.</summary>
        public const double C4MillisecondsPerTick = 1.0;
        /// <summary>C4 bytes per tick.</summary>
        public const int C4BytesPerTick = 512 << 10;
        /// <summary>Below this C4 free space only classes (1)-(2) are written.</summary>
        public const long C4LowFreeBytes = 1L << 20;
        /// <summary>Below this C3 free space no new model starts.</summary>
        public const long C3LowFreeBytes = 8L << 20;
        /// <summary>Edits within this distance of the camera are class (2).</summary>
        public const double NearEditRadiusMetres = 50.0;
        /// <summary>C1 + C2 per tick, ms (target).</summary>
        public const double C1C2MillisecondsPerTick = 0.3;

        // ---- Subnautica consumer
 /// <summary>Drain per frame, ms …</summary>
        public const double DrainMillisecondsPerFrame = 4.0;
        /// <summary>… or bytes (§5.1).</summary>
        public const long DrainBytesPerFrame = 48L << 20;
        /// <summary>Main-thread uploads per frame, ms.</summary>
        public const double UploadMillisecondsPerFrame = 2.0;
        /// <summary>Retarget per frame, ms (HYPOTHESIS); over it: Retarget off, SeBones, logged.</summary>
        public const double RetargetMillisecondsPerFrame = 0.3;
        /// <summary>Per-bone blend between animation sources, s (HYPOTHESIS for the look).</summary>
        public const double SourceBlendSeconds = 0.25;
    }

    /// <summary>The C4 write classes, highest priority first (CONTRACT §5.4 "SE C4 order").</summary>
    public enum C4Priority
    {
        /// <summary>(1) ClearAll, EntityRemove, GridAdd re-parent or flag change, CharacterAdd (and its parts).</summary>
        Control = 1,
        /// <summary>(2) Edits within 50 m of the camera, newest first.</summary>
        NearEdit = 2,
        /// <summary>(3) Other edits, newest first.</summary>
        Edit = 3,
        /// <summary>(4) First sends, nearest entity first, within it nearest cell first.</summary>
        FirstSend = 4,
    }

    /// <summary>The C2 subpart-table row kinds, in write order.</summary>
    public enum C2RowKind
    {
        /// <summary>A subgrid relative to its root (by root distance).</summary>
        Subgrid = 0,
        /// <summary>A Dynamic subpart in its grid's frame (nearest first).</summary>
        DynamicSubpart = 1,
        /// <summary>The held tool in the character's model space.</summary>
        Tool = 2,
    }

    /// <summary>The pure decisions of the S5 budgets.</summary>
    public static class MirrorBackPressure
    {
        /// <summary>
 /// The class of a C4 record: ClearAll, EntityRemove and CharacterAdd are Control; a GridAdd is Control when it
 /// re-sends a known entity (re-parent, flag change) and FirstSend otherwise; PartAdd/PartRemove/PartRecolor are
 /// FirstSend while their entity is being sent for the first time, else NearEdit within 50 m and Edit beyond.
 /// Other types (SkinSet, RoomSet) are Edit.
 /// </summary>
        public static C4Priority Classify(uint c4Type, bool firstSend, double distanceMetres)
        {
            switch (c4Type)
            {
                case C4Type.ClearAll:
                case C4Type.EntityRemove:
                case C4Type.CharacterAdd:
                    return C4Priority.Control;
                case C4Type.GridAdd:
                    return firstSend ? C4Priority.FirstSend : C4Priority.Control;
                case C4Type.PartAdd:
                case C4Type.PartRemove:
                case C4Type.PartRecolor:
                    if (firstSend) return C4Priority.FirstSend;
                    return distanceMetres <= MirrorBudgets.NearEditRadiusMetres ? C4Priority.NearEdit : C4Priority.Edit;
                default:
                    return C4Priority.Edit;
            }
        }

        /// <summary>C4 back-pressure: with less than 1 MiB free only classes (1)-(2) are written.</summary>
        public static bool AdmitC4(C4Priority priority, long c4FreeBytes)
        {
            return priority <= C4Priority.NearEdit || c4FreeBytes >= MirrorBudgets.C4LowFreeBytes;
        }

        /// <summary>C3 back-pressure: with less than 8 MiB free no new model starts (a started model continues).</summary>
        public static bool MayStartModel(long c3FreeBytes) { return c3FreeBytes >= MirrorBudgets.C3LowFreeBytes; }

        /// <summary>
 /// C2 row order: subgrids, then Dynamic subparts, then the tool; within a kind nearest first (subgrids by their
 /// root's distance). Ties keep the caller's order when the sort is stable.
 /// </summary>
        public static int CompareRows(C2RowKind kindA, double distanceA, C2RowKind kindB, double distanceB)
        {
            if (kindA != kindB) return kindA < kindB ? -1 : 1;
            return distanceA.CompareTo(distanceB);
        }

        /// <summary>Rows kept of <paramref name="candidates"/> (the rest is <c>c2Dropped</c>).</summary>
        public static int RowsKept(int candidates) { return Math.Max(0, Math.Min(candidates, MirrorBudgets.MaxSubpartRows)); }
    }
}
