using NUnit.Framework.Constraints;
using UnityEngine;

namespace UHFPS.Runtime
{
    public interface IOnDragStart
    {
        void OnDragStart();
    }

    public interface IOnDragEnd
    {
        void OnDragEnd();
    }

    public interface IOnDragUpdate
    {
        void OnDragUpdate(Vector3 velocity);
    }
    
    public interface IDraggableObject
    {
        bool AllowDragging { get; }
        float MaxHoldDistanceValue { get; set; }
        MinMax ZoomDistanceValue { get; set; }
    }
}