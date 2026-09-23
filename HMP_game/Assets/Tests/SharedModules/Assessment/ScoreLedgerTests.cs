using HMProtection.Modules.Score;
using NUnit.Framework;

namespace HMProtection.Assessment.Tests
{
    public sealed class ScoreLedgerTests
    {
        [Test]
        public void BusinessKeyCanOnlyAwardOnce()
        {
            var ledger = new ScoreLedger();
            Assert.That(ledger.TryRecord("attempt.1", "q", "a", true, false, 3f, out var first, out _), Is.True);
            Assert.That(ledger.TryRecord("attempt.1", "q", "b", false, false, 4f, out var duplicate, out _), Is.False);
            Assert.That(duplicate, Is.SameAs(first));
            Assert.That(ledger.Entries.Count, Is.EqualTo(1));
        }
    }
}
