using System;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UHFPS.Runtime
{
    /// <summary>
    /// Stores custom item data either as raw JSON or structured item attributes.
    /// </summary>
    [Serializable]
    public sealed class ItemCustomData
    {
        public const string ItemsKey = "items";
        public const string DataKey = "data";
        public const string KeyKey = "key";
        public const string ValueKey = "value";

        public enum DataType
        {
            /// <summary>Custom data is stored directly in JsonData.</summary>
            Json = 0,

            /// <summary>Custom data is stored in ItemAttributes.</summary>
            Attributes = 1
        }

        // --------------------------------------------------------------------
        // Inspector Fields
        // --------------------------------------------------------------------

        [Tooltip("Determines how the custom data is stored and accessed.")]
        public DataType Type = DataType.Json;

        [TextArea(3, 10), Tooltip("Raw JSON data used when Type is set to Json.")]
        public string JsonData;

        [Tooltip("Structured item attributes used when Type is set to Attributes.")]
        public ItemAttributes ItemAttributes = new();

        // --------------------------------------------------------------------
        // Public JSON Access
        // --------------------------------------------------------------------

        /// <summary>
        /// Returns this item custom data as a JObject.
        /// </summary>
        public JObject GetJson()
        {
            return Type == DataType.Attributes
                ? CreateAttributesJson()
                : ParseJsonData();
        }

        /// <summary>
        /// Tries to get a value by JSON key or attribute key.
        /// Returns true if found, false otherwise.
        /// </summary>
        public bool TryGetValue<T>(string key, out T value)
        {
            if (string.IsNullOrEmpty(key))
            {
                value = default;
                return false;
            }
            
            if (Type == DataType.Attributes)
            {
                if (TryGetAttributeData(key, out JObject attributeData))
                {
                    var valueToken = attributeData[ValueKey];
                    value = valueToken.ToObject<T>();
                    return true;
                }
            }
            
            JObject json = GetJson();
            if (json.TryGetValue(key, out JToken token))
            {
                value = token.ToObject<T>();
                return true;
            }
            
            value = default;
            return false;
        }
        
        /// <summary>
        /// Gets a value by JSON key or attribute key.
        /// </summary>
        public T GetValue<T>(string key)
        {
            if (string.IsNullOrEmpty(key))
                return default;

            if (Type == DataType.Attributes)
            {
                if (TryGetAttributeData(key, out JObject attributeData))
                {
                    var valueToken = attributeData[ValueKey];
                    return valueToken.ToObject<T>();
                }
            }

            JObject json = GetJson();
            return json.TryGetValue(key, out JToken value)
                ? value.ToObject<T>() 
                : default;
        }
        
        /// <summary>
        /// Gets a value by JSON key or attribute key.
        /// </summary>
        public JToken GetValue(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            if (Type == DataType.Attributes)
            {
                if (TryGetAttributeData(key, out JObject attributeData))
                    return attributeData[ValueKey];
            }

            JObject json = GetJson();
            return json.TryGetValue(key, out JToken value) ? value : null;
        }

        /// <summary>
        /// Sets or replaces a JSON value or existing attribute value.
        /// </summary>
        public void SetValue(string key, object value)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (Type == DataType.Attributes)
            {
                JArray attributesArray = ItemAttributes.ToJson();
                if (TryGetAttributeData(attributesArray, key, out JObject attributeData))
                {
                    attributeData[ValueKey] = CreateToken(value);
                    ItemAttributes.FromJson(attributesArray);
                }

                return;
            }

            JObject json = GetJson();
            json[key] = CreateToken(value);
            SaveJson(json);
        }

        /// <summary>
        /// Adds a JSON value if the key does not exist.
        /// </summary>
        public void AddValue(string key, object value)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (Type == DataType.Attributes)
            {
                Debug.LogError("DataType is set to Attributes. Use AddAttribute() to add structured attributes instead of AddValue().");
                return;
            }

            JObject json = GetJson();
            if (json.ContainsKey(key))
                return;

            json.Add(key, CreateToken(value));
            SaveJson(json);
        }

        /// <summary>
        /// Adds an attribute value if the key does not exist.
        /// </summary>
        public void AddAttribute(ItemAttribute attribute)
        {
            if (attribute == null)
                return;

            if (Type == DataType.Json)
            {
                Debug.LogError("DataType is set to Json. Use AddValue() to add key-value pairs.");
                return;
            }

            ItemAttributes.Items.Add(attribute);
        }

        /// <summary>
        /// Removes a JSON value or attribute by key.
        /// </summary>
        public void RemoveValue(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (Type == DataType.Attributes)
            {
                JArray attributesArray = ItemAttributes.ToJson();

                for (int i = attributesArray.Count - 1; i >= 0; i--)
                {
                    JObject attributeJson = attributesArray[i] as JObject;
                    JObject attributeData = attributeJson?[DataKey] as JObject;

                    if (attributeData == null)
                        continue;

                    string attributeKey = attributeData[KeyKey]?.ToString();

                    if (attributeKey != key)
                        continue;

                    attributesArray.RemoveAt(i);
                    ItemAttributes.FromJson(attributesArray);
                    return;
                }

                return;
            }

            JObject json = GetJson();
            if (json.Remove(key))
            {
                SaveJson(json);
            }
        }

        /// <summary>
        /// Updates this custom data from an external JObject.
        /// </summary>
        public void Update(JObject json)
        {
            if (json == null)
            {
                JsonData = "{}";
                return;
            }

            if (Type == DataType.Attributes)
            {
                UpdateAttributes(json);
            }

            SaveJson(json);
        }

        /// <summary>
        /// Returns the current custom data as compact JSON text.
        /// </summary>
        public override string ToString()
        {
            return GetJson().ToString(Formatting.Indented);
        }

        // --------------------------------------------------------------------
        // Attributes Handling
        // --------------------------------------------------------------------

        /// <summary>
        /// Creates a JObject containing the current item attributes.
        /// </summary>
        private JObject CreateAttributesJson()
        {
            return new JObject
            {
                [ItemsKey] = ItemAttributes.ToJson()
            };
        }

        /// <summary>
        /// Updates ItemAttributes from a JObject containing an items array.
        /// </summary>
        private void UpdateAttributes(JObject json)
        {
            if (!json.TryGetValue(ItemsKey, out JToken attributesToken))
                return;

            if (attributesToken is not JArray attributesArray)
                return;

            ItemAttributes.FromJson(attributesArray);
        }

        /// <summary>
        /// Finds attribute data by attribute key.
        /// </summary>
        private bool TryGetAttributeData(string key, out JObject attributeData)
        {
            return TryGetAttributeData(ItemAttributes.ToJson(), key, out attributeData);
        }

        /// <summary>
        /// Finds attribute data in a serialized attributes array.
        /// </summary>
        private bool TryGetAttributeData(JArray attributesArray, string key, out JObject attributeData)
        {
            attributeData = null;
            if (attributesArray == null || string.IsNullOrEmpty(key))
                return false;

            foreach (JToken attributeToken in attributesArray)
            {
                if (attributeToken?[DataKey] is not JObject data)
                    continue;

                string attributeKey = data[KeyKey]?.ToString();
                if (attributeKey != key)
                    continue;

                attributeData = data;
                return true;
            }

            return false;
        }

        // --------------------------------------------------------------------
        // JSON Parsing and Saving
        // --------------------------------------------------------------------

        /// <summary>
        /// Transfers data from another ItemCustomData instance, copying the type, JSON data, and attributes.
        /// </summary>
        public void Transfer(ItemCustomData other)
        {            
            if (other == null)
                return;

            Type = other.Type;
            JsonData = other.JsonData;
            ItemAttributes = new ItemAttributes(other.ItemAttributes);
        }

        /// <summary>
        /// Deserializes a JToken into this ItemCustomData, determining whether to store it as JSON or structured attributes.
        /// </summary>
        public void Deserialize(JToken data)
        {
            if (data is JObject obj)
            {
                if (obj.TryGetValue(ItemsKey, out JToken itemsToken) && itemsToken is JArray itemsArray)
                {
                    ItemAttributes.FromJson(itemsArray);
                    Type = DataType.Attributes;
                }
                else
                {
                    JsonData = obj.ToString(Formatting.Indented);
                    Type = DataType.Json;
                }
            }
            else
            {
                JsonData = data.ToString(Formatting.Indented);
                Type = DataType.Json;
            }
        }
        
        /// <summary>
        /// Parses JsonData into a JObject.
        /// </summary>
        private JObject ParseJsonData()
        {
            if (string.IsNullOrWhiteSpace(JsonData))
                return new JObject();

            try
            {
                JToken token = JToken.Parse(JsonData);

                if (token is JObject obj)
                    return obj;

                if (token is JArray array)
                {
                    return new JObject
                    {
                        [ItemsKey] = array
                    };
                }

                return new JObject
                {
                    [ValueKey] = token
                };
            }
            catch (JsonReaderException)
            {
                Debug.LogError($"Invalid item custom JSON data:\n{JsonData}");
                return new JObject();
            }
        }

        /// <summary>
        /// Saves a JObject into JsonData as compact JSON.
        /// </summary>
        private void SaveJson(JObject json)
        {
            JsonData = json != null
                ? json.ToString(Formatting.Indented)
                : "{}";
        }

        /// <summary>
        /// Converts an object into a JToken.
        /// </summary>
        private static JToken CreateToken(object value)
        {
            return value != null
                ? JToken.FromObject(value)
                : JValue.CreateNull();
        }
    }
}