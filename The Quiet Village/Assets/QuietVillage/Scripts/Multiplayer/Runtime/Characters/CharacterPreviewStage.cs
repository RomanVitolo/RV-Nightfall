using System.Collections.Generic;
using UnityEngine;

namespace QuietVillage.Multiplayer.Characters
{
    /// <summary>
    /// A small lit stage out of the lobby camera's sight that shows the chosen body, turning, into a texture the
    /// Character screen draws.
    /// </summary>
    /// <remarks>
    /// The body is the same prefab, Avatar and controller a teammate will see in the level, so what the preview shows is
    /// what the others get. It stands in the controller's default state, which is idle.
    ///
    /// Placed far below the lobby rather than on a layer of its own: UHFPS claims most of the project's layers, and the
    /// lobby's camera looks straight ahead at nothing, so distance keeps the two apart without spending one. The camera
    /// only renders while the Character screen is open.
    /// </remarks>
    public class CharacterPreviewStage : MonoBehaviour
    {
        [SerializeField] private int m_resolution = 1024;

        [Tooltip("Degrees per second the body turns on its own while nobody drags it.")]
        [SerializeField] private float m_idleTurnSpeed = 18f;

        [Tooltip("Seconds after a drag before the body starts turning on its own again.")]
        [SerializeField] private float m_idleTurnDelay = 2.5f;

        [SerializeField, Range(10f, 60f)] private float m_fieldOfView = 26f;

        [Tooltip("Space kept around the body in the frame, as a share of its height.")]
        [SerializeField] private float m_framePadding = 0.12f;

        [SerializeField] private Color m_keyLightColour = new(1f, 0.93f, 0.85f);
        [SerializeField] private Color m_rimLightColour = new(0.55f, 0.65f, 0.85f);

        [Tooltip("Size in pixels of the head-and-shoulders pictures on the Look buttons.")]
        [SerializeField] private int m_thumbnailResolution = 128;

        /// <summary>A prop placed behind the body, in the stage's own space (the body stands at the origin facing +Z).</summary>
        [System.Serializable]
        public class BackdropPiece
        {
            public GameObject Prefab;
            public Vector3 Position;
            public float Yaw;
            [Min(0.01f)] public float Scale = 1f;
        }

        [Header("Mood")]
        [Tooltip("Night scene behind the body: ground, props, fog and moonlight. Off shows the body on the screen's own panel.")]
        [SerializeField] private bool m_moodEnabled = true;

        [Tooltip("Sky colour behind everything; the fog fades to it.")]
        [SerializeField] private Color m_nightColour = new(0.035f, 0.045f, 0.065f);

        [SerializeField] private Color m_ambientColour = new(0.09f, 0.1f, 0.13f);

        [Tooltip("Exponential squared fog. Around 0.1 hides the far props; lower shows more of the cemetery.")]
        [SerializeField, Range(0f, 0.5f)] private float m_fogDensity = 0.11f;

        [SerializeField] private Color m_moonLightColour = new(0.62f, 0.72f, 0.95f);
        [SerializeField, Range(0f, 3f)] private float m_moonLightIntensity = 0.85f;

        [Tooltip("Warm light low beside the body, as from the candles. Zero intensity turns it off.")]
        [SerializeField] private Color m_candleLightColour = new(1f, 0.62f, 0.3f);
        [SerializeField, Range(0f, 8f)] private float m_candleLightIntensity = 2.2f;
        [SerializeField] private float m_candleLightRange = 3.5f;
        [SerializeField] private Vector3 m_candleLightPosition = new(0.9f, 0.45f, -0.9f);

        [Tooltip("Floor the body stands on. Without one there is no ground, and no shadow.")]
        [SerializeField] private Material m_groundMaterial;

        [SerializeField] private List<BackdropPiece> m_backdrop = new();

        private const float FallbackHeight = 1.8f;

        // Far enough to the side that neither camera sees the other's body.
        private static readonly Vector3 ThumbnailSpot = new(40f, 0f, 0f);

        // Share of the body's height the thumbnail centres on, and how much of it the frame spans: head and shoulders.
        private const float ThumbnailCentre = 0.8f;
        private const float ThumbnailSpan = 0.42f;

        /// <summary>A look waiting for its picture.</summary>
        private readonly struct ThumbnailJob
        {
            public readonly CharacterCatalog.Character Character;
            public readonly int Variant;
            public readonly RuntimeAnimatorController Controller;
            public readonly RenderTexture Target;

            public ThumbnailJob(CharacterCatalog.Character character, int variant, RuntimeAnimatorController controller,
                RenderTexture target)
            {
                Character = character;
                Variant = variant;
                Controller = controller;
                Target = target;
            }
        }

