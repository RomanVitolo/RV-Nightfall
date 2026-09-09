using System;

namespace UHFPS.Runtime
{
    [Serializable]
    public struct InputReference
    {
        public string ActionName;
        public int BindingIndex;
        
        public readonly bool IsAssigned => !string.IsNullOrEmpty(ActionName);

        public static implicit operator string(InputReference input)
        {
            return input.ActionName;
        }

        public bool Equals(InputReference other)
        {
            return ActionName == other.ActionName
                   && BindingIndex == other.BindingIndex;
        }
    }
}