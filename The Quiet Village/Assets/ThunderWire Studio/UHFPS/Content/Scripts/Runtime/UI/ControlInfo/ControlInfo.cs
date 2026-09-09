using System;
using UnityEngine;

namespace UHFPS.Runtime
{
    [Serializable]
    public sealed class ControlInfo
    {
        public bool IsEnabled;
        public InputReference Input;
        public GString Text;
    }
}
