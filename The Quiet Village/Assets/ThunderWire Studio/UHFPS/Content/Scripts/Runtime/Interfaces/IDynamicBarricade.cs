using UnityEngine;

namespace UHFPS.Runtime
{
    public interface IDynamicBarricade
    {
        /// <summary>
        /// Check if the dynamic object is barricaded.
        /// </summary>
        bool CheckBarricaded();
    }
}