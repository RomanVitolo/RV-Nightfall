using System;
using UnityEngine;
using UHFPS.Tools;
using UHFPS.Rendering;
using static UHFPS.Runtime.GameManager;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public class PlayerHealth : BaseHealthEntity
    {
        /// <summary>
        /// Event triggered when the player's health changes, providing the old and new health values.
        /// </summary>
        public event Action<int, int> OnPlayerHealthChange;
        
        /// <summary>
        /// Event triggered when the player's health reaches zero, indicating that the player has died.
        /// </summary>
        public event Action OnPlayerDied;
        
        public uint MaxHealth = 100;
        public uint StartHealth = 100;

        public bool UseHearthbeat;
        public float LowHealthPulse = 5f;
        public float HealthFadeTime = 0.1f;

        public uint MinHealthFade = 20;
        public float BloodDuration = 2f;
        public float BloodFadeInSpeed = 2f;
        public float BloodFadeOutSpeed = 1f;
        
        public bool CloseEyesOnDie = true;
        public float CloseEyesTime = 2f;
        public float CloseEyesSpeed = 2f;

        public bool EnableFallDamage;
        public MinMax FallDistance = new(5, 10);
        public MinMaxInt FallDamage = new(0, 15);

        public bool UseDamageSounds;
        public AudioClip[] DamageSounds;
        [Range(0f, 1f)]
        public float DamageVolume = 1f;

        public bool IsInvisibleToEnemies;
        public bool IsInvisibleToAllies;

        private PlayerStateMachine player;
        private GameManager gameManager;
        private EyeBlink eyeBlink;

        private float targetHealth;
        private float healthVelocity;

        private float bloodWeight;
        private float targetBlood;
        private float bloodTime;
        private float eyesTime;

        private int lastDamageSound;
        private Vector3 lastPosition;

        private bool wasInAir;
        private bool lastPosLoaded;

        /// <summary>
        /// MULTIPLAYER PATCH: set by the bridge's PlayerHealthSync. The server owns player health, so damage and
        /// healing detected here (medkits, damage zones, falls) is sent to it instead of changing this copy's
        /// health. The server's answer comes back through <see cref="SetAuthoritativeHealth"/>.
        /// </summary>
        public IPlayerHealthAuthority Authority { get; set; }

        private void Awake()
        {
            gameManager = LocalPlayerContext.ResolveGameManager(this);
            // MULTIPLAYER PATCH: the health volume is a scene reference the bridge assigns at spawn, after Awake, so
            // the eye blink is looked up on first use instead. Dereferencing it here threw and skipped InitHealth,
            // leaving MaxEntityHealth at 0 — which clamped every heal to zero and greyed out medkits.
            player = GetComponent<PlayerStateMachine>();

            if (!SaveGameManager.GameWillLoad || !SaveGameManager.GameStateExist)
                InitHealth();
        }

        private void Update()
        {
            if (!lastPosLoaded)
            {
                lastPosition = transform.position;
                lastPosLoaded = true;
            }

            if (gameManager.HealthBar != null)
            {
                float healthValue = gameManager.HealthBar.value;
                healthValue = Mathf.SmoothDamp(healthValue, targetHealth, ref healthVelocity, HealthFadeTime);
                gameManager.HealthBar.value = healthValue;
            }

            if (EntityHealth > MinHealthFade)
            {
                if (bloodTime > 0f) bloodTime -= Time.deltaTime;
                else
                {
                    targetBlood = 0f;
                    bloodTime = 0f;
                }
            }

            bloodWeight = Mathf.MoveTowards(bloodWeight, targetBlood, Time.deltaTime * (bloodTime > 0 ? BloodFadeInSpeed : BloodFadeOutSpeed));

            // MULTIPLAYER PATCH: null until the bridge assigns the scene's volume at spawn; see Awake.
            if (gameManager.HealthPPVolume != null)
            {
                gameManager.HealthPPVolume.weight = bloodWeight;
                if (eyeBlink == null) gameManager.HealthPPVolume.profile.TryGet(out eyeBlink);
            }

            if (CloseEyesOnDie && IsDead && eyeBlink != null)
            {
                if (eyesTime < CloseEyesTime)
                {
                    eyesTime += Time.deltaTime;
                }
                else
                {
                    float blinkValue = eyeBlink.Blink.value;
                    eyeBlink.Blink.value = Mathf.MoveTowards(blinkValue, 1f, Time.deltaTime * CloseEyesSpeed);
                }
            }

            if (!IsDead && EnableFallDamage)
            {
                if (player.StateGrounded)
                {
                    if(!wasInAir) lastPosition = transform.position;
                    else
                    {
                        Vector3 dropPosition = transform.position;
                        float fallDistance = Mathf.Clamp(lastPosition.y - dropPosition.y, 0, Mathf.Infinity);
                        float fallModifier = Mathf.InverseLerp(FallDistance.RealMin, FallDistance.RealMax, fallDistance);
                        float fallDamage = 0f;

                        if (fallModifier > 0f) fallDamage = Mathf.Lerp(FallDamage.RealMin, FallDamage.RealMax, fallModifier);
                        if (fallDamage > 1f) ApplyDamage(Mathf.RoundToInt(fallDamage));
                        wasInAir = false;
                    }
                }
                else if(!wasInAir)
                {
                    wasInAir = true;
                }
            }
        }

        public void InitHealth()
        {
            InitializeHealth((int)StartHealth, (int)MaxHealth);

            if (StartHealth <= MinHealthFade)
            {
                targetBlood = 1f;
                bloodTime = BloodDuration;
            }
        }

        public override void OnHealthChanged(int oldHealth, int newHealth)
        {
            gameManager.HealthPercent.text = newHealth.ToString();
            targetHealth = (float)newHealth / MaxHealth;

            if (UseHearthbeat)
            {
                Material hearthbeatMat = gameManager.Hearthbeat.material;

                if (newHealth <= 0)
                {
                    hearthbeatMat.EnableKeyword("ZERO_PULSE");
                }
                else
                {
                    float pulse = GameTools.Remap(0f, MaxHealth, LowHealthPulse, 1f, newHealth);
                    hearthbeatMat.SetFloat("_PulseMultiplier", pulse);
                    hearthbeatMat.DisableKeyword("ZERO_PULSE");
                }
            }
            
            OnPlayerHealthChange?.Invoke(oldHealth, newHealth);
        }

        public override void ApplyDamage(int damage, Transform sender = null)
        {
            if (IsDead) return;

            // MULTIPLAYER PATCH: see Authority. The feedback plays when the server's value arrives.
            if (Authority != null)
            {
                Authority.RequestDamage(damage);
                return;
            }

            base.ApplyDamage(damage, sender);
            PlayDamageFeedback();
        }

        /// <summary>
        /// MULTIPLAYER PATCH: the base class kills by setting health to zero directly, bypassing ApplyDamage and
        /// with it the Authority — so a kill needs routing on its own.
        /// </summary>
        public override void ApplyDamageMax(Transform sender = null)
        {
            if (IsDead) return;

            if (Authority != null)
            {
                Authority.RequestDamage((int)MaxHealth);
                return;
            }

            base.ApplyDamageMax(sender);
        }

        public override void ApplyHeal(int healAmount)
        {
            // MULTIPLAYER PATCH: see Authority.
            if (Authority != null)
            {
                if (!IsDead) Authority.RequestHeal(healAmount);
                return;
            }

            base.ApplyHeal(healAmount);
            PlayHealFeedback();
        }

        /// <summary>MULTIPLAYER PATCH: like ApplyDamageMax, the base class bypasses ApplyHeal.</summary>
        public override void ApplyHealMax()
        {
            if (Authority != null)
            {
                if (!IsDead) Authority.RequestHeal((int)MaxHealth);
                return;
            }

            base.ApplyHealMax();
        }

        /// <summary>
        /// MULTIPLAYER PATCH: applies the health the server decided, with the feedback the local call would have
        /// played. Setting EntityHealth raises the health-changed and death callbacks, so the HUD and death flow
        /// run unchanged.
        /// </summary>
        /// <param name="health">The server's value.</param>
        /// <param name="playFeedback"><c>false</c> for an initial value, which is not a hit or a heal.</param>
        public void SetAuthoritativeHealth(int health, bool playFeedback)
        {
            int previous = EntityHealth;
            EntityHealth = health;

            if (!playFeedback) return;

            if (health < previous) PlayDamageFeedback();
            else if (health > previous) PlayHealFeedback();
        }

        private void PlayDamageFeedback()
        {
            if (UseDamageSounds && DamageSounds.Length > 0)
            {
                int damageSound = GameTools.RandomUnique(0, DamageSounds.Length, lastDamageSound);
                GameTools.PlayOneShot2D(transform.position, DamageSounds[damageSound], DamageVolume, "DamageSound");
                lastDamageSound = damageSound;
            }

            targetBlood = 1f;
            bloodTime = BloodDuration;
        }

        private void PlayHealFeedback()
        {
            if (EntityHealth > MinHealthFade)
                bloodTime = BloodDuration;
        }

        public override void OnHealthZero()
        {
            gameManager.ShowPanel(PanelType.DeadPanel);
            gameManager.PlayerPresence.FreezePlayer(true, true);
            gameManager.PlayerPresence.PlayerManager.PlayerItems.DeactivateCurrentItem();
            targetBlood = 1f;
            
            OnPlayerDied?.Invoke();
        }
    }
}