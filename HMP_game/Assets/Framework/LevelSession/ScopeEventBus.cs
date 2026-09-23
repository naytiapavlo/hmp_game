using System;
using System.Collections.Generic;
using UnityEngine;

namespace HMProtection.Sessions
{
    /// <summary>A session-local, FIFO event queue. Subscribers are strong references until disposed.</summary>
    public sealed class ScopeEventBus : IDisposable
    {
        private sealed class Subscriber
        {
            public Action<object> Callback;
            public bool Active = true;
        }

        private readonly struct PendingEvent
        {
            public readonly Type Type;
            public readonly object Value;
            public PendingEvent(Type type, object value) { Type = type; Value = value; }
        }

        private readonly Dictionary<Type, List<Subscriber>> subscribers = new Dictionary<Type, List<Subscriber>>();
        private readonly Queue<PendingEvent> pending = new Queue<PendingEvent>();
        private readonly int maxPendingEvents;
        private bool disposed;
        private bool overflowReported;

        public ScopeEventBus(int maxPendingEvents = 1024)
        {
            this.maxPendingEvents = Math.Max(1, maxPendingEvents);
        }

        public IDisposable Subscribe<T>(Action<T> listener)
        {
            if (listener == null) throw new ArgumentNullException(nameof(listener));
            if (disposed) return EmptyDisposable.Instance;
            var type = typeof(T);
            if (!subscribers.TryGetValue(type, out var list)) subscribers[type] = list = new List<Subscriber>();
            var subscriber = new Subscriber { Callback = value => listener((T)value) };
            list.Add(subscriber);
            return new Subscription(this, type, subscriber);
        }

        public void Publish<T>(T value)
        {
            if (disposed) return;
            if (pending.Count >= maxPendingEvents)
            {
                if (!overflowReported)
                {
                    overflowReported = true;
                    Debug.LogWarning("ScopeEventBus dropped events because its pending queue reached capacity.");
                }
                return;
            }
            pending.Enqueue(new PendingEvent(typeof(T), value));
        }

        public int Dispatch(int maxEvents = 256)
        {
            if (disposed || maxEvents <= 0) return 0;
            var dispatched = 0;
            while (dispatched < maxEvents && pending.Count > 0)
            {
                var item = pending.Dequeue();
                dispatched++;
                if (!subscribers.TryGetValue(item.Type, out var list)) continue;
                var snapshot = list.ToArray();
                foreach (var subscriber in snapshot)
                {
                    if (!subscriber.Active) continue;
                    try { subscriber.Callback(item.Value); }
                    catch (Exception exception) { Debug.LogException(exception); }
                }
            }
            return dispatched;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            pending.Clear();
            foreach (var list in subscribers.Values)
                foreach (var subscriber in list) subscriber.Active = false;
            subscribers.Clear();
        }

        private void Unsubscribe(Type type, Subscriber subscriber)
        {
            subscriber.Active = false;
            if (!subscribers.TryGetValue(type, out var list)) return;
            list.Remove(subscriber);
            if (list.Count == 0) subscribers.Remove(type);
        }

        private sealed class Subscription : IDisposable
        {
            private ScopeEventBus bus;
            private readonly Type type;
            private readonly Subscriber subscriber;
            public Subscription(ScopeEventBus bus, Type type, Subscriber subscriber) { this.bus = bus; this.type = type; this.subscriber = subscriber; }
            public void Dispose() { var current = bus; bus = null; current?.Unsubscribe(type, subscriber); }
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new EmptyDisposable();
            public void Dispose() { }
        }
    }
}
