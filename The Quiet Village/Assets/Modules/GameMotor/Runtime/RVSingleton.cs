using UnityEngine;

namespace Modules.GameMotor.Runtime
{
    /// <summary>
    /// Singleton genérico seguro. No crea instancias automáticamente.
    /// No destruye duplicados salvo que lo habilites explícitamente.
    /// </summary>
    public abstract class RVSingleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;
        private static bool _quitting;

        /// <summary>Crear una instancia si no hay una en escena.</summary>
        protected virtual bool AutoCreateIfMissing => false;

        /// <summary>Destruir duplicados cuando ya existe una instancia.</summary>
        protected virtual bool DestroyDuplicates => false;

        /// <summary>Marcar como DontDestroyOnLoad.</summary>
        protected virtual bool IsPersistent => true;

        public static T Instance
        {
            get
            {
                if (_quitting) return null;
                if (_instance != null) return _instance;

                _instance = FindObjectOfType<T>();
                if (_instance == null)
                {
                    // SIN auto-crear por defecto para no “inyectar” objetos sin que te des cuenta
                    var host = (FindObjectOfType<RVSingleton<T>>() as RVSingleton<T>);
                    if (host != null && host.AutoCreateIfMissing)
                    {
                        var go = new GameObject($"[{typeof(T).Name}]");
                        _instance = go.AddComponent<T>();
                    }
                }
                return _instance;
            }
        }

        protected virtual void Awake()
        {
            if (_instance == null)
            {
                _instance = this as T;
                if (IsPersistent) DontDestroyOnLoad(gameObject);
                return;
            }

            if (_instance != this as T)
            {
                if (DestroyDuplicates)
                {
                    Debug.LogWarning($"[RVSingleton] Duplicate of {typeof(T).Name} destroyed on {name}.");
                    Destroy(gameObject);
                }
                else
                {
                    Debug.LogWarning($"[RVSingleton] Duplicate of {typeof(T).Name} detected on {name} (not destroyed).");
                }
            }
        }

        protected virtual void OnApplicationQuit() => _quitting = true;

        protected virtual void OnDestroy()
        {
            if (_instance == this as T) _instance = null;
        }
    }
}
