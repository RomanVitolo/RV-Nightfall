using System.Collections;
using QuietVillage.Multiplayer.Loading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuietVillage.Multiplayer.Flow
{
    /// <summary>
    /// The first scene's only job: show the splash while the main menu loads, then hand over to it.
    /// </summary>
    /// <remarks>
    /// Lives in BootScene, first in Build Settings. The splash is <see cref="LoadingScreen"/>, which persists, so it keeps
    /// covering the screen through the switch and fades out over the menu. Opening LobbyScene directly in the editor
    /// skips this and still works. Created by <c>Tools > Quiet Village > UI > Set Up Boot And Loading Screens</c>.
    /// </remarks>
    public class BootLoader : MonoBehaviour
    {
        [Tooltip("Scene the game opens on after the splash: the main menu.")]
        [SerializeField] private string m_menuScene = "LobbyScene";

        [Tooltip("Least time the splash stays up, so it reads as a splash rather than a flicker.")]
        [SerializeField] private float m_minimumSeconds = 2.5f;

        private IEnumerator Start()
        {
            LoadingScreen.ShowSplash();

            // A frame for the splash to draw before the load starts taking the frame time.
            yield return null;

            var load = SceneManager.LoadSceneAsync(m_menuScene);
            if (load == null)
            {
                Debug.LogError($"{nameof(BootLoader)}: could not load '{m_menuScene}'. Is it in Build Settings?", this);
                LoadingScreen.Hide();
                yield break;
            }

            load.allowSceneActivation = false;
            var startedAt = Time.unscaledTime;

            // Unity holds an unactivated load at 0.9.
            while (load.progress < 0.9f || Time.unscaledTime - startedAt < m_minimumSeconds)
            {
                var loaded = Mathf.Clamp01(load.progress / 0.9f);
                var timed = m_minimumSeconds > 0f ? Mathf.Clamp01((Time.unscaledTime - startedAt) / m_minimumSeconds) : 1f;
                LoadingScreen.Report(Mathf.Min(loaded, timed), "Loading");
                yield return null;
            }

            LoadingScreen.Report(1f, "Ready");
            LoadingScreen.HideOnNextScene();
            load.allowSceneActivation = true;
        }
    }
}
