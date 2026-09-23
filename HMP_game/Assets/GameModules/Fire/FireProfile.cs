using System;
using UnityEngine;

namespace HMProtection.Modules.Fire
{
    /// <summary>
    /// Immutable-at-runtime tuning shared by fire sources. Runtime pressure,
    /// confirmation and cooldown state live exclusively in FireStateModel.
    /// </summary>
    [CreateAssetMenu(fileName = "FireProfile", menuName = "HM Protection/Fire Profile")]
    public sealed class FireProfile : ScriptableObject
    {
        [Min(0f)] public float suppressSpeed = 1.1f;
        [Min(0f)] public float recoverSpeed = .12f;
        [Range(0f, 1f)] public float lowerAt = .2f;
        [Range(0f, 1f)] public float smokeLowerAt = .15f;
        [Min(0f)] public float reigniteAfter = 4f;
        [Min(0f)] public float confirmSeconds = 5f;

        public bool TryGetTuning(out FireTuning tuning, out string error)
        {
            tuning = new FireTuning(suppressSpeed, recoverSpeed, lowerAt, smokeLowerAt, reigniteAfter, confirmSeconds);
            if (!IsFiniteNonNegative(suppressSpeed) || !IsFiniteNonNegative(recoverSpeed)
                || !IsFiniteNonNegative(reigniteAfter) || !IsFiniteNonNegative(confirmSeconds))
            { error = "Fire speeds and durations must be finite and nonnegative."; return false; }
            if (!IsFinite01(lowerAt) || !IsFinite01(smokeLowerAt))
            { error = "Fire lowering thresholds must be finite values from 0 to 1."; return false; }
            error = null;
            return true;
        }

        public bool TryValidate(out string error) => TryGetTuning(out _, out error);
        static bool IsFiniteNonNegative(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        static bool IsFinite01(float value) => IsFiniteNonNegative(value) && value <= 1f;
    }

    public readonly struct FireTuning
    {
        public readonly float SuppressSpeed, RecoverSpeed, LowerAt, SmokeLowerAt, ReigniteAfter, ConfirmSeconds;
        public FireTuning(float suppressSpeed, float recoverSpeed, float lowerAt, float smokeLowerAt, float reigniteAfter, float confirmSeconds)
        {
            SuppressSpeed = suppressSpeed; RecoverSpeed = recoverSpeed; LowerAt = lowerAt;
            SmokeLowerAt = smokeLowerAt; ReigniteAfter = reigniteAfter; ConfirmSeconds = confirmSeconds;
        }
    }
}
