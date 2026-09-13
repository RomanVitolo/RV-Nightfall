using UnityEngine;

namespace QuietVillage.Multiplayer.Characters
{
    /// <summary>Builds a character's body from its vendor prefab, ready for the shared humanoid animations.</summary>
    /// <remarks>
    /// One recipe for the lobby preview and the in-game remote body, so a character cannot look right in one and wrong in
    /// the other.
    /// </remarks>
    public static class CharacterBodies
    {
        /// <summary>Instantiates a body under <paramref name="parent"/>, configured and active.</summary>
        /// <param name="avatar">Humanoid Avatar the rig maps through; without it the shared clips cannot play.</param>
        /// <param name="controller">The shared body controller; the prefab's own is replaced.</param>
        public static GameObject Create(GameObject prefab, Transform parent, Avatar avatar,
            RuntimeAnimatorController controller, float scale)
        {
            if (prefab == null) return null;

            // Built under an inactive holder, so nothing on the vendor prefab wakes up before it is configured: its own
            // Animator would otherwise start with root motion and a Generic avatar, and walk the body off its spot.
            var holder = new GameObject("BodyHolder");
            holder.SetActive(false);

            var body = Object.Instantiate(prefab, holder.transform, false);
            body.name = prefab.name;

            var animator = body.GetComponent<Animator>();
            if (animator == null) animator = body.AddComponent<Animator>();

            if (controller != null) animator.runtimeAnimatorController = controller;
            if (avatar != null) animator.avatar = avatar;

            // Something else moves the body: the CharacterController and NetworkTransform in game, the turntable here.
            animator.applyRootMotion = false;

            // AvatarAim edits bones after the Animator writes them; with culling an off-screen body would skip that
            // write and the edit would compound. Only a few bodies ever exist, so always animating is cheap.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            if (avatar == null)
                Debug.LogWarning($"{nameof(CharacterBodies)}: '{prefab.name}' has no Humanoid Avatar in the character " +
                                 "catalog, so it cannot play the shared animations.", body);

            body.transform.SetParent(parent, false);
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;
            body.transform.localScale = Vector3.one * Mathf.Max(0.1f, scale);
            Object.Destroy(holder);

            return body;
        }

        /// <summary>Gives a whole body one layer, so it is lit and culled like whatever it replaced.</summary>
        public static void SetLayer(GameObject body, int layer)
        {
            if (body == null) return;

            foreach (var child in body.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
        }
    }
}
