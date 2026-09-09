using System.Collections.Generic;
using UnityEngine;

namespace UHFPS.Runtime
{
    // --------------------------------------------------
    // ICON DATA
    // --------------------------------------------------

    public struct InteractIconData
    {
        public Vector3 IconPosition;
        public Sprite Sprite;
        public Vector2 Size;

        public InteractIconData(Vector3 iconPosition, Sprite sprite, Vector2 size)
        {
            IconPosition = iconPosition;
            Sprite = sprite;
            Size = size;
        }
    }

    // --------------------------------------------------
    // SOURCE INTERFACE
    // --------------------------------------------------

    public interface IInteractIconSource
    {
        /// <summary>
        /// Returns the icon data for the current state.
        /// </summary>
        InteractIconData GetInteractIconData();
    }

    // --------------------------------------------------
    // MODULE
    // --------------------------------------------------

    [System.Serializable]
    public class InteractIconModule : ManagerModule
    {
        // --------------------------------------------------
        // CACHE
        // --------------------------------------------------

        public struct IconUpdateCache
        {
            public IInteractIconSource Source;
            public FloatingIcon Icon;
            public Sprite LastSprite;
            public Vector2 LastSize;
        }

        // --------------------------------------------------
        // SETTINGS
        // --------------------------------------------------

        public override string Name => "Interact Icon";

        public GameObject InteractIconPrefab;
        public float FadeInTime = 0.2f;
        public float FadeOutTime = 0.05f;

        // --------------------------------------------------
        // STATE
        // --------------------------------------------------

        private readonly Dictionary<IInteractIconSource, FloatingIcon> floatingIcons = new();
        private readonly List<IconUpdateCache> updateCache = new();

        // --------------------------------------------------
        // PUBLIC API
        // --------------------------------------------------

        public void ShowInteractIcon(IInteractIconSource source)
        {
            if (source == null || InteractIconPrefab == null)
                return;

            if (floatingIcons.ContainsKey(source))
                return;

            InteractIconData data = source.GetInteractIconData();

            Vector3 screenPoint = PlayerPresence.PlayerCamera.WorldToScreenPoint(data.IconPosition);
            GameObject iconObject = Object.Instantiate(InteractIconPrefab, screenPoint, Quaternion.identity, GameManager.FloatingIcons);

            FloatingIcon floatingIcon = iconObject.AddComponent<FloatingIcon>();
            floatingIcon.SetSprite(data.Sprite, data.Size);
            floatingIcon.FadeIn(FadeInTime);

            floatingIcons.Add(source, floatingIcon);
            updateCache.Add(new IconUpdateCache
            {
                Source = source,
                Icon = floatingIcon,
                LastSprite = data.Sprite,
                LastSize = data.Size
            });
        }

        public void DestroyInteractIcon(IInteractIconSource source)
        {
            if (source == null)
                return;

            if (!floatingIcons.TryGetValue(source, out FloatingIcon icon))
                return;

            icon.FadeOut(FadeOutTime);
            floatingIcons.Remove(source);
        }

        // --------------------------------------------------
        // UPDATE LOOP
        // --------------------------------------------------

        public override void OnUpdate()
        {
            for (int i = 0; i < updateCache.Count; i++)
            {
                IconUpdateCache cache = updateCache[i];

                if (cache.Icon == null || cache.Source == null)
                {
                    updateCache.RemoveAt(i);
                    i--;
                    continue;
                }

                InteractIconData data = cache.Source.GetInteractIconData();

                // Update position
                cache.Icon.transform.position = PlayerPresence.PlayerCamera.WorldToScreenPoint(data.IconPosition);

                // Update visual if changed
                if (data.Sprite != cache.LastSprite || data.Size != cache.LastSize)
                {
                    cache.Icon.SetSprite(data.Sprite, data.Size);
                    cache.LastSprite = data.Sprite;
                    cache.LastSize = data.Size;
                    updateCache[i] = cache;
                }
            }
        }
    }
}
