using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine.Rendering.Universal;
using UnityEngine.Events;
using UnityEngine;

namespace UHFPS.Runtime
{
    public partial class UVFlashlightReveal : MonoBehaviour, ISaveable
    {
        public enum EMaterialSource
        {
            DecalProjector,
            RendererMaterial,
            CustomMaterial
        }
        
        public static readonly int UVLightPosition      = Shader.PropertyToID("_UVLightPositionWS");
        public static readonly int UVLightDirection     = Shader.PropertyToID("_UVLightDirectionWS");
        public static readonly int UVLightAngle         = Shader.PropertyToID("_UVLightAngle");
        public static readonly int UVLightRange         = Shader.PropertyToID("_UVLightRange");
        public static readonly int UVLightIntensity     = Shader.PropertyToID("_UVLightIntensity");
        public static readonly int UVLightAngleAdjust   = Shader.PropertyToID("_AngleAdjust");
        
#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.AutoStaticsCleanup]
#endif
        public static readonly List<UVFlashlightReveal> Reveals = new();
        
#if UNITY_6000_5_OR_NEWER
        [Unity.Scripting.LifecycleManagement.AutoStaticsCleanup]
#endif
        public static bool IsUVFlashlightEnabled { get; set; } = false;

        public EMaterialSource MaterialSource = EMaterialSource.DecalProjector;
        
        public DecalProjector DecalProjector;
        public RendererMaterial RendererMaterial;
        public Material CustomMaterial;
        
        public bool EnableRevealEvent = true;
        public Vector3 RectOffset = Vector3.zero;
        public Vector2 RectSize = new(5f, 5f);
        [Range(0f, 1f)] public float RevealThreshold = 0.05f;
        public bool UseOcclusionRaycast = true;
        public LayerMask OcclusionMask = ~0;
        
        public UnityEvent OnRevealed;
        
        private Vector3 lastLightPosWS;
        private Vector3 lastLightDirWS;
        private float lastLightAngleRad;
        private float lastLightRange;
        private float lastAngleAdjust;
        
        private bool wasFullyLitLastTime;
        private readonly Vector3[] rectCornersWS = new Vector3[4];
        
        public Material RevealMaterial
        {
            get
            {
                return MaterialSource switch
                {
                    EMaterialSource.DecalProjector => DecalProjector != null ? DecalProjector.material : null,
                    EMaterialSource.RendererMaterial => RendererMaterial.IsAssigned ? RendererMaterial.ClonedMaterial : null,
                    EMaterialSource.CustomMaterial => CustomMaterial,
                    _ => null
                };
            }
        }
        
        // --------------------------------------------------
        // UNITY METHODS
        // --------------------------------------------------
        
        private void Awake()
        {
            if (DecalProjector == null)
                DecalProjector = GetComponent<DecalProjector>();
        }

        private void OnEnable()
        {
            if (!Reveals.Contains(this))
                Reveals.Add(this);
            
            ClearLightData();
        }

        private void OnDisable()
        {
            Reveals.Remove(this);
            ClearLightData();
        }

        private void Update()
        {
            if (!EnableRevealEvent || !IsUVFlashlightEnabled)
                return;

            bool fullyLitNow = AreRectangleCornersFullyLit();
            if (fullyLitNow && !wasFullyLitLastTime)
            {
                OnRevealed?.Invoke();
                wasFullyLitLastTime = true;
            }
        }
        
        // --------------------------------------------------
        // PUBLIC API
        // --------------------------------------------------
        
        /// <summary>
        /// Set the light data that will be passed to the shader for revealing.
        /// </summary>
        public void SetLightData(Vector3 positionWS, Vector3 directionWS, float angleRad, float range, float intensity = 1f)
        {
            if (RevealMaterial == null)
                return;
            
            lastLightPosWS    = positionWS;
            lastLightDirWS    = directionWS;
            lastLightAngleRad = angleRad;
            lastLightRange    = range;
            lastAngleAdjust   = RevealMaterial.GetFloat(UVLightAngleAdjust);
            
            var mat = RevealMaterial;
            mat.SetVector(UVLightPosition,  positionWS);
            mat.SetVector(UVLightDirection, directionWS);
            mat.SetFloat(UVLightAngle,     angleRad);
            mat.SetFloat(UVLightRange,     range);
            mat.SetFloat(UVLightIntensity, intensity);
        }

