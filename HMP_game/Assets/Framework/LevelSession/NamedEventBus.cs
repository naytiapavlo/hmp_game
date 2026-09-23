using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HMProtection.Sessions
{
    public readonly struct NamedEvent
    {
        public NamedEvent(string topic, string payloadJson, long sequence) { Topic = topic; PayloadJson = payloadJson; Sequence = sequence; }
        public string Topic { get; }
        public string PayloadJson { get; }
        public long Sequence { get; }
    }

    /// <summary>Bounded string event queue for scripting facades; JSON remains opaque here.</summary>
    public sealed class NamedEventBus : IDisposable
    {
        public const int DefaultCapacity = 1024;
        public const int MaxTopicLength = 128;
        public const int MaxPayloadUtf8Bytes = 64 * 1024;
        private sealed class Subscriber { public Action<NamedEvent> Callback; public bool Active = true; }
        private readonly Dictionary<string, List<Subscriber>> subscribers = new Dictionary<string, List<Subscriber>>(StringComparer.Ordinal);
        private readonly Queue<NamedEvent> pending = new Queue<NamedEvent>();
        private readonly int capacity;
        private long nextSequence;
        private bool disposed, dispatching;

        public NamedEventBus(int capacity = DefaultCapacity) { this.capacity = Math.Max(1, capacity); }

        public bool TryPublish(string topic, string payloadJson, out string error)
        {
            if (disposed) { error = "Named event bus is disposed."; return false; }
            if (!TryValidateTopic(topic, out error)) return false;
            payloadJson ??= string.Empty;
            if (Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadUtf8Bytes) { error = "Event payload exceeds the 64KB UTF-8 limit."; return false; }
            if (pending.Count >= capacity) { error = "Named event queue is at capacity."; return false; }
            if (nextSequence == long.MaxValue) { error = "Named event sequence is exhausted."; return false; }
            pending.Enqueue(new NamedEvent(topic, payloadJson, ++nextSequence));
            error = null;
            return true;
        }

        public IDisposable Subscribe(string topic, Action<NamedEvent> callback)
        {
            if (!TryValidateTopic(topic, out var error)) throw new ArgumentException(error, nameof(topic));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (disposed) return EmptySubscription.Instance;
            if (!subscribers.TryGetValue(topic, out var list)) subscribers[topic] = list = new List<Subscriber>();
            var subscriber = new Subscriber { Callback = callback };
            list.Add(subscriber);
            return new Subscription(this, topic, subscriber);
        }

        /// <summary>Only events queued on entry are delivered; nested dispatch does no work.</summary>
        public int Dispatch(int maxEvents = 256)
        {
            if (disposed || dispatching || maxEvents <= 0) return 0;
            dispatching = true;
            try
            {
                var budget = Math.Min(maxEvents, pending.Count);
                var delivered = 0;
                for (var index = 0; index < budget && !disposed; index++)
                {
                    var item = pending.Dequeue();
                    delivered++;
                    if (!subscribers.TryGetValue(item.Topic, out var listeners)) continue;
                    foreach (var subscriber in listeners.ToArray())
                    {
                        if (disposed || !subscriber.Active) continue;
                        try { subscriber.Callback(item); }
                        catch (Exception exception) { Debug.LogException(exception); }
                    }
                }
                return delivered;
            }
            finally { dispatching = false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            pending.Clear();
            foreach (var list in subscribers.Values) foreach (var subscriber in list) subscriber.Active = false;
            subscribers.Clear();
        }

        private static bool TryValidateTopic(string topic, out string error)
        {
            if (string.IsNullOrWhiteSpace(topic)) { error = "Event topic is required."; return false; }
            if (topic.Length > MaxTopicLength) { error = "Event topic exceeds the 128 character limit."; return false; }
            error = null;
            return true;
        }
        private void Unsubscribe(string topic, Subscriber subscriber)
        {
            subscriber.Active = false;
            if (!subscribers.TryGetValue(topic, out var list)) return;
            list.Remove(subscriber);
            if (list.Count == 0) subscribers.Remove(topic);
        }
        private sealed class Subscription : IDisposable
        {
            private NamedEventBus bus; private readonly string topic; private readonly Subscriber subscriber;
            public Subscription(NamedEventBus bus, string topic, Subscriber subscriber) { this.bus = bus; this.topic = topic; this.subscriber = subscriber; }
            public void Dispose() { var current = bus; bus = null; current?.Unsubscribe(topic, subscriber); }
        }
        private sealed class EmptySubscription : IDisposable { public static readonly EmptySubscription Instance = new EmptySubscription(); public void Dispose() { } }
    }
}
