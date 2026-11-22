using LR2.Core;
using UnityEngine;

namespace LR2.FX
{
    /// <summary>
    /// B136a (user 11-22: "The tracers are quite invisible now, make them way more visible"): keeps a sleek 2.8 cm core
    /// (not a fat rod), fixes the head/tail UV fade + low luminance in HDRP daylight, adds a 2.6 px minimum screen floor
    /// so TAA never erases rounds mid-flight, and slows the streak to 360 m/s over 8.5 m so multiple consecutive frames read clearly.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class Tracer : MonoBehaviour
    {
        [Tooltip("Streak speed (m/s). 280 m/s keeps rounds crisp across 3-5 frames instead of skipping past in 1 frame.")]
        [SerializeField] private float speed = 280f;
        [Tooltip("Visible streak length (m).")]
        [SerializeField] private float length = 10.5f;
        [Tooltip("Core width at the head (m).")]
        [SerializeField] private float width = 0.032f;
        [SerializeField, Range(0.05f, 1f)] private float tailWidthFactor = 0.45f;
        [Tooltip("The tracer compound lights this far past the muzzle (m).")]
        [SerializeField] private float ignitionDistance = 0.45f;
        [Tooltip("Never thinner than this many screen pixels so HDRP TAA does not erase distant streaks.")]
        [SerializeField, Range(0.5f, 5f)] private float minPixels = 3.0f;

        public float Speed => speed;
        public float Length => length;
        public float Width => width;
        public float IgnitionDistance => ignitionDistance;
        public float MinPixels => minPixels;

        // user 11-18: "make my tracers orange/yellow and keep their red. green looks unnatural"
        public static readonly Color FriendlyTracerStart = new Color(1.00f, 0.94f, 0.68f, 1f);
        public static readonly Color FriendlyTracerEnd = new Color(1.00f, 0.54f, 0.08f, 0.28f);
        public static readonly Color EnemyTracerStart = new Color(1.00f, 0.36f, 0.14f, 1f);
        public static readonly Color EnemyTracerEnd = new Color(0.95f, 0.05f, 0.02f, 0.30f);

        private LineRenderer _line;
        private Vector3 _from, _to, _dir;
        private float _travelled, _total;
        private Color _start, _end;
        private float _flicker = 1f;

        public void Launch(Vector3 from, Vector3 to, bool? isFriendlyTeam = null)
        {
            if (_line == null) _line = GetComponent<LineRenderer>();
            _from = from;
            _to = to;
            _total = Vector3.Distance(from, to);
            _dir = _total > 0.001f ? (to - from) / _total : Vector3.forward;
            _travelled = 0f;
            _line.positionCount = 2;
            _line.useWorldSpace = true;
            if (isFriendlyTeam.HasValue)
            {
                _start = isFriendlyTeam.Value ? FriendlyTracerStart : EnemyTracerStart;
                _end = isFriendlyTeam.Value ? FriendlyTracerEnd : EnemyTracerEnd;
            }
            else
            {
                _start = new Color(1f, 0.95f, 0.78f, 1f);
                _end = new Color(1f, 0.68f, 0.22f, 0.28f);
            }
            _flicker = Random.Range(0.90f, 1f);
            _line.startColor = _start * new Color(1f, 1f, 1f, _flicker);
            _line.endColor = _end;

            // B136a: Render an initial visible streak immediately on frame 0 past ignitionDistance so single-frame
            // bursts and screenshot captures never catch an invisible uninitialized tracer.
            float ignite = Mathf.Min(ignitionDistance, _total * 0.35f);
            if (_total >= ignite + 0.35f)
            {
                float head0 = Mathf.Min(_total, ignite + Mathf.Min(length * 0.35f, 3.2f));
                Vector3 headPos0 = _from + _dir * head0;
                _travelled = head0;
                _line.SetPosition(0, headPos0);
                _line.SetPosition(1, _from + _dir * ignite);
                float w0 = width;
                var cam = Camera.main;
                if (cam != null && Screen.height > 0)
                {
                    float perPixel = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;
                    w0 = Mathf.Max(width, Vector3.Distance(cam.transform.position, headPos0) * perPixel * minPixels);
                }
                _line.startWidth = w0;
                _line.endWidth = w0 * tailWidthFactor;
                _line.enabled = true;
            }
            else
            {
                _line.SetPosition(0, from);
                _line.SetPosition(1, from);
                _line.enabled = false;
            }
        }

        private void Update()
        {
            if (_line == null) return;
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            _travelled += speed * dt;
            if (_travelled - length > _total)
            {
                _line.enabled = false;
                LR2Pool.Despawn(gameObject);
                return;
            }
            float ignite = Mathf.Min(ignitionDistance, _total * 0.4f);
            float head = Mathf.Min(_travelled, _total);
            float tail = Mathf.Clamp(_travelled - length, ignite, _total);
            if (head <= ignite + 0.05f)
            {
                _line.enabled = false;
                return;
            }
            Vector3 headPos = _from + _dir * head;
            _line.SetPosition(0, headPos);
            _line.SetPosition(1, _from + _dir * tail);

            // thin up close, never erased by TAA far away
            float w = width;
            var cam = Camera.main;
            if (cam != null && Screen.height > 0)
            {
                float perPixel = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;
                w = Mathf.Max(width, Vector3.Distance(cam.transform.position, headPos) * perPixel * minPixels);
            }
            _line.startWidth = w;
            _line.endWidth = w * tailWidthFactor;
            _line.enabled = true;
        }
    }
}