        /// <summary>
        /// Reset the light data to default values.
        /// </summary>
        public void ResetLightData()
        {
            if (RevealMaterial == null)
                return;
            
            lastLightPosWS    = transform.position;
            lastLightDirWS    = Vector3.forward;
            lastLightAngleRad = 90f * 0.5f * Mathf.Deg2Rad;
            lastLightRange    = 100f;
            lastAngleAdjust   = 0f;
            
            var mat = RevealMaterial;
            mat.SetVector(UVLightPosition,  transform.position);
            mat.SetVector(UVLightDirection, Vector3.forward);
            mat.SetFloat(UVLightAngle,     90f * 0.5f * Mathf.Deg2Rad);
            mat.SetFloat(UVLightRange,     100f);
            mat.SetFloat(UVLightIntensity, 1f);
        }

        /// <summary>
        /// Clear the light data, setting parameters to default values.
        /// </summary>
        public void ClearLightData()
        {
            if (RevealMaterial == null)
                return;
            
            lastLightPosWS    = Vector3.zero;
            lastLightDirWS    = Vector3.zero;
            lastLightAngleRad = 0f;
            lastLightRange    = 0f;
            lastAngleAdjust   = 0f;

            var mat = RevealMaterial;
            mat.SetVector(UVLightPosition, Vector3.zero);
            mat.SetVector(UVLightDirection, Vector3.zero);
            mat.SetFloat(UVLightAngle, 0f);
            mat.SetFloat(UVLightRange, 0f);
            mat.SetFloat(UVLightIntensity, 0f);
        }
        
        // --------------------------------------------------
        // INTERNAL LOGIC
        // --------------------------------------------------
        
        private bool AreRectangleCornersFullyLit()
        {
            FillRectangleCornerWorldPositions(rectCornersWS);

            float adjustRad = lastAngleAdjust * Mathf.Deg2Rad;
            float cosHalfAngle = Mathf.Cos(lastLightAngleRad + adjustRad);
            float halfAngle = cosHalfAngle * (1f - RevealThreshold);
            
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = rectCornersWS[i];
                Vector3 toCorner = corner - lastLightPosWS;
                float distance = toCorner.magnitude;

                // Out of range case
                if (distance > lastLightRange)
                    return false;

                if (distance < 0.0001f)
                    continue;

                Vector3 dirToCorner = toCorner / Mathf.Max(0.0001f, distance);
                float dot = Vector3.Dot(lastLightDirWS, dirToCorner);

                // Outside cone case
                if (dot < halfAngle)
                    return false;
                
                // Behind wall case
                if (UseOcclusionRaycast && IsCornerOccluded(lastLightPosWS, corner))
                    return false;
            }

            return true;
        }
        
        private bool IsCornerOccluded(Vector3 lightPosWS, Vector3 cornerWS)
        {
            Vector3 dir = cornerWS - lightPosWS;
            float dist = dir.magnitude;
            
            if (dist < 0.0001f)
                return false;

            dir /= dist;
            return Physics.Raycast(lightPosWS, dir, dist, OcclusionMask, QueryTriggerInteraction.Ignore);
        }
                
        private void FillRectangleCornerWorldPositions(Vector3[] cornersOut)
        {
            Vector3 c = RectOffset;
            float halfW = RectSize.x * 0.5f;
            float halfH = RectSize.y * 0.5f;
            
            Vector3 local0 = new Vector3(c.x - halfW, c.y - halfH, c.z);
            Vector3 local1 = new Vector3(c.x - halfW, c.y + halfH, c.z);
            Vector3 local2 = new Vector3(c.x + halfW, c.y + halfH, c.z);
            Vector3 local3 = new Vector3(c.x + halfW, c.y - halfH, c.z);

            cornersOut[0] = transform.TransformPoint(local0);
            cornersOut[1] = transform.TransformPoint(local1);
            cornersOut[2] = transform.TransformPoint(local2);
            cornersOut[3] = transform.TransformPoint(local3);
        }
        
        // --------------------------------------------------
        // GIZMOS
        // --------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            if (!EnableRevealEvent)
                return;
                
            FillRectangleCornerWorldPositions(rectCornersWS);
            
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(rectCornersWS[0], rectCornersWS[1]);
            Gizmos.DrawLine(rectCornersWS[1], rectCornersWS[2]);
            Gizmos.DrawLine(rectCornersWS[2], rectCornersWS[3]);
            Gizmos.DrawLine(rectCornersWS[3], rectCornersWS[0]);
        }

        public StorableCollection OnSave()
        {
            return new StorableCollection()
            {
                { "isRevealed" , wasFullyLitLastTime },
            };
        }

        public void OnLoad(JToken data)
        {
            wasFullyLitLastTime = (bool)data["isRevealed"];
        }
    }
}