        private readonly Dictionary<(CharacterCatalog.Character, int), RenderTexture> m_thumbnails = new();
        private readonly Queue<ThumbnailJob> m_thumbnailJobs = new();
        private Camera m_thumbnailCamera;
        private Transform m_thumbnailSpot;
        private GameObject m_thumbnailBody;

        private Camera m_camera;
        private RenderTexture m_texture;
        private Transform m_turntable;
        private GameObject m_body;
        // Bodies face +Z and the camera looks back along it, so 0 starts them facing the player.
        private float m_yaw;
        private float m_lastDragTime = float.NegativeInfinity;

        private CharacterCatalog.Character m_shownCharacter;
        private int m_shownVariant = -1;

        /// <summary>What the stage camera sees. Created on first use.</summary>
        public Texture Texture
        {
            get
            {
                EnsureStage();
                return m_texture;
            }
        }

        /// <summary>Starts or stops rendering, e.g. as the Character screen opens and closes.</summary>
        public void SetShowing(bool showing)
        {
            EnsureStage();
            if (showing == m_camera.enabled) return;

            m_camera.enabled = showing;
            if (!m_moodEnabled) return;

            if (showing) ApplyMood();
            else RestoreLobbyLighting();
        }

        // ---- Mood ------------------------------------------------------------------------------------

        // The lobby's own lighting, put back when the screen closes. Fog, ambient light and the sun are scene-wide, so the
        // stage can only have its night while it is the thing being looked at.
        private bool m_savedFog;
        private FogMode m_savedFogMode;
        private Color m_savedFogColour;
        private float m_savedFogDensity;
        private UnityEngine.Rendering.AmbientMode m_savedAmbientMode;
        private Color m_savedAmbientColour;
        private Light m_savedSun;
        private Light m_moonLight;

        private void ApplyMood()
        {
            m_savedFog = RenderSettings.fog;
            m_savedFogMode = RenderSettings.fogMode;
            m_savedFogColour = RenderSettings.fogColor;
            m_savedFogDensity = RenderSettings.fogDensity;
            m_savedAmbientMode = RenderSettings.ambientMode;
            m_savedAmbientColour = RenderSettings.ambientLight;
            m_savedSun = RenderSettings.sun;

            RenderSettings.fog = m_fogDensity > 0f;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = m_nightColour;
            RenderSettings.fogDensity = m_fogDensity;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = m_ambientColour;

            // URP casts shadows only from its main light; naming the moon makes sure it is the one.
            RenderSettings.sun = m_moonLight;
        }

        private void RestoreLobbyLighting()
        {
            RenderSettings.fog = m_savedFog;
            RenderSettings.fogMode = m_savedFogMode;
            RenderSettings.fogColor = m_savedFogColour;
            RenderSettings.fogDensity = m_savedFogDensity;
            RenderSettings.ambientMode = m_savedAmbientMode;
            RenderSettings.ambientLight = m_savedAmbientColour;
            RenderSettings.sun = m_savedSun;
        }

        /// <summary>Lays the ground and the props behind the body, and the candle light among them.</summary>
        private void BuildBackdrop()
        {
            var root = new GameObject("Backdrop").transform;
            root.SetParent(transform, false);

            if (m_groundMaterial != null)
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
                Destroy(ground.GetComponent<Collider>());
                ground.transform.SetParent(root, false);
                ground.transform.localScale = new Vector3(3f, 1f, 3f);
                ground.GetComponent<Renderer>().sharedMaterial = m_groundMaterial;
            }

            foreach (var piece in m_backdrop)
            {
                if (piece?.Prefab == null) continue;

                var instance = Instantiate(piece.Prefab, root, false);
                instance.transform.localPosition = piece.Position;
                instance.transform.localRotation = Quaternion.Euler(0f, piece.Yaw, 0f);
                instance.transform.localScale = Vector3.one * piece.Scale;

                // Scenery only: the pack's own lights would fight the candle light below, and nothing here is touched.
                foreach (var collider in instance.GetComponentsInChildren<Collider>(true)) Destroy(collider);
                foreach (var light in instance.GetComponentsInChildren<Light>(true)) light.enabled = false;
                foreach (var behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
            }

            if (m_candleLightIntensity <= 0f) return;

            var candleObject = new GameObject("CandleLight");
            candleObject.transform.SetParent(root, false);
            candleObject.transform.localPosition = m_candleLightPosition;

            var candle = candleObject.AddComponent<Light>();
            candle.type = LightType.Point;
            candle.color = m_candleLightColour;
            candle.intensity = m_candleLightIntensity;
            candle.range = m_candleLightRange;
            candle.shadows = LightShadows.None;
        }

