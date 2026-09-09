using System;
using Newtonsoft.Json;

namespace UHFPS.Runtime
{
    [Serializable]
    public sealed class IntegerValueAttribute : ItemAttribute
    {
        [JsonProperty("key")]
        public string Key;
        [JsonProperty("value")]
        public int Value;
        
        public IntegerValueAttribute()
        {
        }
        
        public IntegerValueAttribute(string key, int value)
        {
            Key = key;
            Value = value;
        }
        
        public override string Name => base.Name + "Integer Attribute";
    }

    [Serializable]
    public sealed class FloatValueAttribute : ItemAttribute
    {
        [JsonProperty("key")]
        public string Key;
        [JsonProperty("value")]
        public float Value;

        public FloatValueAttribute()
        {
        }

        public FloatValueAttribute(string key, float value)
        {
            Key = key;
            Value = value;
        }

        public override string Name => base.Name + "Float Attribute";
    }

    [Serializable]
    public sealed class BooleanValueAttribute : ItemAttribute
    {
        [JsonProperty("key")]
        public string Key;
        [JsonProperty("value")]
        public bool Value;

        public BooleanValueAttribute()
        {
        }

        public BooleanValueAttribute(string key, bool value)
        {
            Key = key;
            Value = value;
        }

        public override string Name => base.Name + "Boolean Attribute";
    }

    [Serializable]
    public sealed class StringValueAttribute : ItemAttribute
    {
        [JsonProperty("key")]
        public string Key;
        [JsonProperty("value")]
        public string Value;

        public StringValueAttribute()
        {
        }

        public StringValueAttribute(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public override string Name => base.Name + "String Attribute";
    }
}