using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace HMProtection.Sessions.Tests
{
    public sealed class NamedEventBusTests
    {
        [Test]
        public void FifoSnapshotDefersReentrantPublishAndRejectsNestedDispatch()
        {
            var bus = new NamedEventBus(3); var seen = new List<string>();
            bus.Subscribe("level.event", item => { seen.Add(item.PayloadJson + "#" + item.Sequence); if (item.PayloadJson == "first") { Assert.That(bus.Dispatch(), Is.Zero); Assert.That(bus.TryPublish("level.event", "third", out var error), Is.True, error); } });
            Assert.That(bus.TryPublish("level.event", "first", out var first), Is.True, first);
            Assert.That(bus.TryPublish("level.event", "second", out var second), Is.True, second);
            Assert.That(bus.Dispatch(), Is.EqualTo(2));
            CollectionAssert.AreEqual(new[] { "first#1", "second#2" }, seen);
            Assert.That(bus.Dispatch(), Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { "first#1", "second#2", "third#3" }, seen);
            bus.Dispose();
        }

        [Test]
        public void LimitsAndImmediateSnapshotCancellationAreEnforced()
        {
            var bus = new NamedEventBus(1);
            Assert.That(bus.TryPublish("", "{}", out var topic), Is.False); Assert.That(topic, Does.Contain("topic"));
            Assert.That(bus.TryPublish(new string('a', NamedEventBus.MaxTopicLength + 1), "{}", out _), Is.False);
            Assert.That(bus.TryPublish("ok", new string('x', NamedEventBus.MaxPayloadUtf8Bytes + 1), out var payload), Is.False); Assert.That(payload, Does.Contain("64KB"));
            Assert.That(bus.TryPublish("ok", "{}", out var first), Is.True, first);
            Assert.That(bus.TryPublish("ok", "{}", out var capacity), Is.False); Assert.That(capacity, Does.Contain("capacity"));
            bus.Dispose();

            var calls = new List<string>(); var live = new NamedEventBus(); IDisposable second = null;
            live.Subscribe("topic", _ => { calls.Add("first"); second.Dispose(); });
            second = live.Subscribe("topic", _ => calls.Add("second"));
            live.TryPublish("topic", "{}", out _); live.Dispatch();
            CollectionAssert.AreEqual(new[] { "first" }, calls);
            live.Dispose();
        }

        [Test]
        public void DisposeDuringNamedOrTypedDispatchStopsCapturedSubscribers()
        {
            var calls = 0;
            var named = new NamedEventBus();
            named.Subscribe("topic", _ => named.Dispose()); named.Subscribe("topic", _ => calls++);
            named.TryPublish("topic", "{}", out _); named.Dispatch(); Assert.That(calls, Is.Zero);
            Assert.That(named.TryPublish("topic", "{}", out _), Is.False);

            var typed = new ScopeEventBus();
            typed.Subscribe<int>(_ => typed.Dispose()); typed.Subscribe<int>(_ => calls++);
            typed.Publish(1); typed.Dispatch(); Assert.That(calls, Is.Zero);
        }
    }
}
