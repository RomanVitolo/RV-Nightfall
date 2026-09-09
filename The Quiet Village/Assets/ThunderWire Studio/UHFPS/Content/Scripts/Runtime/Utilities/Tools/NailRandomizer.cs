using System.Collections.Generic;
using UnityEngine;
using UHFPS.Tools;
using ThunderWire.Attributes;

namespace UHFPS.Runtime
{
    [InspectorHeader("Nail Randomizer")]
    public class NailRandomizer : MonoBehaviour
    {
        public List<GameObject> Nails = new();

        [Header("Options")] public bool RandomizeNail = true;
        public bool RandomizeRotation = true;

        [Header("Rotation Cone")] 
        public Axis ConeDirection = Axis.X;
        [Range(0f, 60f)] public float ConeAngle = 10f;
        public bool UseLocalDirection = false;
        
        [Header("Gizmos")]
        public bool DrawConeGizmos = true;
        public float GizmoLength = 0.2f;
        public int GizmoSegments = 24;

        private MeshFilter _targetFilter;
        private MeshRenderer _targetRenderer;

        private bool _directionSet;
        private Axis _previousDirection;
        private Vector3 _parentSpaceConeDirection;

        private Vector3 UseDirection
        {
            get
            {
                if (UseLocalDirection)
                {
                    if (!_directionSet || _previousDirection != ConeDirection)
                    {
                        Transform parent = transform.parent;
                        Vector3 localAxis = ConeDirection.Convert();
                        Vector3 worldDir = transform.TransformDirection(localAxis).normalized;

                        if (parent != null)
                        {
                            // Store in parent space so it rotates with the parent
                            _parentSpaceConeDirection = parent.InverseTransformDirection(worldDir).normalized;
                        }
                        else
                        {
                            // No parent: just store world direction directly
                            _parentSpaceConeDirection = worldDir;
                        }

                        _previousDirection = ConeDirection;
                        _directionSet = true;
                    }

                    return transform.parent != null 
                        ? transform.parent.TransformDirection(_parentSpaceConeDirection) 
                        : _parentSpaceConeDirection;
                }

                // Global/world direction: interpret Axis as a plain world vector
                return ConeDirection.Convert().normalized;
            }
        }
        
        [ContextMenu("Randomize")]
        public void Randomize()
        {
            if (RandomizeNail)
            {
                RandomizeNailMesh();
            }

            if (RandomizeRotation)
            {
                RandomizeNailRotation();
            }
        }
        
        [ContextMenu("Reset Rotation")]
        public void ResetRotation()
        {
            transform.localRotation = Quaternion.identity;
        }
        
        [ContextMenu("Reset Direction")]
        public void ResetDirection()
        {
            _parentSpaceConeDirection = Vector3.zero;
            _directionSet = false;
        }

        private void RandomizeNailMesh()
        {
            if (Nails == null || Nails.Count == 0) return;
            if (_targetFilter == null) _targetFilter = GetComponent<MeshFilter>();
            if (_targetRenderer == null) _targetRenderer = GetComponent<MeshRenderer>();

            if (_targetFilter == null) return;

            // Pick a random nail source object
            int index = Random.Range(0, Nails.Count);
            GameObject source = Nails[index];
            if (source == null) return;

            MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
            MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();

            if (sourceFilter != null)
            {
                // Copy mesh
                _targetFilter.sharedMesh = sourceFilter.sharedMesh;
            }

            if (sourceRenderer != null && _targetRenderer != null)
            {
                // Optionally also copy materials
                _targetRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
            }
        }

        private void RandomizeNailRotation()
        {
            Vector3 dir = UseDirection;
            Vector3 baseDir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.up;
            Vector3 randomDir = GetRandomDirectionInCone(baseDir, ConeAngle);
            transform.rotation = Quaternion.LookRotation(randomDir, Vector3.up);
        }

        /// <summary>
        /// Returns a random direction within a cone around 'direction', with maxAngle in degrees.
        /// </summary>
        private Vector3 GetRandomDirectionInCone(Vector3 direction, float maxAngle)
        {
            direction = direction.normalized;
            if (maxAngle <= 0f) return direction;

            // Pick a random axis perpendicular to direction
            Vector3 random = Random.onUnitSphere;
            Vector3 axis = Vector3.Cross(direction, random);
            if (axis.sqrMagnitude < 1e-4f)
            {
                axis = Vector3.Cross(direction, Vector3.up);
            }

            axis.Normalize();

            // Rotate direction around this axis by a random angle within [0, maxAngle]
            float angle = Random.Range(0f, maxAngle);
            Quaternion q = Quaternion.AngleAxis(angle, axis);
            return q * direction;
        }
        
        private void OnDrawGizmosSelected()
        {
            if (!DrawConeGizmos) return;
            if (ConeAngle <= 0f) return;

            Vector3 dir = UseDirection;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.up;

            Vector3 origin = transform.position;

            Gizmos.color = new Color(1f, 0.6470588f, 0.0f, 1f);
            DrawConeGizmo(origin, dir.normalized, ConeAngle, GizmoLength, GizmoSegments);
        }

        private static void DrawConeGizmo(Vector3 origin, Vector3 direction, float angle, float length, int segments)
        {
            direction.Normalize();

            // Draw axis
            Gizmos.DrawLine(origin, origin + direction * length);

            float radius = Mathf.Tan(angle * Mathf.Deg2Rad) * length;
            Vector3 center = origin + direction * length;

            // Find a perpendicular basis
            Vector3 right = Vector3.Cross(direction, Vector3.up);
            if (right.sqrMagnitude < 1e-4f)
            {
                right = Vector3.Cross(direction, Vector3.right);
            }
            right.Normalize();

            Vector3 up = Vector3.Cross(right, direction);
            up.Normalize();

            Vector3 prevPoint = Vector3.zero;

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments * Mathf.PI * 2f;
                Vector3 circleOffset = right * Mathf.Cos(t) * radius + up * Mathf.Sin(t) * radius;
                Vector3 point = center + circleOffset;

                if (i > 0) Gizmos.DrawLine(prevPoint, point);
                if (i < segments) Gizmos.DrawLine(origin, point);

                prevPoint = point;
            }
        }
    }
}