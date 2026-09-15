using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Points the player prefab's UI events back at the managers they call, where the target was lost.
    /// </summary>
    /// <remarks>
    /// "Move Managers And HUD Into Player" carried the HUD's buttons onto the prefab, but a button whose click called a
    /// manager that lived on another scene object kept only the method name: the target became empty. Such a listener does
    /// nothing when clicked (Resume, Main Menu, Save, Restart, Options' Apply, the inventory's outside-click), and
    /// <see cref="SessionMenus"/> and <see cref="SessionDeathScreen"/> could not recognise the buttons they adapt, so dead
    /// players never spectated.
    ///
    /// Every persistent listener on the prefab with an empty target is given the prefab's own component of the type the
    /// listener was made for (recorded beside the method name), when there is exactly one to choose from. Listeners with a
    /// target are left alone. Idempotent: a second run finds nothing to do.
    /// </remarks>
    public static class PlayerEventRepair
    {
        [MenuItem("Tools/Quiet Village/Multiplayer/Repair Player UI Events")]
        public static void Run()
        {
            var path = ProjectPaths.PlayerPrefab;
            var root = PrefabUtility.LoadPrefabContents(path);
            var report = new StringBuilder();
            int repaired = 0, unresolved = 0;

            try
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;

                    var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator();
                    var changed = false;

                    while (property.Next(true))
                    {
                        if (!property.isArray || property.name != "m_Calls" || !property.propertyPath.EndsWith("m_PersistentCalls.m_Calls"))
                            continue;

                        for (var i = 0; i < property.arraySize; i++)
                        {
                            var call = property.GetArrayElementAtIndex(i);
                            var target = call.FindPropertyRelative("m_Target");
                            if (target == null || target.objectReferenceValue != null) continue;

                            var typeName = call.FindPropertyRelative("m_TargetAssemblyTypeName")?.stringValue;
                            var method = call.FindPropertyRelative("m_MethodName")?.stringValue;
                            var type = string.IsNullOrEmpty(typeName) ? null : Type.GetType(typeName);

                            if (type == null || !typeof(Component).IsAssignableFrom(type))
                            {
                                report.AppendLine($"  UNRESOLVED {Where(component)} → {typeName}.{method}: type not found.");
                                unresolved++;
                                continue;
                            }

                            var candidates = root.GetComponentsInChildren(type, true);
                            if (candidates.Length != 1)
                            {
                                report.AppendLine($"  UNRESOLVED {Where(component)} → {type.Name}.{method}: " +
                                                  $"{candidates.Length} {type.Name} on the prefab.");
                                unresolved++;
                                continue;
                            }

                            target.objectReferenceValue = candidates[0];
                            changed = true;
                            repaired++;
                            report.AppendLine($"  {Where(component)} → {type.Name}.{method}");
                        }
                    }

                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }

                if (repaired > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            Debug.Log($"{nameof(PlayerEventRepair)}: {repaired} listener(s) repaired, {unresolved} unresolved.\n{report}");
        }

        private static string Where(Component component)
        {
            var names = component.name;
            var parent = component.transform.parent;
            if (parent != null) names = $"{parent.name}/{names}";
            return $"{names} ({component.GetType().Name})";
        }
    }
}