        /// <summary>Puts a body on the stage, replacing the last. Showing the same one again does nothing.</summary>
        public void Show(CharacterCatalog.Character character, int variantIndex, RuntimeAnimatorController controller)
        {
            EnsureStage();

            if (ReferenceEquals(character, m_shownCharacter) && variantIndex == m_shownVariant && m_body != null) return;

            m_shownCharacter = character;
            m_shownVariant = variantIndex;

            if (m_body != null) Destroy(m_body);
            m_body = null;

            var variant = character?.VariantAt(variantIndex);
            if (variant == null || variant.Prefab == null) return;

            m_body = CharacterBodies.Create(variant.Prefab, m_turntable, character.HumanoidAvatar, controller,
                character.BodyScale);
            Frame();
        }

        /// <summary>A head-and-shoulders picture of one look, for its button. Blank until drawn, a frame or so later.</summary>
        /// <remarks>
        /// Drawn by switching a small camera on for one frame per look rather than calling <c>Camera.Render</c>, which
        /// URP does not support; the texture then keeps the picture, so each look is drawn once per session.
        /// </remarks>
        public Texture Thumbnail(CharacterCatalog.Character character, int variantIndex, RuntimeAnimatorController controller)
        {
            if (character?.VariantAt(variantIndex)?.Prefab == null) return null;

            EnsureStage();
            if (m_thumbnails.TryGetValue((character, variantIndex), out var existing)) return existing;

            var target = new RenderTexture(m_thumbnailResolution, m_thumbnailResolution, 24, RenderTextureFormat.ARGB32)
            {
                name = $"CharacterThumbnail {character.Id}:{variantIndex}",
                antiAliasing = 4
            };
            target.Create();

            // Cleared to transparent, so the button shows nothing rather than leftover memory until it is drawn.
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = previous;

            m_thumbnails[(character, variantIndex)] = target;
            m_thumbnailJobs.Enqueue(new ThumbnailJob(character, variantIndex, controller, target));
            return target;
        }

        /// <summary>Finishes the last thumbnail, which rendered last frame, and sets up the next one.</summary>
        private void DrawThumbnails()
        {
            if (m_thumbnailBody != null)
            {
                // Deactivated first: Destroy waits for the end of the frame, and the next body stands on the same spot.
                m_thumbnailBody.SetActive(false);
                Destroy(m_thumbnailBody);
                m_thumbnailBody = null;
            }

            m_thumbnailCamera.enabled = false;

            while (m_thumbnailJobs.Count > 0)
            {
                var job = m_thumbnailJobs.Dequeue();
                if (job.Target == null) continue;

                var variant = job.Character.VariantAt(job.Variant);
                m_thumbnailBody = CharacterBodies.Create(variant.Prefab, m_thumbnailSpot, job.Character.HumanoidAvatar,
                    job.Controller, job.Character.BodyScale);
                if (m_thumbnailBody == null) continue;

                // Poses the body in the controller's first state (idle) now, instead of the bind pose until it next updates.
                var animator = m_thumbnailBody.GetComponent<Animator>();
                if (animator != null) animator.Update(0f);

                var height = BodyHeight(m_thumbnailBody, m_thumbnailSpot.position.y);
                var span = height * ThumbnailSpan;
                var distance = span * 0.5f / Mathf.Tan(m_fieldOfView * 0.5f * Mathf.Deg2Rad);

                m_thumbnailCamera.transform.localPosition = ThumbnailSpot + new Vector3(0f, height * ThumbnailCentre, distance);
                m_thumbnailCamera.targetTexture = job.Target;
                m_thumbnailCamera.enabled = true;
                return;
            }
        }

        /// <summary>Turns the body by a drag, and pauses the idle turn for a moment.</summary>
        public void Turn(float degrees)
        {
            m_yaw += degrees;
            m_lastDragTime = Time.unscaledTime;
        }

        private void Update()
        {
            if (m_thumbnailCamera != null) DrawThumbnails();

            if (m_camera == null || !m_camera.enabled) return;

            if (Time.unscaledTime - m_lastDragTime > m_idleTurnDelay) m_yaw += m_idleTurnSpeed * Time.unscaledDeltaTime;

            m_turntable.localRotation = Quaternion.Euler(0f, m_yaw, 0f);
        }

