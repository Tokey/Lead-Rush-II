using LR2.Core;
using UnityEngine;

namespace LR2.FX
{
    /// <summary>
    /// A streak of light from the muzzle to where the (hitscan) bullet hit. It keeps a thin core with a screen-size floor so
    /// HDRP's TAA does not erase it (B136a), and since 11-26 it flies at a rifle-like speed and rides with the gun.
    /// The developer: "while strafing, the bullets get too lagged behind". The streak used to fly a fixed world path from
    /// where the muzzle was at the shot, at 280 m/s: strafing at 5 m/s moved the gun about 0.45 m away from a 25 m streak
    /// before it landed, so it trailed behind. Now it is drawn from the muzzle's current position every frame (after the
    /// gun's final pose) to the impact point, like a round that carries the shooter's motion, and it is 2.5x faster.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    [DefaultExecutionOrder(900)] // after Kinemation (0), WeaponAntiClip (200) and GenericBoneMirror (550): the gun is in its final pose
    public class Tracer : MonoBehaviour
    {
        [Tooltip("Streak speed (m/s). Fast enough to stay with a strafing shooter; the longer streak keeps it visible for a few frames.")]
        [SerializeField] private float speed = 720f;
        [Tooltip("Visible streak length (m).")]
        [SerializeField] private float length = 15f;
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
        private Transform _muzzle;

        /// <summary>
        /// Fires the streak from <paramref name="from"/> to <paramref name="to"/>. With <paramref name="muzzle"/> set, the
        /// streak starts from that transform's current position every frame, so it moves with the shooter.
        /// </summary>
        public void Launch(Vector3 from, Vector3 to, bool? isFriendlyTeam = null, Transform muzzle = null)
        {
            if (_line == null) _line = GetComponent<LineRenderer>();
            _muzzle = muzzle;
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

        private void LateUpdate()
        {
            if (_line == null) return;
            if (_muzzle != null)
            {
                if (_muzzle.gameObject.activeInHierarchy)
                {
                    _from = _muzzle.position;
                    _total = Vector3.Distance(_from, _to);
                    _dir = _total > 0.001f ? (_to - _from) / _total : _dir;
                }
                else _muzzle = null; // weapon switched or shooter died: finish on the last path
            }
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
