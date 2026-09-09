using System;
using UnityEngine;

namespace UHFPS.Runtime
{
    [Serializable]
    public class InterfaceReference<T> : ISerializationCallbackReceiver where T : class
    {
        [SerializeField]
        private UnityEngine.Object _object;

        /// <summary>
        /// Access the interface.
        /// </summary>
        public T Value
        {
            get
            {
                if (_object == null)
                    return null;

                // If it's already the right type, just cast
                if (_object is T t)
                    return t;

                // Fallback: if someone somehow stored a GameObject, try to get the component
                if (_object is GameObject go)
                    return go.GetComponent(typeof(T)) as T;

                return null;
            }
        }

        /// <summary>
        /// True if the reference is not null and actually implements T.
        /// </summary>
        public bool HasValue => Value != null;
        
        public static implicit operator T(InterfaceReference<T> reference)
        {
            return reference?.Value;
        }

        public void OnBeforeSerialize()
        {
            if (_object == null)
                return;

            if (!IsValidObject(_object))
                _object = null;
        }

        public void OnAfterDeserialize()
        {
            
        }

        private static bool IsValidObject(UnityEngine.Object obj)
        {
            if (obj == null)
                return true;

            // Component or ScriptableObject that implements the interface
            if (obj is T) return true;

            // GameObject that has a component implementing the interface
            if (obj is GameObject go)
            {
                var comp = go.GetComponent(typeof(T));
                return comp != null;
            }

            return false;
        }
    }
}