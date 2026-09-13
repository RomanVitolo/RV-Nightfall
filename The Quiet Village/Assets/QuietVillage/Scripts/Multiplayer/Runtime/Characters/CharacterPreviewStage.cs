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

        private const float FallbackHeight = 1.8f;

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
            m_camera.enabled = showing;
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

        /// <summary>Turns the body by a drag, and pauses the idle turn for a moment.</summary>
        public void Turn(float degrees)
        {
            m_yaw += degrees;
            m_lastDragTime = Time.unscaledTime;
        }

        private void Update()
        {
            if (m_camera == null || !m_camera.enabled) return;

            if (Time.unscaledTime - m_lastDragTime > m_idleTurnDelay) m_yaw += m_idleTurnSpeed * Time.unscaledDeltaTime;

            m_turntable.localRotation = Quaternion.Euler(0f, m_yaw, 0f);
        }

        private void OnDestroy()
        {
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

            AddLight("KeyLight", new Vector3(35f, 150f, 0f), m_keyLightColour, 1.4f);
            AddLight("RimLight", new Vector3(20f, -30f, 0f), m_rimLightColour, 0.9f);

            Frame();
        }

        private void AddLight(string lightName, Vector3 angles, Color colour, float intensity)
        {
            var lightObject = new GameObject(lightName);
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.localRotation = Quaternion.Euler(angles);

            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = colour;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
        }

        /// <summary>Places the camera so the whole body fills the frame, whatever its height.</summary>
        private void Frame()
        {
            if (m_camera == null) return;

            var height = FallbackHeight;
            if (m_body != null)
            {
                var renderers = m_body.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    var bounds = renderers[0].bounds;
                    for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                    height = Mathf.Max(0.5f, bounds.max.y - transform.position.y);
                }
            }

            var framed = height * (1f + m_framePadding * 2f);
            var distance = framed * 0.5f / Mathf.Tan(m_fieldOfView * 0.5f * Mathf.Deg2Rad);

            m_camera.transform.localPosition = new Vector3(0f, height * 0.5f, distance);
            m_camera.transform.localRotation = Quaternion.LookRotation(Vector3.back);
        }
    }
}
