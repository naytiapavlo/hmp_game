using HMProtection.Modules.Assessment;
using NUnit.Framework;

namespace HMProtection.Assessment.Tests
{
    public sealed class QuestionSessionTests
    {
        [Test]
        public void AnswerBeforeDeadlineWinsAndSecondTerminalEventIsRejected()
        {
            var session = new QuestionSession("attempt.1", "question.1", 10d, 5d);
            Assert.That(session.TryAnswer("option.a", 14.99d, out var answer), Is.True);
            Assert.That(answer.Resolution, Is.EqualTo(QuestionResolution.Answered));
            Assert.That(session.TryTimeout(15d, out _), Is.False);
        }

        [Test]
        public void DeadlineWinsWhenAnswerArrivesLate()
        {
            var session = new QuestionSession("attempt.2", "question.2", 0d, 3d);
            Assert.That(session.TryAnswer("late", 3.01d, out var result), Is.True);
            Assert.That(result.Resolution, Is.EqualTo(QuestionResolution.TimedOut));
            Assert.That(result.OptionId, Is.Null);
        }

        [Test]
        public void CancellationIsTerminalAndElapsedIsNonNegative()
        {
            var session = new QuestionSession("attempt.3", "question.3", 9d, 4d);
            Assert.That(session.TryCancel(7d, out var result), Is.True);
            Assert.That(result.Resolution, Is.EqualTo(QuestionResolution.Cancelled));
            Assert.That(result.ElapsedSeconds, Is.EqualTo(0d));
            Assert.That(session.TryAnswer("x", 10d, out _), Is.False);
        }
    }
}
