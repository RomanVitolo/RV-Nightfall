using System;
using UnityEngine;

namespace UHFPS.Runtime
{
    [Serializable]
    public sealed class HealthAttribute : PlayerAttribute
    {
        public int Amount;

        public HealthAttribute() { }
        public HealthAttribute(int amount) => Amount = amount;

        public override string Name => base.Name + "Health Attribute";
        public override int Priority => 0;

        public override void Apply(PlayerManager player)
        {
            player.PlayerHealth.ApplyHeal(Amount);
        }
    }

    [Serializable]
    public sealed class RestoreHealthAttribute : PlayerAttribute
    {
        public RestoreHealthAttribute() { }

        public override string Name => base.Name + "Restore Health Attribute";
        public override int Priority => 0;

        public override void Apply(PlayerManager player)
        {
            player.PlayerHealth.ApplyHealMax();
        }
    }

    [Serializable]
    public sealed class DamageAttribute : PlayerAttribute
    {
        public int Amount;

        public DamageAttribute() { }
        public DamageAttribute(int amount) => Amount = amount;

        public override string Name => base.Name + "Damage Attribute";
        public override int Priority => 1;

        public override void Apply(PlayerManager player)
        {
            player.PlayerHealth.ApplyDamage(Amount);
        }
    }

    [Serializable]
    public sealed class DeathAttribute : PlayerAttribute
    {
        public override string Name => base.Name + "Death Attribute";
        public override int Priority => 1;

        public override void Apply(PlayerManager player)
        {
            player.PlayerHealth.ApplyDamageMax();
        }
    }

    [Serializable]
    public sealed class StaminaAttribute : PlayerAttribute
    {
        public enum StaminaType
        {
            Drain,
            Restore
        }

        public StaminaType Type;
        [Range(0f, 1f)] public float Amount = 0.5f;

        public StaminaAttribute() { }
        public StaminaAttribute(float amount) => Amount = amount;

        public override string Name => base.Name + "Stamina Attribute";
        public override int Priority => 0;

        public override void Apply(PlayerManager player)
        {
            float value = Type == StaminaType.Drain ? -Amount : Amount;
            player.PlayerStateMachine.AddStamina(value);
        }
    }
}
