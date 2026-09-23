using System;
using HMProtection.Entities;
using NUnit.Framework;
using UnityEngine;

namespace HMProtection.Sessions.Tests
{
    public sealed class LevelSessionTests
    {
        private readonly System.Collections.Generic.List<GameObject> roots = new System.Collections.Generic.List<GameObject>();
        private readonly System.Collections.Generic.List<EntityScope> scopes = new System.Collections.Generic.List<EntityScope>();

        [TearDown]
        public void TearDown()
        {
            foreach (var scope in scopes) scope.Dispose();
            foreach (var root in roots) if (root != null) UnityEngine.Object.DestroyImmediate(root);
            scopes.Clear();
            roots.Clear();
        }

        [Test]
        public void SessionsAreIsolated_AndStartingRequiresReadyEntities()
        {
            var unready = new LevelSession(new EntityScope("unready"));
            Assert.That(unready.Start(out _), Is.False);
            unready.Dispose();

            var first = new LevelSession(CreateReadyScope("first"));
            var second = new LevelSession(CreateReadyScope("second"));
            Assert.That(first.Start(out var firstError), Is.True, firstError);
            Assert.That(second.Start(out var secondError), Is.True, secondError);
            var firstCount = 0;
            var secondCount = 0;
            first.Events.Subscribe<int>(_ => firstCount++);
            second.Events.Subscribe<int>(_ => secondCount++);
            first.Events.Publish(1);
            Assert.That(first.Tick(0f, 0f, out firstError), Is.True, firstError);
            Assert.That(firstCount, Is.EqualTo(1));
            Assert.That(secondCount, Is.Zero);
            first.Dispose();
            second.Dispose();
        }

        [Test]
        public void EventDispatchIsFifo_ReentrantAndBounded()
        {
            var bus = new ScopeEventBus();
            var sequence = new System.Collections.Generic.List<int>();
            bus.Subscribe<int>(value => { sequence.Add(value); if (value == 1) bus.Publish(2); });
            bus.Publish(1);
            Assert.That(bus.Dispatch(1), Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { 1 }, sequence);
            Assert.That(bus.Dispatch(1), Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { 1, 2 }, sequence);
            bus.Dispose();
        }

        [Test]
        public void TimerPauseRestartAndGenerationPreventOldCallback()
        {
            var timers = new SessionTimerService();
            var elapsed = 0;
            Assert.That(timers.Start(1f, ClockDomain.Simulation, _ => elapsed++, out var original, out var error), Is.True, error);
            Assert.That(timers.Pause(original), Is.True);
            timers.Tick(2f, 0f);
            Assert.That(elapsed, Is.Zero);
            Assert.That(timers.Resume(original), Is.True);
            Assert.That(timers.Restart(original, 0f, out var restarted, out error), Is.True, error);
            Assert.That(timers.IsActive(original), Is.False);
            timers.Tick(0f, 0f);
            Assert.That(elapsed, Is.EqualTo(1));
            Assert.That(timers.IsActive(restarted), Is.False);
            timers.Tick(10f, 0f);
            Assert.That(elapsed, Is.EqualTo(1));
        }

        [Test]
        public void TimerCallbackCanCancelAnotherElapsedTimer_AndElapsedHandleCannotRestart()
        {
            var timers = new SessionTimerService();
            var firstCalls = 0;
            var secondCalls = 0;
            TimerHandle second = default;
            Assert.That(timers.Start(0f, ClockDomain.Simulation, _ => { firstCalls++; timers.Cancel(second); }, out _, out var error), Is.True, error);
            Assert.That(timers.Start(0f, ClockDomain.Simulation, _ => secondCalls++, out second, out error), Is.True, error);
            timers.Tick(0f, 0f);
            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(secondCalls, Is.Zero);
            Assert.That(timers.Restart(second, 1f, out _, out error), Is.False);
            Assert.That(error, Does.Contain("stale"));
        }

        [Test]
        public void ControlLeasesComposeAndReleaseIndependently()
        {
            var controls = new ControlLeaseService();
            var first = controls.Acquire(this, ControlMask.Movement | ControlMask.Interaction);
            var second = controls.Acquire("tutorial", ControlMask.Movement);
            Assert.That(controls.IsBlocked(ControlMask.Movement), Is.True);
            Assert.That(controls.IsBlocked(ControlMask.Interaction), Is.True);
            first.Dispose();
            Assert.That(controls.IsBlocked(ControlMask.Movement), Is.True);
            Assert.That(controls.IsBlocked(ControlMask.Interaction), Is.False);
            second.Dispose();
            Assert.That(controls.IsBlocked(ControlMask.Movement), Is.False);
            controls.Dispose();
        }

        [Test]
        public void StopCancelsTimersSubscriptionsControlsAndEntityScope_Idempotently()
        {
            var scope = CreateReadyScope("stop");
            var session = new LevelSession(scope);
            Assert.That(session.Start(out var error), Is.True, error);
            var callbacks = 0;
            session.Events.Subscribe<string>(_ => callbacks++);
            session.Timers.Start(0f, ClockDomain.Simulation, _ => callbacks++, out _, out error);
            var lease = session.Controls.Acquire(this, ControlMask.ToolUse);
            session.Stop();
            session.Stop();
            Assert.That(session.Lifetime.IsCancellationRequested, Is.True);
            Assert.That(scope.IsDisposed, Is.True);
            Assert.That(session.Controls.IsBlocked(ControlMask.ToolUse), Is.False);
            Assert.That(session.Tick(0f, 0f, out _), Is.False);
            Assert.That(callbacks, Is.Zero);
            Assert.That(session.Timers.Start(1f, ClockDomain.Simulation, _ => callbacks++, out _, out error), Is.False);
            lease.Dispose();
            session.Dispose();
            Assert.That(session.State, Is.EqualTo(LevelSessionState.Disposed));
        }

        [Test]
        public void SimulationLeasePausesOnlySimulationClockAndTimer()
        {
            using (var session = new LevelSession(CreateReadyScope("pause")))
            {
                Assert.That(session.Start(out var error), Is.True, error);
                int simulation = 0, presentation = 0;
                session.Timers.Start(1f, ClockDomain.Simulation, _ => simulation++, out _, out error);
                session.Timers.Start(1f, ClockDomain.Presentation, _ => presentation++, out _, out error);
                using (session.Controls.Acquire(this, ControlMask.Simulation))
                {
                    Assert.That(session.Tick(2f, 2f, out error), Is.True, error);
                    Assert.That(simulation, Is.Zero);
                    Assert.That(presentation, Is.EqualTo(1));
                    Assert.That(session.Clock.SimulationTime, Is.Zero);
                }
                session.Tick(1f, 0f, out error);
                Assert.That(simulation, Is.EqualTo(1));
            }
        }

        private EntityScope CreateReadyScope(string id)
        {
            var root = new GameObject("session-root-" + id);
            roots.Add(root);
            var scope = new EntityScope(id);
            scopes.Add(scope);
            Assert.That(scope.TryInitialize(null, new[] { root.transform }, out var error), Is.True, error.ToString());
            return scope;
        }
    }
}
