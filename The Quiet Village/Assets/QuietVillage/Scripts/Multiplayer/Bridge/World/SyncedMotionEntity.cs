using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.World
{
    /// <summary>
    /// Streams the movement of one transform from the client moving it to everyone else.
    /// </summary>
    /// <remarks>
    /// Mirrors the motion rather than the logic behind it. A door opened by a click, dragged by the mouse,
    /// rattled because it is locked, or swung by a puzzle's event all move the same transform, so watching that
    /// transform catches every case without knowing about any of them — including changes no interaction hook
    /// would see.
    ///
    /// The client whose copy moved first becomes the author and streams poses; the server lets one author
    /// stream at a time. Followers pause their own copy's driver (the door script, the rigidbody) so it cannot
    /// fight the stream, ease towards each pose, and on the author's reliable end message apply its final state
    /// through the driver's own save format, which leaves their copy consistent for whoever uses it next.
    /// </remarks>
    public abstract class SyncedMotionEntity : WorldSyncEntity
    {
        [Header("Motion")]
        [Tooltip("Pose packets per second while moving.")]
        [SerializeField] private float m_sendRate = 20f;

        [Tooltip("Movement smaller than this (m) is not a change: numeric noise must not start a stream.")]
        [SerializeField] private float m_positionThreshold = 0.002f;

        [Tooltip("Rotation smaller than this (degrees) is not a change.")]
        [SerializeField] private float m_angleThreshold = 0.25f;

        [Tooltip("Seconds without movement after which the author sends the final state and stops streaming.")]
        [SerializeField] private float m_restTime = 0.35f;

        [Tooltip("How quickly followers close the gap to the latest pose. Higher is snappier, lower is smoother.")]
        [SerializeField] private float m_followSharpness = 18f;

        [Tooltip("Seconds after applying a remote final state during which this copy's own movement is ignored — " +
                 "a rigidbody settling into place must not bounce authorship straight back.")]
        [SerializeField] private float m_quietAfterApply = 1f;

        private enum Mode { Idle, Authoring, Following }

        private Mode m_mode;
        private Pose m_observed;
        private Pose m_lastSent;
        private float m_lastMovedAt;
        private float m_nextSendAt;
        private float m_quietUntil;

        private Pose m_followTarget;
        private ulong m_followAuthor;

        /// <summary>The transform whose movement is replicated.</summary>
        protected abstract Transform Moving { get; }

        /// <summary>Whether poses are exchanged in the parent's space (a door leaf) or the world's (a loose prop).</summary>
        protected abstract bool LocalSpace { get; }

        /// <summary>Full state sent with the begin and end of a stream, in the driver's own save format.</summary>
        protected abstract string CaptureState();

        /// <summary>Stops the local driver moving <see cref="Moving"/> while a remote stream does.</summary>
        protected abstract void SuspendDriver();

        /// <summary>Applies the author's final state, if any, and hands movement back to the local driver.</summary>
        protected abstract void ResumeDriver(string finalState);

        /// <summary>Whether a still object may end its stream. A rigidbody still sliding may not.</summary>
        protected virtual bool CanRest => true;

        /// <summary>Called when a remote stream starts, e.g. to play the sound the author heard.</summary>
        protected virtual void OnRemoteBegin(string state) { }

        protected bool IsFollowing => m_mode == Mode.Following;

        /// <summary>Keeps an authored stream open while set, even when the object is still.</summary>
        /// <remarks>
        /// Set while this client examines the object. Held still in front of the camera it stops moving, and
        /// ending the stream then would hand followers a final pose in mid-air — a physics prop would drop out
        /// of the examiner's hands on every other screen.
        /// </remarks>
        public bool HoldOpen { get; set; }

        protected override void OnBound()
        {
            m_mode = Mode.Idle;
            m_observed = ReadPose();
        }

        protected override void OnUnbound()
        {
            if (m_mode == Mode.Following) ResumeDriver(null);
            m_mode = Mode.Idle;
        }

        protected virtual void OnDisable()
        {
            // An object switched off mid-stream (a physics pickup being taken) must not leave followers waiting.
            if (m_mode == Mode.Authoring && IsLive) World.PublishMotionEnd(this, CaptureState());
            if (m_mode == Mode.Following) ResumeDriver(null);

            m_mode = Mode.Idle;
        }

        protected virtual void LateUpdate()
        {
            if (!IsLive || Moving == null) return;

            switch (m_mode)
            {
                case Mode.Idle:
                    TickIdle();
                    break;
                case Mode.Authoring:
                    TickAuthoring();
                    break;
                case Mode.Following:
                    TickFollowing();
                    break;
            }
        }

        private void TickIdle()
        {
            var pose = ReadPose();
            if (!HasMoved(m_observed, pose)) return;

            // Settling, or just applied someone else's final state: absorb the movement without publishing it.
            if (!CanPublish || Time.time < m_quietUntil)
            {
                m_observed = pose;
                return;
            }

            m_mode = Mode.Authoring;
            m_observed = pose;
            m_lastMovedAt = Time.time;
            World.PublishMotionBegin(this, CaptureState());
            SendPose(pose);
        }

        private void TickAuthoring()
        {
            var pose = ReadPose();
            if (HasMoved(m_observed, pose))
            {
                m_observed = pose;
                m_lastMovedAt = Time.time;
            }

            if (Time.time >= m_nextSendAt && HasMoved(m_lastSent, pose)) SendPose(pose);

            if (Time.time - m_lastMovedAt >= m_restTime && CanRest && !HoldOpen)
            {
                m_mode = Mode.Idle;
                World.PublishMotionEnd(this, CaptureState());
                m_observed = ReadPose();
            }
        }

        private void SendPose(Pose pose)
        {
            World.PublishMotion(this, pose.position, pose.rotation);
            m_lastSent = pose;
            m_nextSendAt = Time.time + 1f / Mathf.Max(1f, m_sendRate);
        }

        private void TickFollowing()
        {
            // Frame-rate independent exponential ease towards the latest pose received.
            var blend = 1f - Mathf.Exp(-m_followSharpness * Time.deltaTime);
            var current = ReadPose();

            WritePose(new Pose(
                Vector3.Lerp(current.position, m_followTarget.position, blend),
                Quaternion.Slerp(current.rotation, m_followTarget.rotation, blend)));
        }

        /// <summary>Streams from now on, moving or not, for a dropped item's first moments on its dropper's copy.</summary>
        /// <remarks>
        /// The other copies start out held still (<see cref="FollowFrom"/>) and only move when this stream says so.
        /// It must reach them even if the item landed before its id arrived, which the usual start, on movement,
        /// would never send.
        /// </remarks>
        internal void StartAuthoring()
        {
            if (!IsLive || Moving == null) return;

            var pose = ReadPose();
            m_mode = Mode.Authoring;
            m_observed = pose;
            m_lastMovedAt = Time.time;
            World.PublishMotionBegin(this, CaptureState());
            SendPose(pose);
        }

        /// <summary>
        /// Holds this copy still until <paramref name="author"/>'s stream moves it, for a dropped item created here
        /// on another client's behalf. Left to its own physics it would start a competing stream of its own.
        /// </summary>
        internal void FollowFrom(ulong author) => ApplyRemoteMotionBegin(null, author);

        internal override void ApplyRemoteMotionBegin(string state, ulong author)
        {
            // If this copy was authoring too, the server has granted the other client; its stream wins here.
            m_mode = Mode.Following;
            m_followAuthor = author;
            SuspendDriver();
            m_followTarget = ReadPose();
            OnRemoteBegin(state);
        }

        internal override void ApplyRemoteMotion(Vector3 position, Quaternion rotation, ulong author)
        {
            // Poses only count inside a stream begun by the same author. Unreliable packets can outlive their
            // stream's end, and one arriving late must not re-open a finished stream or yank the object back.
            if (m_mode != Mode.Following || author != m_followAuthor) return;

            m_followTarget = new Pose(position, rotation);
        }

        internal override void ApplyRemoteMotionEnd(string state, ulong author)
        {
            if (m_mode == Mode.Following && author != m_followAuthor) return;

            ResumeDriver(state);
            m_mode = Mode.Idle;
            m_observed = ReadPose();
            m_quietUntil = Time.time + m_quietAfterApply;
        }

        internal override void OnMotionDenied()
        {
            // Another client owns this movement; its begin message puts this copy into following.
            if (m_mode == Mode.Authoring) m_mode = Mode.Idle;
            m_observed = ReadPose();
        }

        protected Pose ReadPose()
        {
            var moving = Moving;
            if (moving == null) return default;

            return LocalSpace
                ? new Pose(moving.localPosition, moving.localRotation)
                : new Pose(moving.position, moving.rotation);
        }

        protected virtual void WritePose(Pose pose)
        {
            var moving = Moving;
            if (moving == null) return;

            if (LocalSpace) moving.SetLocalPositionAndRotation(pose.position, pose.rotation);
            else moving.SetPositionAndRotation(pose.position, pose.rotation);
        }

        /// <summary>A world pose in the compact form stream begin and end messages carry.</summary>
        protected static string PoseToJson(Transform target)
        {
            if (target == null) return null;

            var position = target.position;
            var rotation = target.rotation;
            return new JObject
            {
                ["position"] = new JArray(position.x, position.y, position.z),
                ["rotation"] = new JArray(rotation.x, rotation.y, rotation.z, rotation.w)
            }.ToString(Formatting.None);
        }

        /// <summary>Applies a pose written by <see cref="PoseToJson"/>; ignores anything else.</summary>
        protected static void ApplyPoseJson(Transform target, string json)
        {
            if (target == null) return;

            var state = Parse(json);
            if (state?["position"] is JArray p && p.Count == 3 && state["rotation"] is JArray r && r.Count == 4)
            {
                target.SetPositionAndRotation(
                    new Vector3((float)p[0], (float)p[1], (float)p[2]),
                    new Quaternion((float)r[0], (float)r[1], (float)r[2], (float)r[3]));
            }
        }

        private bool HasMoved(Pose from, Pose to) =>
            (to.position - from.position).sqrMagnitude > m_positionThreshold * m_positionThreshold
            || Quaternion.Angle(from.rotation, to.rotation) > m_angleThreshold;
    }
}
