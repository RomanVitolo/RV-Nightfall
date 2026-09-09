using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UHFPS.Runtime
{
    public interface ISerializedReferenceListItem
    {
        string Name { get; }
    }

    /// <summary>
    /// Provides a serializable list of reference-type items with type-based retrieval and management functionality.
    /// Draws a custom inspector in the Unity Editor.
    /// </summary>
    [Serializable]
    public abstract class SerializedReferenceList<TItem> where TItem : class, ISerializedReferenceListItem
    {
        [SerializeReference]
        public List<TItem> Items = new();

        /// <summary>
        /// Get the first item of type T, or null if none found.
        /// </summary>
        public T Get<T>() where T : class, TItem
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i] is T t) 
                    return t;
            }
            return null;
        }

        /// <summary>
        /// Remove all items of type T. Returns true if any were removed.
        /// </summary>
        public bool Remove<T>() where T : class, TItem
        {
            return Items.RemoveAll(a => a is T) > 0;
        }

        /// <summary>
        /// Clear all items.
        /// </summary>
        public void Clear() => Items.Clear();

        /// <summary>
        /// Try to get the first item of type T. Returns true if found, false otherwise.
        /// </summary>
        public bool TryGet<T>(out T item) where T : class, TItem
        {
            item = Get<T>();
            return item != null;
        }

        /// <summary>
        /// Get the first item of type T, or create and add a new one if none found.
        /// </summary>
        public T GetOrAdd<T>() where T : class, TItem, new()
        {
            if (TryGet<T>(out var found))
                return found;

            var created = new T();
            Items.Add(created);
            return created;
        }

        /// <summary>
        /// Serialize the list of items to a JSON array.
        /// </summary>
        public JArray ToJson()
        {
            JArray array = new();
            foreach (var item in Items)
            {
                if (item == null)
                    continue;

                Type type = item.GetType();
                array.Add(new JObject()
                {
                    ["type"] = type.AssemblyQualifiedName,
                    ["data"] = JObject.FromObject(item)
                });
            }

            return array;
        }

        /// <summary>
        /// Deserialize a JSON array to populate the list of items.
        /// </summary>
        public void FromJson(JArray json)
        {
            Items.Clear();

            foreach (var token in json)
            {
                if (token is not JObject entry)
                    continue;

                string typeName = entry["type"]?.ToString();
                JObject itemJson = entry["data"] as JObject;

                if (string.IsNullOrEmpty(typeName) || itemJson == null)
                {
                    Debug.LogError("Invalid serialized item entry.");
                    continue;
                }

                Type itemType = Type.GetType(typeName);

                if (itemType == null || !typeof(TItem).IsAssignableFrom(itemType))
                {
                    Debug.LogError($"Could not find type '{typeName}' or it does not implement '{typeof(TItem).Name}'");
                    continue;
                }

                TItem item = (TItem)itemJson.ToObject(itemType);
                if (item != null) Items.Add(item);
            }
        }
    }
}
