using HMProtection.Modules.Fire;
using NUnit.Framework;
using UnityEngine;

namespace HMProtection.Tests.SharedModules.Fire
{
    public sealed class FireStateModelTests
    {
        [Test]
        public void EffectiveSamples_LowerEveryTier_AndConfirmExtinguish()
        {
            var model = NewFastModel();
            int confirmed = 0;
            model.Extinguished += () => confirmed++;
            model.SetState(FireState.Large);
            for (int i = 0; i < 8; i++) { model.ApplySpray(true); model.Tick(.1f); }
            Assert.That(model.CurrentState, Is.EqualTo(FireState.None));
            model.Tick(.31f);
            Assert.That(model.ExtinguishedConfirmed, Is.True);
            Assert.That(confirmed, Is.EqualTo(1));
        }

        [Test]
        public void StaleSpraySample_IsNotAppliedTwice()
        {
            var model = NewFastModel();
            model.SuppressSpeed = 1f; // Stay above LowerAt so pressure is observable.
            model.SetState(FireState.Medium);
            model.ApplySpray(true);
            model.Tick(.1f);
            float afterOne = model.Pressed01;
            model.Tick(.1f);
            Assert.That(model.Pressed01, Is.GreaterThan(afterOne));
        }

        [Test]
        public void PartialCoverage_UsesOnlyItsAllocatedToolBudget()
        {
            var model = NewFastModel();
            model.SetState(FireState.Medium);
            model.ApplySpray(true, .5f);
            model.Tick(.05f);
            Assert.That(model.Pressed01, Is.EqualTo(.75f).Within(.001f));
        }

        [Test]
        public void ProfileValidation_RejectsNonFiniteAndOutOfRangeValues()
        {
            var profile = ScriptableObject.CreateInstance<FireProfile>();
            try
            {
                profile.suppressSpeed = float.NaN;
                Assert.That(profile.TryValidate(out _), Is.False);
                profile.suppressSpeed = 1f;
                profile.lowerAt = 1.1f;
                Assert.That(profile.TryValidate(out _), Is.False);
                profile.lowerAt = .2f;
                Assert.That(profile.TryGetTuning(out var tuning, out var error), Is.True, error);
                Assert.That(tuning.SuppressSpeed, Is.EqualTo(1f));
            }
            finally { Object.DestroyImmediate(profile); }
        }

        [Test]
        public void PausedModel_DoesNotAdvanceRecoveryOrConfirmation()
        {
            var model = NewFastModel();
            model.SetState(FireState.None);
            model.SetSimulationPaused(true);
            model.Tick(10f);
            Assert.That(model.ExtinguishedConfirmed, Is.False);
        }

        [Test]
        public void StoppedAfterLoweringToSmall_ReignitesOneTier()
        {
            var model = NewFastModel();
            model.ReigniteAfter = .2f;
            model.SetState(FireState.Medium);
            model.ApplySpray(true); model.Tick(.1f);
            Assert.That(model.CurrentState, Is.EqualTo(FireState.Small));
            model.Tick(.21f);
            Assert.That(model.CurrentState, Is.EqualTo(FireState.Medium));
        }
        [Test]
        public void PauseDiscardsUnconsumedSamples_AndRejectsSamplesWhilePaused()
        {
            var model = NewFastModel();
            model.SetState(FireState.Large);
            model.ApplySpray(true);
            model.SetSimulationPaused(true);
            model.ApplySpray(true);
            model.Tick(10f);
            model.SetSimulationPaused(false);
            model.Tick(.1f);
            Assert.That(model.CurrentState, Is.EqualTo(FireState.Large));
            Assert.That(model.Pressed01, Is.EqualTo(1f));
        }

        static FireStateModel NewFastModel() => new FireStateModel
        {
            SuppressSpeed = 10f, RecoverSpeed = 1f, LowerAt = .2f,
            SmokeLowerAt = .2f, ConfirmSeconds = .3f, ReigniteAfter = 10f,
        };
    }
}
