using System;
using System.Collections.Generic;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.World
{
    /// <summary>
    /// Replicates any UHFPS saveable object — puzzles, switches, lights, storage — through its own save format.
    /// </summary>
    /// <remarks>
    /// Every object UHFPS can save already knows how to describe its state (<c>OnSave</c>) and restore it
    /// (<c>OnLoad</c>). Polling that description and sending it when it changes replicates dozens of object
    /// types without writing code for any of them.
    ///
    /// Polled rather than hooked because puzzles change state through their own buttons and screens, several
    /// steps removed from the interaction that started them. The cost is one small serialisation per object
    /// every half second.
    ///
    /// Limits, inherent to the save format: <c>OnLoad</c> restores state but does not replay the events that
    /// changing it would have fired. What those events move — a door, a prop — replicates through its own
    /// entity; anything else they touch (a lamp with no saveable of its own) changes only for the player who
    /// triggered it. An object whose state changes by itself on every client is detected and stops syncing,
    /// rather than flooding the network.
    /// </remarks>
    public class SyncedSaveable : WorldSyncEntity
    {
        [Tooltip("The UHFPS component whose save state is replicated. Must implement ISaveable.")]
        [SerializeField] private MonoBehaviour m_saveable;

        [Tooltip("Seconds between checks for a change.")]
        [SerializeField] private float m_pollInterval = 0.5f;

        [Tooltip("More changes than this within the churn window marks the object as changing by itself.")]
        [SerializeField] private int m_churnLimit = 8;

        [SerializeField] private float m_churnWindow = 5f;

        private ISaveable m_target;
        private string m_lastState;
        private float m_nextPollAt;
        private bool m_churning;
        private bool m_reportedError;
        private readonly Queue<float> m_recentSends = new();

        /// <summary>The component this entity replicates.</summary>
        public MonoBehaviour Target => m_saveable;

        private void Awake()
        {
            m_target = m_saveable as ISaveable;
            if (m_target == null)
            {
                Debug.LogError($"{nameof(SyncedSaveable)} on '{name}' has no ISaveable target; it will not sync.", this);
                enabled = false;
            }
        }

        protected override void OnBound()
        {
            m_lastState = Capture();

            // Spread polls across frames so a level full of saveables does not serialise all of them at once.
            m_nextPollAt = Time.time + UnityEngine.Random.Range(0f, m_pollInterval);
        }

        private void Update()
        {
            if (!IsLive || m_churning || m_target == null || Time.time < m_nextPollAt) return;

            m_nextPollAt = Time.time + m_pollInterval;

            var state = Capture();
            if (state == null || state == m_lastState) return;

            m_lastState = state;
            if (!CanPublish) return;

            if (IsChurning())
            {
                m_churning = true;
                Debug.LogWarning($"{nameof(SyncedSaveable)}: '{name}' ({m_saveable.GetType().Name}) changes state by " +
                                 "itself and has stopped syncing. Disable this component if that is expected.", this);
                return;
            }

            World.PublishState(this, state);
        }

        internal override void ApplyRemoteState(string json)
        {
            var state = Parse(json);
            if (state == null || m_target == null) return;

            try
            {
                m_target.OnLoad(state);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }

            // Re-read rather than trusting the incoming text: what OnLoad produced is what the next poll
            // compares against, and a formatting difference must not echo the change straight back.
            m_lastState = Capture();
        }

        private string Capture()
        {
            try
            {
                return Serialize(m_target.OnSave());
            }
            catch (Exception exception)
            {
                if (!m_reportedError)
                {
                    Debug.LogWarning($"{nameof(SyncedSaveable)}: '{name}' could not capture its state: {exception.Message}", this);
                    m_reportedError = true;
                }

                return null;
            }
        }

        private bool IsChurning()
        {
            var now = Time.time;
            m_recentSends.Enqueue(now);
            while (m_recentSends.Count > 0 && now - m_recentSends.Peek() > m_churnWindow) m_recentSends.Dequeue();

            return m_recentSends.Count > m_churnLimit;
        }
    }
}
