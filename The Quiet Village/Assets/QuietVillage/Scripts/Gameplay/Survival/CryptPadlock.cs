using System.Text;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// Gives a crypt's number padlock a new code each game, and writes it on the note that gives it away.
    /// </summary>
    /// <remarks>
    /// The code comes from the same seed as the loot (<see cref="SurvivalDirector"/>), mixed with this lock's name, so
    /// every client sets the same code without it being sent, and a replay does not open with last game's number. The
    /// note is one of the level's loot spots, so where it lies changes too.
    ///
    /// Unlocking itself is UHFPS's: the padlock's own state and the door's lock replicate through World Sync.
    /// </remarks>
    public class CryptPadlock : MonoBehaviour
    {
        [SerializeField] private PadlockPuzzle m_padlock;

        [Tooltip("Paper notes that give the code away; one per place the note may lie.")]
        [SerializeField] private InteractableItem[] m_notes = System.Array.Empty<InteractableItem>();

        [Tooltip("What the crypt is called on the note, e.g. \"the Ashford crypt\".")]
        [SerializeField] private string m_cryptName = "the crypt";

        [Tooltip("The door this padlock keeps shut. Found beside the padlock when empty.")]
        [SerializeField] private DynamicObject m_door;

        /// <summary>The code this game uses; empty until the seed has arrived.</summary>
        public string Code { get; private set; } = string.Empty;

        private void Awake()
        {
            // The builder hangs the padlock in the doorway's wall, beside the door.
            if (m_door == null && transform.parent != null) m_door = transform.parent.GetComponentInChildren<DynamicObject>(true);
        }

        private void OnEnable()
        {
            if (m_padlock != null) m_padlock.OnPadlockUnlock?.AddListener(UnlockDoor);
        }

        private void OnDisable()
        {
            if (m_padlock != null) m_padlock.OnPadlockUnlock?.RemoveListener(UnlockDoor);
        }

        /// <summary>
        /// Opens the door's lock when the padlock opens, however the player came to it.
        /// </summary>
        /// <remarks>
        /// UHFPS unlocks the door only when the padlock was reached through the door (<c>OnTryUnlock</c> hands it the
        /// door). A player aiming at the padlock itself, which hangs right there on the door, opened it and left the door
        /// locked for good. The door's new state then replicates through its own World Sync entity, as a key would.
        /// </remarks>
        private void UnlockDoor()
        {
            if (m_door != null && m_door.IsLocked) m_door.TryUnlockResult(true);
        }

        /// <summary>Sets the code from the game's seed. Called on every client with the same seed.</summary>
        internal void ApplySeed(int seed)
        {
            if (m_padlock == null) return;

            var digits = Mathf.Max(1, m_padlock.PadlockDigits != null ? m_padlock.PadlockDigits.Length : 4);
            var random = new System.Random(seed ^ (int)StableHash(name));

            var code = new StringBuilder(digits);
            for (var i = 0; i < digits; i++) code.Append((char)('0' + random.Next(10)));

            Code = code.ToString();
            m_padlock.UnlockCode = Code;

            var text = $"If the lock on {m_cryptName} has you beaten again: {SpacedOut(Code)}.\n\nDon't leave this lying about.";
            foreach (var note in m_notes)
            {
                // With a key as well as the text: UHFPS reads the key when the note starts, and a GString built from text
                // alone has none, which threw there. The leading '*' marks it as plain text, not a localisation key.
                if (note != null) note.PaperText = new GString(GString.EXCLUDE_CHAR + text, text);
            }
        }

        private static string SpacedOut(string code) => string.Join(" ", code.ToCharArray());

        /// <summary>32-bit FNV-1a, identical in every process, unlike string.GetHashCode.</summary>
        public static uint StableHash(string text)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var character in text ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619u;
                }

                return hash;
            }
        }

#if UNITY_EDITOR
        public void Configure(PadlockPuzzle padlock, InteractableItem[] notes, string cryptName)
        {
            m_padlock = padlock;
            m_notes = notes;
            m_cryptName = cryptName;
        }
#endif
    }
}
