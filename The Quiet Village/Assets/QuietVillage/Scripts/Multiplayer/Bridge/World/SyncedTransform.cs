using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.World
{
    /// <summary>
    /// Streams the movement of an object that nothing but its user moves — an examinable without physics.
    /// </summary>
    /// <remarks>
    /// When a player examines an object, UHFPS lifts it in front of their camera and puts it back afterwards.
    /// This makes that visible to everyone else: the object floats up to the examiner, turns as they turn it,
    /// and settles back in place. With no physics or script driving it on the followers' side, there is nothing
    /// to pause — the pose is simply applied.
    /// </remarks>
    [DisallowMultipleComponent]
    public class SyncedTransform : SyncedMotionEntity
    {
        protected override Transform Moving => transform;

        protected override bool LocalSpace => false;

        protected override string CaptureState() => PoseToJson(transform);

        protected override void SuspendDriver() { }

        protected override void ResumeDriver(string finalState) => ApplyPoseJson(transform, finalState);
    }
}
