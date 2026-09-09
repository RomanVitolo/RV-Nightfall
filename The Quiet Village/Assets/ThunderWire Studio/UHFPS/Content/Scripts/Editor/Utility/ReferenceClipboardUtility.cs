using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UHFPS.Editors
{
    /// <summary>
    /// Simple copy/paste for managed reference (SerializeReference) properties.
    /// </summary>
    public static class ReferenceClipboardUtility
    {
        private const string Prefix = "MRCLIP:";

        [Serializable] 
        private class Envelope 
        { 
            public string asm; 
            public string type; 
            public string json; 
            public bool isNull; 
        }

        /// <summary>
        /// True if the clipboard currently holds a managed reference copied by this helper.
        /// </summary>
        public static bool CanPaste()
        {
            var s = EditorGUIUtility.systemCopyBuffer;
            return !string.IsNullOrEmpty(s) && s.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Copy a ManagedReference (SerializeReference) property to the clipboard.
        /// </summary>
        public static void Copy(SerializedProperty prop)
        {
            if (prop == null) throw new ArgumentNullException(nameof(prop));
            if (prop.propertyType != SerializedPropertyType.ManagedReference)
                throw new ArgumentException("Property must be a ManagedReference (SerializeReference).", nameof(prop));

            // Unity format: "AssemblyName Full.Type.Name"
            string unityType = prop.managedReferenceFullTypename;
            string asm = null, type = null;
            SplitUnityType(unityType, out asm, out type);

            var obj = prop.managedReferenceValue;
            var env = new Envelope
            {
                asm = asm,
                type = type,
                isNull = obj == null,
                json = obj == null ? null : EditorJsonUtility.ToJson(obj, prettyPrint: false)
            };

            EditorGUIUtility.systemCopyBuffer = Prefix + JsonUtility.ToJson(env);
        }

        /// <summary>
        /// Paste the managed reference from clipboard into a property. Returns true on success.
        /// </summary>
        public static bool Paste(SerializedProperty prop)
        {
            if (prop == null) throw new ArgumentNullException(nameof(prop));
            if (prop.propertyType != SerializedPropertyType.ManagedReference)
                throw new ArgumentException("Destination must be a ManagedReference (SerializeReference).", nameof(prop));

            var buf = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(buf) || !buf.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            Envelope env;
            try { env = JsonUtility.FromJson<Envelope>(buf.Substring(Prefix.Length)); }
            catch { return false; }
            if (env == null) return false;

            if (env.isNull)
            {
                prop.serializedObject.Update();
                prop.managedReferenceValue = null;
                prop.serializedObject.ApplyModifiedProperties();
                return true;
            }

            var t = ResolveType(env.asm, env.type);
            if (t == null) { Debug.LogWarning($"Could not resolve type '{env.type}' in '{env.asm}'."); return false; }

            object instance;
            try { instance = Activator.CreateInstance(t); }
            catch (Exception ex) { Debug.LogWarning($"Cannot create '{t.FullName}': {ex.Message}"); return false; }

            try
            {
                if (!string.IsNullOrEmpty(env.json))
                    EditorJsonUtility.FromJsonOverwrite(env.json, instance);

                prop.serializedObject.Update();
                prop.managedReferenceValue = instance;
                prop.serializedObject.ApplyModifiedProperties();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Paste failed: {ex.Message}");
                return false;
            }
        }

        private static void SplitUnityType(string unity, out string asm, out string type)
        {
            asm = null; type = null;
            if (string.IsNullOrEmpty(unity)) return;
            int i = unity.IndexOf(' ');
            if (i > 0 && i < unity.Length - 1) { asm = unity.Substring(0, i); type = unity.Substring(i + 1); }
            else { type = unity; }
        }

        private static Type ResolveType(string asm, string fullTypeName)
        {
            if (string.IsNullOrEmpty(fullTypeName)) return null;

            // Try assembly-qualified first
            if (!string.IsNullOrEmpty(asm))
            {
                var t = Type.GetType($"{fullTypeName}, {asm}", false);
                if (t != null) return t;
            }

            // Try loaded assemblies (prefer matching asm if given)
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            if (!string.IsNullOrEmpty(asm))
            {
                var targetAsm = loaded.FirstOrDefault(a =>
                {
                    var name = a.FullName;
                    var comma = name.IndexOf(',');
                    var shortName = comma >= 0 ? name.Substring(0, comma) : name;
                    return string.Equals(shortName, asm, StringComparison.Ordinal);
                });
                var t = targetAsm?.GetType(fullTypeName, false);
                if (t != null) return t;
            }

            // Last resort: search all
            foreach (var a in loaded)
            {
                var t = a.GetType(fullTypeName, false);
                if (t != null) return t;
            }

            return null;
        }
    }
}