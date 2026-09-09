using System;
using UnityEngine;

namespace UHFPS.Runtime
{
    [Serializable]
    public struct MinMax
    {
        public float min;
        public float max;

        public bool Flipped => max < min;

        public bool HasValue => min != 0 || max != 0;

        public float RealMin => Mathf.Min(min, max);
        public float RealMax => Mathf.Max(max, min);
        public Vector2 RealVector => this;
        public Vector2 Vector => new(min, max);

        public MinMax(float min, float max)
        {
            this.min = min;
            this.max = max;
        }

        public static implicit operator Vector2(MinMax minMax)
        {
            return new Vector2(minMax.RealMin, minMax.RealMax);
        }

        public static implicit operator MinMax(Vector2 vector)
        {
            MinMax result = default;
            result.min = vector.x;
            result.max = vector.y;
            return result;
        }

        public MinMax Flip() => new MinMax(max, min);

        /// <summary>
        /// Calculate weight (0-1) based on the distance between min and max values.
        /// <br>1 = close to min, 0 = close to max (not flipped)</br>
        /// </summary>
        public float Weight(float distance, bool flip = false)
        {
            return flip
                ? Mathf.InverseLerp(RealMin, RealMax, distance)
                : Mathf.InverseLerp(RealMax, RealMin, distance);
        }

        /// <summary>
        /// Interpolates between the minimum and maximum real values using the specified interpolation factor.
        /// </summary>
        public float Lerp(float t, bool flip = false)
        {
            return flip
                ? Mathf.Lerp(RealMax, RealMin, t)
                : Mathf.Lerp(RealMin, RealMax, t);
        }

        /// <summary>
        /// Get the real value based on the state.
        /// </summary>
        /// <param name="flip">Flipped state returns the min value when state is true, otherwise returns max.</param>
        public float State(bool state, bool flip = false)
        {
            return flip
                ? (state ? min : max)
                : (state ? max : min);
        }

        public override string ToString() => $"({RealMin}, {RealMax})";
    }
}