using System;

namespace HMProtection.Modules.Fire
{
    /// <summary>
    /// Fire's authoritative, presentation-independent state.  A level can own one
    /// instance per ignition source; views and suppression components only submit
    /// commands to it.  It deliberately has no Unity dependency, so its rules can
    /// be tested without a scene or a particle prefab.
    /// </summary>
    public sealed class FireStateModel
    {
        public FireState CurrentState { get; private set; } = FireState.None;
        public float Pressed01 { get; private set; } = 1f;
        public bool Confirming { get; private set; }
        public bool ExtinguishedConfirmed { get; private set; }
        public bool IsSimulationPaused { get; private set; }

        public float SuppressSpeed { get; set; } = 1.1f;
        public float RecoverSpeed { get; set; } = .12f;
        public float LowerAt { get; set; } = .2f;
        public float SmokeLowerAt { get; set; } = .15f;
        public float ReigniteAfter { get; set; } = 4f;
        public float ConfirmSeconds { get; set; } = 5f;

        public event Action<FireState> StateChanged;
        public event Action<float, bool> VisualPressureChanged;
        public event Action<FireState> LevelLowered;
        public event Action Reignited;
        public event Action Extinguished;

        float clock;
        float lastEffectiveAt;
        bool loweredBySuppression;
        bool sprayEffective;
        float sprayCoverage;

        public void SetSimulationPaused(bool value)
        {
            IsSimulationPaused = value;
            if (value) { sprayEffective = false; sprayCoverage = 0f; }
        }

        /// <summary>External gameplay/debug command. It starts a new fire episode.</summary>
        public void SetState(FireState value)
        {
            if (value < FireState.None || value > FireState.Large) return;
            bool changed = CurrentState != value;
            CurrentState = value;
            Pressed01 = 1f;
            loweredBySuppression = false;
            Confirming = false;
            ExtinguishedConfirmed = false;
            sprayEffective = false;
            sprayCoverage = 0f;
            if (changed) StateChanged?.Invoke(value);
            VisualPressureChanged?.Invoke(Pressed01, value == FireState.SmokeOnly);
        }

        /// <summary>Records the result sampled by a spray tool for the current frame.</summary>
        public void ApplySpray(bool effective) => ApplySpray(effective, 1f);

        /// <summary>
        /// Adds a normalized share of one tool's spray budget. Multiple reports in
        /// the same frame use the strongest coverage rather than multiplying a
        /// fire's suppression rate.
        /// </summary>
        public void ApplySpray(bool effective, float coverage)
        {
            // Several tools may sample this fire during one render frame. A valid
            // coverage sample must win over a miss from another nozzle.
            if (!effective || IsSimulationPaused || float.IsNaN(coverage) || float.IsInfinity(coverage) || coverage <= 0f) return;
            sprayEffective = true;
            sprayCoverage = Math.Max(sprayCoverage, Math.Max(0f, Math.Min(1f, coverage)));
            lastEffectiveAt = clock;
        }

        /// <summary>Call once per frame by the owning suppression driver.</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || IsSimulationPaused)
            { sprayEffective = false; sprayCoverage = 0f; return; }
            clock += deltaTime;
            // ApplySpray is a one-frame sample. A caller must continue supplying
            // samples while the handle is held; stale effective samples never keep
            // suppressing after a spray tool is disabled or destroyed.
            bool effectiveThisTick = sprayEffective;
            sprayEffective = false;
            float coverageThisTick = sprayCoverage;
            sprayCoverage = 0f;
            if (CurrentState == FireState.None)
            {
                if (Confirming && clock - lastEffectiveAt >= ConfirmSeconds)
                {
                    Confirming = false;
                    ExtinguishedConfirmed = true;
                    Extinguished?.Invoke();
                }
                return;
            }

            if (effectiveThisTick)
            {
                Pressed01 = Math.Max(0f, Pressed01 - Math.Max(0f, SuppressSpeed) * coverageThisTick * deltaTime);
                PublishPressure();
                float threshold = CurrentState == FireState.SmokeOnly ? SmokeLowerAt : LowerAt;
                if (Pressed01 <= threshold) LowerOneLevel();
                return;
            }

            if (Pressed01 < 1f)
            {
                Pressed01 = Math.Min(1f, Pressed01 + Math.Max(0f, RecoverSpeed) * deltaTime);
                PublishPressure();
            }
            if (loweredBySuppression && CurrentState == FireState.Small && !ExtinguishedConfirmed
                && clock - lastEffectiveAt >= ReigniteAfter)
            {
                ChangeState(FireState.Medium);
                Pressed01 = 1f;
                loweredBySuppression = false;
                PublishPressure();
                Reignited?.Invoke();
            }
        }

        void LowerOneLevel()
        {
            FireState next = CurrentState switch
            {
                FireState.Large => FireState.Medium,
                FireState.Medium => FireState.Small,
                FireState.Small => FireState.SmokeOnly,
                _ => FireState.None,
            };
            ChangeState(next);
            Pressed01 = 1f;
            loweredBySuppression = true;
            if (next == FireState.None)
            {
                Confirming = true;
                lastEffectiveAt = clock;
            }
            PublishPressure();
            LevelLowered?.Invoke(next);
        }

        void ChangeState(FireState next)
        {
            if (CurrentState == next) return;
            CurrentState = next;
            StateChanged?.Invoke(next);
        }

        void PublishPressure() => VisualPressureChanged?.Invoke(Pressed01, CurrentState == FireState.SmokeOnly);
    }
}
