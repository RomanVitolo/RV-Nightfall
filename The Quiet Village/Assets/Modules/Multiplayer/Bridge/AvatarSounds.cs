using UHFPS.Runtime;
using UHFPS.Scriptable;
using UHFPS.Tools;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Gives another player's body a voice: footsteps, landings, and the sounds of what they are holding.
    /// </summary>
    /// <remarks>
    /// Every AudioSource on a remote copy is switched off, because each belongs to a first-person player standing at
    /// their own machine's ear. This adds one of its own, at the body, so a teammate walking past or firing in the
    /// next room is heard where they actually are.
    ///
    /// UHFPS's own footstep system cannot run here: it is driven by a state machine and a CharacterController only
    /// the owner runs. Its settings are still on this copy, though, so steps keep their timing, volume and surfaces:
    /// read from that component, timed from replicated speed, and matched to whatever is underfoot.
    ///
    /// Remote copies only: <see cref="HeroPlayerNetworkSetup"/> adds it to bodies this client does not own.
    /// </remarks>
    public class AvatarSounds : MonoBehaviour
    {
        // Between the prefab's crouch (1 m/s), walk (2.5) and run (6). Crouching is not replicated, so a player
        // moving at crouching pace is given the quieter step, which is what they are almost certainly doing.
        private const float CrouchSpeed = 0.3f;
        private const float WalkSpeed = 1.7f;
        private const float RunSpeed = 4.2f;

        private const float RaycastUp = 0.3f;
        private const float RaycastDown = 1.5f;

        private PlayerLocomotionSync m_locomotion;
        private PlayerActionSync m_actions;
        private FootstepsSystem m_footsteps;
        private AudioSource m_source;

        private float m_stepTimer;
        private float m_airTime;
        private int m_lastStep;
        private int m_lastLandStep;

        /// <summary>Starts voicing this body. For a copy this client does not own.</summary>
        public void Bind(PlayerLocomotionSync locomotion, PlayerActionSync actions, FootstepsSystem footsteps)
        {
            m_locomotion = locomotion;
            m_actions = actions;
            m_footsteps = footsteps;

            m_source = gameObject.AddComponent<AudioSource>();
            m_source.playOnAwake = false;
            m_source.spatialBlend = 1f;
            m_source.rolloffMode = AudioRolloffMode.Linear;
            m_source.minDistance = 1.5f;
            m_source.maxDistance = 30f;

            if (m_actions == null) return;

            m_actions.ItemActionPlayed += HandleItemAction;
            m_actions.ItemSoundPlayed += PlaySound;
            m_actions.EquippedItemChanged += HandleEquippedItemChanged;
            m_actions.HeldLightToggled += HandleHeldLightToggled;
        }

        private void OnDestroy()
        {
            if (m_actions == null) return;

            m_actions.ItemActionPlayed -= HandleItemAction;
            m_actions.ItemSoundPlayed -= PlaySound;
            m_actions.EquippedItemChanged -= HandleEquippedItemChanged;
            m_actions.HeldLightToggled -= HandleHeldLightToggled;
        }

        private void Update()
        {
            if (m_locomotion == null || m_footsteps == null || m_source == null) return;

            if (!m_locomotion.IsGrounded)
            {
                m_airTime += Time.deltaTime;
                return;
            }

            if (m_airTime > 0f)
            {
                var airTime = m_airTime;
                m_airTime = 0f;

                // A landing replaces the step that would have been taken on arrival.
                if (airTime >= m_footsteps.LandStepTime && m_footsteps.EnableLandSteps)
                {
                    PlayLandStep();
                    return;
                }
            }

            TickFootsteps();
        }

        private void TickFootsteps()
        {
            var state = StateForSpeed(m_locomotion.PlanarSpeed);
            if (state == FootstepsSystem.StepState.None)
            {
                // The next step lands as they set off again, not half a stride later.
                m_stepTimer = 0f;
                return;
            }

            m_stepTimer -= Time.deltaTime;
            if (m_stepTimer > 0f) return;

            m_stepTimer = StepInterval(state);
            PlayStep(state);
        }

        private static FootstepsSystem.StepState StateForSpeed(float speed)
        {
            if (speed >= RunSpeed) return FootstepsSystem.StepState.Run;
            if (speed >= WalkSpeed) return FootstepsSystem.StepState.Walk;
            if (speed >= CrouchSpeed) return FootstepsSystem.StepState.Crouch;

            return FootstepsSystem.StepState.None;
        }

        private float StepInterval(FootstepsSystem.StepState state) => state switch
        {
            FootstepsSystem.StepState.Run => m_footsteps.RunStepTime,
            FootstepsSystem.StepState.Crouch => m_footsteps.CrouchStepTime,
            _ => m_footsteps.WalkStepTime
        };

        private float VolumeScale(FootstepsSystem.StepState state) => state switch
        {
            FootstepsSystem.StepState.Run => m_footsteps.RunningVolume,
            FootstepsSystem.StepState.Crouch => m_footsteps.CrouchingVolume,
            _ => m_footsteps.WalkingVolume
        };

        private bool StepEnabled(FootstepsSystem.StepState state) => state switch
        {
            FootstepsSystem.StepState.Run => m_footsteps.EnableRunSteps,
            FootstepsSystem.StepState.Crouch => m_footsteps.EnableCrouchSteps,
            _ => m_footsteps.EnableWalkSteps
        };

        private void PlayStep(FootstepsSystem.StepState state)
        {
            if (!StepEnabled(state)) return;

            var surface = SurfaceUnderfoot();
            if (surface == null || surface.SurfaceFootsteps.Count <= 0) return;

            m_lastStep = GameTools.RandomUnique(0, surface.SurfaceFootsteps.Count, m_lastStep);
            m_source.PlayOneShot(surface.SurfaceFootsteps[m_lastStep], surface.FootstepsVolume * VolumeScale(state));
        }

        private void PlayLandStep()
        {
            var surface = SurfaceUnderfoot();
            if (surface == null || surface.SurfaceLandSteps.Count <= 0) return;

            m_lastLandStep = GameTools.RandomUnique(0, surface.SurfaceLandSteps.Count, m_lastLandStep);
            m_source.PlayOneShot(surface.SurfaceLandSteps[m_lastLandStep],
                surface.LandStepsVolume * m_footsteps.LandVolume);
        }

        /// <summary>
        /// What this body stands on. UHFPS learns that from collisions the owner's controller reports; a remote copy
        /// has no controller running, so it looks down instead.
        /// </summary>
        private SurfaceDefinition SurfaceUnderfoot()
        {
            if (m_footsteps.SurfaceDefinitionSet == null) return null;

            var origin = transform.position + Vector3.up * RaycastUp;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, RaycastDown, m_footsteps.FootstepsMask,
                    QueryTriggerInteraction.Ignore))
                return null;

            return m_footsteps.SurfaceDefinitionSet.GetSurface(hit.collider.gameObject, hit.point,
                m_footsteps.SurfaceDetection);
        }

        /// <summary>
        /// Plays what the held item would have sounded like. The item's own AudioSource belongs to its owner's
        /// first-person view, so the clip is read from its settings and played here, at the body.
        /// </summary>
        private void HandleItemAction(PlayerItemBehaviour.ItemAction action)
        {
            if (m_source == null || m_actions == null) return;

            PlaySound(SoundFor(action, m_actions.EquippedItem));
        }

        /// <summary>
        /// Shooting and swinging are played from the item's own settings; reloading is not, because UHFPS has no
        /// reload clip. Its sound comes from the reload animation instead, and arrives through ItemSoundPlayed.
        /// </summary>
        private static SoundClip SoundFor(PlayerItemBehaviour.ItemAction action, PlayerItemBehaviour item)
        {
            if (action == PlayerItemBehaviour.ItemAction.Shoot && item is GunItem gun) return gun.gunSounds?.ShootSound;
            if (action != PlayerItemBehaviour.ItemAction.Attack) return null;

            if (item is AxeItem axe) return axe.AxeSlash;
            if (item is KnifeItem knife) return knife.KnifeSlash;

            return null;
        }

        /// <summary>Puts one item away and takes the next out, audibly.</summary>
        private void HandleEquippedItemChanged(PlayerItemBehaviour previous, PlayerItemBehaviour current)
        {
            PlaySound(HideSoundFor(previous));
            PlaySound(DrawSoundFor(current));
        }

        /// <summary>The click of a flashlight, played from the item's own settings as its owner heard it.</summary>
        private void HandleHeldLightToggled(bool on)
        {
            if (m_actions.EquippedItem is not FlashlightItem flashlight) return;

            PlaySound(on ? flashlight.FlashlightClickOn : flashlight.FlashlightClickOff);
        }

        private static SoundClip DrawSoundFor(PlayerItemBehaviour item) => item switch
        {
            GunItem gun => gun.gunSounds?.DrawSound,
            AxeItem axe => axe.AxeDraw,
            KnifeItem knife => knife.KnifeDraw,
            LanternItem lantern => lantern.LanternDraw,
            _ => null
        };

        private static SoundClip HideSoundFor(PlayerItemBehaviour item) => item switch
        {
            GunItem gun => gun.gunSounds?.HideSound,
            AxeItem axe => axe.AxeHide,
            KnifeItem knife => knife.KnifeHide,
            LanternItem lantern => lantern.LanternHide,
            _ => null
        };

        private void PlaySound(SoundClip sound)
        {
            if (m_source == null || sound == null || sound.audioClip == null) return;

            m_source.PlayOneShot(sound.audioClip, sound.volume);
        }
    }
}
