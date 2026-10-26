namespace DeepEngineers.Wire
{
    /// <summary>
 /// Builds <see cref="ViewInputs.RenderAckSeq"/> for a display unit that reports counters instead of echoing
 /// <see cref="ViewOutputs.RenderSeq"/> (render ack contract: <see cref="ViewStateMachine"/> remarks). Update thread,
 /// once per tick, right before <see cref="ViewStateMachine.Step"/>, with the request currently published (the
 /// previous step's <c>RenderSeq</c> and <c>Request</c>, i.e. <see cref="ViewMachineState.RenderSeq"/> and
 /// <see cref="ViewMachineState.Request"/>):
 /// <code>
 /// inputs.RenderAckSeq = ack.Update(state.RenderSeq, state.Request, display.MarginsApplied,
 /// display.SceneDrawnPresents, display.SceneSkippedPresents);
 /// ViewStateMachine.Step(ref state, ref inputs, out outputs);
 /// </code>
 /// A request counts as presented once a frame drawn under it was presented after it was first seen here (a skipped
 /// frame for a scene-skip request, a scene-drawn frame otherwise) and the DWM margins state matches it, where
 /// <c>marginsApplied</c> = "margins −1 applied (S_OK) on SE's window and at least one frame presented since", false
 /// whenever they are 0 (always false in A′ and C). Review fix: a scene-drawn request that asks for
 /// margins (Prime, Surfacing) is acknowledged on the frame condition alone, because the render thread may be unable
 /// to apply them (peer exited, window lost, forced off) and Surfacing must still complete once SE's scene is on
 /// screen; the machine checks the live margins separately (<see cref="ViewInputs.MarginsReady"/>) before it skips
 /// the scene. Counters are monotonic, render thread.
 /// Frames presented between the step and this call are not counted, so the ack is conservative (one frame later
 /// at worst). A plain struct; <c>default</c> treats the machine's initial request (seq 0) as presented.
 /// </summary>
    public struct RenderAck
    {
        public uint Seq;
        public uint Request;
        public long SceneAtRequest;
        public long SkippedAtRequest;
        public uint Acked;
        public bool Started;

        public uint Update(uint renderSeq, uint request, bool marginsApplied, long sceneDrawnPresents, long sceneSkippedPresents)
        {
            if (!Started || renderSeq != Seq)
            {
                if (!Started) Acked = renderSeq == 0 ? 0u : unchecked(renderSeq - 1);
                Started = true;
                Seq = renderSeq;
                Request = request;
                SceneAtRequest = sceneDrawnPresents;
                SkippedAtRequest = sceneSkippedPresents;
                if (renderSeq == 0 && request == 0) { Acked = 0; return Acked; }
            }
            bool wantMargins = (request & ViewRequest.Margins) != 0;
            bool wantSkip = (request & ViewRequest.SceneSkip) != 0;
            bool framePresented = wantSkip ? sceneSkippedPresents > SkippedAtRequest : sceneDrawnPresents > SceneAtRequest;
            bool marginsMatch = marginsApplied == wantMargins || (wantMargins && !wantSkip);
            if (marginsMatch && framePresented) Acked = Seq;
            return Acked;
        }
    }
}
