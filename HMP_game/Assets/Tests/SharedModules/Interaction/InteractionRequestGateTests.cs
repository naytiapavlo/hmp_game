using HMProtection.Modules.Interaction;
using NUnit.Framework;

namespace HMProtection.SharedInteraction.Tests
{
    public sealed class InteractionRequestGateTests
    {
        [Test]
        public void SameFrameSecondRequestIsRejected()
        {
            var gate = new InteractionRequestGate();
            Assert.That(gate.TryEnter(42), Is.True);
            gate.Exit();
            Assert.That(gate.TryEnter(42), Is.False);
        }

        [Test]
        public void ReentrantRequestIsRejectedUntilExit_ThenNextFrameMayProceed()
        {
            var gate = new InteractionRequestGate();
            Assert.That(gate.TryEnter(10), Is.True);
            Assert.That(gate.TryEnter(11), Is.False);
            gate.Exit();
            Assert.That(gate.TryEnter(11), Is.True);
        }

        [Test]
        public void CancelClearsInFlightRequestWithoutAllowingDuplicateSameFrame()
        {
            var gate = new InteractionRequestGate();
            Assert.That(gate.TryEnter(8), Is.True);
            gate.Cancel();
            Assert.That(gate.TryEnter(8), Is.False);
            Assert.That(gate.TryEnter(9), Is.True);
        }

        [Test]
        public void RequestIdIsIdempotentAfterCompletionOrCancellation()
        {
            var gate = new InteractionRequestGate();
            Assert.That(gate.TryEnter("request-1"), Is.True);
            gate.Complete("request-1");
            Assert.That(gate.TryEnter(99), Is.True); // A later frame does not reopen a terminal request ID.
            gate.Exit();
            Assert.That(gate.TryEnter("request-1"), Is.False);
            Assert.That(gate.TryEnter("request-2"), Is.True);
            gate.Cancel("request-2");
            Assert.That(gate.TryEnter("request-2"), Is.False);
        }
    }
}
