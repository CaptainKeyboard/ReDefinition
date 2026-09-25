using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The arithmetic of RenderInterpolation, apart from KSP so the tests can run it:
    // from where the physics steps left a body, the offset that draws it where it was
    // between its last two steps at this frame's time, as Unity's own interpolation
    // does -- lerp(previous, current, alpha), alpha the part of a step since the latest
    // one began. A step moves the state from fixedTime to fixedTime + fixedDelta, so a
    // body in steady motion is drawn exactly where it is at the frame's time less one
    // step, and every frame moves it on, a frame without a step of its own included.
    internal sealed class StepInterpolator
    {
        private Vector3 current;
        private Vector3 previousStep;
        private float lastFixedTime = -1f;
        private bool known;

        // Physics steps since the last call; 0 on a frame without one.
        internal int Steps { get; private set; }

        // Forgets the history: the next frame is drawn where its step left it.
        internal void Reset()
        {
            known = false;
        }

        // Once a frame, after physics: the body's position as the steps left it, and
        // Unity's clocks. The offset to add for this frame's drawing.
        internal Vector3 Advance(Vector3 position, float time, float fixedTime, float fixedDelta)
        {
            Steps = lastFixedTime < 0f || fixedDelta <= 0f ? 0 : Mathf.RoundToInt((fixedTime - lastFixedTime) / fixedDelta);
            lastFixedTime = fixedTime;
            if (!known)
            {
                current = position;
                previousStep = position;
                known = true;
                return Vector3.zero;
            }
            if (Steps > 0)
            {
                previousStep = position - (position - current) / Steps;
                current = position;
            }
            float alpha = fixedDelta > 0f ? Mathf.Clamp01((time - fixedTime) / fixedDelta) : 1f;
            return (previousStep - current) * (1f - alpha);
        }

        // Where the last step left it, for a jump test.
        internal Vector3 Current
        {
            get { return current; }
        }

        internal bool Known
        {
            get { return known; }
        }
    }
}
