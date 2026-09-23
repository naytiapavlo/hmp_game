using System;

namespace HMProtection.Modules.Assessment
{
    public enum QuestionResolution { Answered, TimedOut, Cancelled }

    public sealed class QuestionAttempt
    {
        public string AttemptId { get; internal set; }
        public string QuestionId { get; internal set; }
        public string OptionId { get; internal set; }
        public QuestionResolution Resolution { get; internal set; }
        public double StartedAt { get; internal set; }
        public double ResolvedAt { get; internal set; }
        public double ElapsedSeconds => Math.Max(0d, ResolvedAt - StartedAt);
    }

    /// <summary>Owns the answer-vs-deadline race: exactly one terminal result is ever emitted.</summary>
    public sealed class QuestionSession
    {
        readonly double deadline;
        readonly QuestionAttempt attempt;
        public bool IsResolved => attempt.Resolution != default || resolved;
        bool resolved;
        public QuestionAttempt Attempt => attempt;

        public QuestionSession(string attemptId, string questionId, double startedAt, double timeLimitSeconds)
        {
            if (string.IsNullOrWhiteSpace(attemptId)) throw new ArgumentException("Attempt ID is required.", nameof(attemptId));
            if (string.IsNullOrWhiteSpace(questionId)) throw new ArgumentException("Question ID is required.", nameof(questionId));
            if (double.IsNaN(startedAt) || double.IsInfinity(startedAt) || double.IsNaN(timeLimitSeconds) || double.IsInfinity(timeLimitSeconds) || timeLimitSeconds < 0d)
                throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));
            attempt = new QuestionAttempt { AttemptId = attemptId, QuestionId = questionId, StartedAt = startedAt };
            deadline = startedAt + timeLimitSeconds;
            if (double.IsInfinity(deadline)) throw new ArgumentOutOfRangeException(nameof(timeLimitSeconds));
        }

        public bool TryAnswer(string optionId, double now, out QuestionAttempt result)
        {
            if (string.IsNullOrWhiteSpace(optionId) || double.IsNaN(now) || double.IsInfinity(now)) { result = null; return false; }
            if (now >= deadline) return TryTimeout(now, out result);
            return Resolve(QuestionResolution.Answered, optionId, now, out result);
        }
        public bool TryTimeout(double now, out QuestionAttempt result)
        {
            if (now < deadline || resolved) { result = null; return false; }
            return Resolve(QuestionResolution.TimedOut, null, now, out result);
        }
        public bool TryCancel(double now, out QuestionAttempt result) => Resolve(QuestionResolution.Cancelled, null, now, out result);
        bool Resolve(QuestionResolution resolution, string optionId, double now, out QuestionAttempt result)
        {
            if (resolved || double.IsNaN(now) || double.IsInfinity(now)) { result = null; return false; }
            resolved = true;
            attempt.Resolution = resolution;
            attempt.OptionId = optionId;
            attempt.ResolvedAt = Math.Max(attempt.StartedAt, now);
            result = attempt;
            return true;
        }
    }
}
