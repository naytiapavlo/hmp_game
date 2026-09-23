namespace HMProtection.Modules.Interaction
{
    /// <summary>Request-ID gate. A terminal request ID is never dispatched again during one scope run.</summary>
    public sealed class InteractionRequestGate
    {
        private int lastAcceptedFrame = -1;
        private readonly System.Collections.Generic.HashSet<string> active = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        private readonly System.Collections.Generic.HashSet<string> terminal = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        public bool IsDispatching { get; private set; }

        public bool TryEnter(int frame)
        {
            if (IsDispatching || lastAcceptedFrame == frame) return false;
            IsDispatching = true;
            lastAcceptedFrame = frame;
            return true;
        }

        public bool TryEnter(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId) || active.Contains(requestId) || terminal.Contains(requestId)) return false;
            active.Add(requestId);
            return true;
        }

        public void Complete(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            active.Remove(requestId);
            terminal.Add(requestId);
        }

        public void Cancel(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            active.Remove(requestId);
            terminal.Add(requestId);
        }

        public void Exit() => IsDispatching = false;
        public void Cancel()
        {
            IsDispatching = false;
            active.Clear();
            terminal.Clear();
        }
    }
}