        private void OnDestroy()
        {
            if (m_thumbnailCamera != null) m_thumbnailCamera.targetTexture = null;
            foreach (var thumbnail in m_thumbnails.Values)
            {
                if (thumbnail == null) continue;
                thumbnail.Release();
                Destroy(thumbnail);
            }

            m_thumbnails.Clear();
            m_thumbnailJobs.Clear();

            if (m_texture == null) return;

            if (m_camera != null) m_camera.targetTexture = null;
            m_texture.Release();
            Destroy(m_texture);
        }

        private void EnsureStage()
        {
            if (m_camera != null) return;

            m_texture = new RenderTexture(m_resolution, m_resolution, 24, RenderTextureFormat.ARGB32)
            {
                name = "CharacterPreview",
                antiAliasing = 4
            };

            m_turntable = new GameObject("Turntable").transform;
            m_turntable.SetParent(transform, false);

            var cameraObject = new GameObject("PreviewCamera");
            cameraObject.transform.SetParent(transform, false);
            m_camera = cameraObject.AddComponent<Camera>();
            m_camera.targetTexture = m_texture;
            m_camera.fieldOfView = m_fieldOfView;
            m_camera.nearClipPlane = 0.05f;
            m_camera.farClipPlane = 30f;

            // Transparent, so the screen's own panel colour shows behind the body.
            m_camera.clearFlags = CameraClearFlags.SolidColor;
            m_camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            m_camera.enabled = false;

            m_thumbnailSpot = new GameObject("ThumbnailSpot").transform;
            m_thumbnailSpot.SetParent(transform, false);
            m_thumbnailSpot.localPosition = ThumbnailSpot;

            var thumbnailObject = new GameObject("ThumbnailCamera");
            thumbnailObject.transform.SetParent(transform, false);
            thumbnailObject.transform.localRotation = Quaternion.LookRotation(Vector3.back);
            m_thumbnailCamera = thumbnailObject.AddComponent<Camera>();
            m_thumbnailCamera.fieldOfView = m_fieldOfView;
            m_thumbnailCamera.nearClipPlane = 0.05f;
            m_thumbnailCamera.farClipPlane = 10f;
            m_thumbnailCamera.clearFlags = CameraClearFlags.SolidColor;
            m_thumbnailCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            m_thumbnailCamera.enabled = false;

            if (m_moodEnabled)
            {
                // An opaque night sky instead of the see-through panel: the backdrop is the background now.
                m_camera.backgroundColor = m_nightColour;

                // Moon high on the camera's side, so the face is lit and the shadow falls back across the ground; the rim
                // keeps the silhouette apart from the dark.
                m_moonLight = AddLight("MoonLight", new Vector3(50f, 160f, 0f), m_moonLightColour, m_moonLightIntensity);
                m_moonLight.shadows = LightShadows.Soft;
                AddLight("RimLight", new Vector3(15f, -35f, 0f), m_rimLightColour, 0.6f);

                BuildBackdrop();
            }
            else
            {
                AddLight("KeyLight", new Vector3(35f, 150f, 0f), m_keyLightColour, 1.4f);
                AddLight("RimLight", new Vector3(20f, -30f, 0f), m_rimLightColour, 0.9f);
            }

            Frame();
        }

        private Light AddLight(string lightName, Vector3 angles, Color colour, float intensity)
        {
            var lightObject = new GameObject(lightName);
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(angles);

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = colour;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            return light;
        }

        /// <summary>How tall a body stands above the floor it was placed on, from its renderers.</summary>
        private static float BodyHeight(GameObject body, float floorY)
        {
            if (body == null) return FallbackHeight;

            var renderers = body.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return FallbackHeight;

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return Mathf.Max(0.5f, bounds.max.y - floorY);
        }

        /// <summary>Places the camera so the whole body fills the frame, whatever its height.</summary>
        private void Frame()
        {
            if (m_camera == null) return;

            var height = BodyHeight(m_body, transform.position.y);

            var framed = height * (1f + m_framePadding * 2f);
            var distance = framed * 0.5f / Mathf.Tan(m_fieldOfView * 0.5f * Mathf.Deg2Rad);

            m_camera.transform.localPosition = new Vector3(0f, height * 0.5f, distance);
            m_camera.transform.localRotation = Quaternion.LookRotation(Vector3.back);
        }
    }
}
