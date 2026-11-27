using LR2.Core;
using UnityEngine;

namespace LR2.FX
{
    /// <summary>Fast streak of light travelling along a bullet path.</summary>

    public static class TracerSystem
    {
        /// <param name="muzzle">Optional: the streak starts from this transform every frame, so it moves with the shooter.</param>
        public static void Spawn(Vector3 from, Vector3 to, bool? isFriendlyTeam = null, Transform muzzle = null)
        {
            var lib = SurfaceLibrary.Instance;
            if (lib == null || lib.tracerPrefab == null) return;
            if ((to - from).sqrMagnitude < 2.25f) return;
            var tracer = LR2Pool.Spawn(lib.tracerPrefab, from, Quaternion.identity);
            var t = tracer != null ? tracer.GetComponent<Tracer>() : null;
            if (t != null) t.Launch(from, to, isFriendlyTeam, muzzle);
        }
    }
}
