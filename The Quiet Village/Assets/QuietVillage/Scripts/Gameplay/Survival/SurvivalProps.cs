using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// Plain shapes for the survival tools placed at runtime (the chapel bell, trap spots, a lit flare), built from Unity
    /// primitives so they need no art assets and no scene changes.
    /// </summary>
    /// <remarks>
    /// Placeholders in the same spirit as the greybox: readable at a glance, easy to replace with real models later by
    /// swapping what these build. Primitive colliders are removed; each tool adds the one collider it needs.
    /// </remarks>
    public static class SurvivalProps
    {
        /// <summary>A primitive with no collider, coloured, parented and placed in local space.</summary>
        public static GameObject Shape(PrimitiveType type, Transform parent, Vector3 localPosition, Vector3 localScale,
            Color color, Quaternion? localRotation = null, float emission = 0f)
        {
            var shape = GameObject.CreatePrimitive(type);
            Object.Destroy(shape.GetComponent<Collider>());

            shape.transform.SetParent(parent, false);
            shape.transform.localPosition = localPosition;
            shape.transform.localRotation = localRotation ?? Quaternion.identity;
            shape.transform.localScale = localScale;

            var renderer = shape.GetComponent<Renderer>();
            var material = renderer.material;
            material.color = color;

            if (emission > 0f)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * emission);
            }

            return shape;
        }

        /// <summary>Sets every object in a hierarchy to a layer, e.g. Interact so UHFPS's reticle finds it.</summary>
        public static void SetLayer(GameObject root, string layerName)
        {
            var layer = LayerMask.NameToLayer(layerName);
            if (layer < 0) return;

            foreach (var child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
        }

        /// <summary>Where the ground is under a point, or the point itself if nothing is found.</summary>
        public static Vector3 Ground(Vector3 point, float searchUp = 1.5f, float searchDown = 4f)
        {
            var origin = point + Vector3.up * searchUp;
            var mask = LayerMask.GetMask("Default", "Ground");
            if (mask == 0) mask = Physics.DefaultRaycastLayers;

            return Physics.Raycast(origin, Vector3.down, out var hit, searchUp + searchDown, mask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : point;
        }
    }
}
