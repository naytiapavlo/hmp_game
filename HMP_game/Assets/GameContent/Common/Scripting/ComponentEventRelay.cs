using System;
using System.Collections.Generic;
using HMProtection.Entities;
using HMProtection.Modules.Fire;
using HMProtection.Modules.Interaction;
using HMProtection.Modules.Visibility;
using HMProtection.Sessions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HMProtection.Scripting
{
    /// <summary>
    /// Converts component-local changes into session-local script events. It only
    /// observes registered entities; it never discovers objects from the scene.
    /// Door/pickup/seat/visibility topology is captured at entity registration;
    /// adding those capabilities later requires unregister/register.
    /// </summary>
    public sealed class ComponentEventRelay : IDisposable
    {
        readonly LevelSession session;
        readonly EntityRegistry registry;
        readonly Dictionary<EntityHandle, FireSubscription> fires = new Dictionary<EntityHandle, FireSubscription>();
        readonly Dictionary<EntityHandle, TrackedEntity> tracked = new Dictionary<EntityHandle, TrackedEntity>();
        readonly List<TrackedEntity> pollBuffer = new List<TrackedEntity>();
        readonly List<EntityHandle> pendingRemoval = new List<EntityHandle>();
        bool disposed;

        sealed class FireSubscription
        {
            public FireStateModel Model;
            public Action<FireState> StateChanged;
            public Action<FireState> LevelLowered;
            public Action Reignited;
            public Action Extinguished;
        }

        struct Snapshot
        {
            public bool Initialized;
            public bool ActiveSelf, ActiveInHierarchy;
            public bool HasDoor, DoorOpen, DoorAnimating;
            public bool HasPickup, PickupHeld;
            public bool HasSeat, SeatOccupied, SeatTransitioning;
            public bool HasVisibility, Visible;
        }

        sealed class TrackedEntity
        {
            public EntityHandle Handle;
            public GameEntity Entity;
            public IDoorCapability Door;
            public IPickupCapability Pickup;
            public ISeatCapability Seat;
            public IVisibilityCapability Visibility;
            public Snapshot Snapshot;
        }

        public ComponentEventRelay(LevelSession session)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            registry = session.Entities?.Registry ?? throw new ArgumentException("Session needs an entity registry.", nameof(session));
            registry.Registered += Registered;
            registry.Unregistered += Unregistered;
            foreach (var handle in registry.GetHandles()) Observe(handle, false);
        }

        public void Tick()
        {
            if (disposed || session.State != LevelSessionState.Running) return;
            // Registry callbacks maintain topology. This frame loop only visits
            // cached entries and uses allocation-free validity checks.
            pollBuffer.Clear();
            foreach (var item in tracked.Values) pollBuffer.Add(item);
            pendingRemoval.Clear();
            foreach (var item in pollBuffer)
            {
                if (!registry.IsValid(item.Handle)) { pendingRemoval.Add(item.Handle); continue; }
                Poll(item);
            }
            foreach (var handle in pendingRemoval) Forget(handle);
        }

        void Registered(EntityHandle handle) => Observe(handle, true);
        void Unregistered(EntityHandle handle)
        {
            if (!disposed && session.State == LevelSessionState.Running)
                Publish("entity.unregistered", handle, null);
            Forget(handle);
        }

        void Observe(EntityHandle handle, bool publishRegistered)
        {
            if (disposed || !registry.IsValid(handle) || !registry.TryGetEntity(handle, out var entity, out _)) return;
            if (publishRegistered) Publish("entity.registered", handle, null);
            var item = new TrackedEntity { Handle = handle, Entity = entity };
            registry.TryGetCapability<IDoorCapability>(handle, out item.Door, out _);
            registry.TryGetCapability<IPickupCapability>(handle, out item.Pickup, out _);
            registry.TryGetCapability<ISeatCapability>(handle, out item.Seat, out _);
            registry.TryGetCapability<IVisibilityCapability>(handle, out item.Visibility, out _);
            item.Snapshot = ReadSnapshot(item);
            tracked[handle] = item;
            EnsureFireSubscription(handle, entity.GetComponent<FireEffectController>());
        }

        void EnsureFireSubscription(EntityHandle handle, FireEffectController controller)
        {
            FireStateModel model = controller != null ? controller.Model : null;
            if (fires.TryGetValue(handle, out var existing) && ReferenceEquals(existing.Model, model)) return;
            if (existing != null) UnsubscribeFire(handle, existing);
            if (model == null) return;
            var subscription = new FireSubscription { Model = model };
            subscription.StateChanged = state => Publish("fire.state_changed", handle, new JObject { ["state"] = state.ToString() });
            subscription.LevelLowered = state => Publish("fire.level_lowered", handle, new JObject { ["state"] = state.ToString() });
            subscription.Reignited = () => Publish("fire.reignited", handle, null);
            subscription.Extinguished = () => Publish("fire.extinguished", handle, null);
            model.StateChanged += subscription.StateChanged;
            model.LevelLowered += subscription.LevelLowered;
            model.Reignited += subscription.Reignited;
            model.Extinguished += subscription.Extinguished;
            fires.Add(handle, subscription);
        }

        void Poll(TrackedEntity item)
        {
            if (item.Entity == null) { pendingRemoval.Add(item.Handle); return; }
            // A controller/model may be replaced by a content script; rebind the
            // local subscription without scanning the scene or capabilities.
            EnsureFireSubscription(item.Handle, item.Entity.GetComponent<FireEffectController>());
            if (!Alive(item.Door)) item.Door = null;
            if (!Alive(item.Pickup)) item.Pickup = null;
            if (!Alive(item.Seat)) item.Seat = null;
            if (!Alive(item.Visibility)) item.Visibility = null;
            Snapshot previous = item.Snapshot;
            Snapshot current = ReadSnapshot(item);
            if (!previous.Initialized) { item.Snapshot = current; return; }
            if (previous.ActiveSelf != current.ActiveSelf || previous.ActiveInHierarchy != current.ActiveInHierarchy)
                Publish("entity.active_changed", item.Handle, new JObject { ["activeSelf"] = current.ActiveSelf, ["activeInHierarchy"] = current.ActiveInHierarchy });
            if (previous.HasDoor && current.HasDoor && (previous.DoorOpen != current.DoorOpen || previous.DoorAnimating != current.DoorAnimating))
                Publish("door.state_changed", item.Handle, new JObject { ["open"] = current.DoorOpen, ["animating"] = current.DoorAnimating });
            if (previous.HasPickup && current.HasPickup && previous.PickupHeld != current.PickupHeld)
                Publish("pickup.state_changed", item.Handle, new JObject { ["held"] = current.PickupHeld });
            if (previous.HasSeat && current.HasSeat && (previous.SeatOccupied != current.SeatOccupied || previous.SeatTransitioning != current.SeatTransitioning))
                Publish("seat.state_changed", item.Handle, new JObject { ["occupied"] = current.SeatOccupied, ["transitioning"] = current.SeatTransitioning });
            if (previous.HasVisibility && current.HasVisibility && previous.Visible != current.Visible)
                Publish("visibility.changed", item.Handle, new JObject { ["visible"] = current.Visible });
            item.Snapshot = current;
        }

        Snapshot ReadSnapshot(TrackedEntity item)
        {
            var entity = item.Entity;
            var target = entity != null ? entity.ActivationTarget : null;
            var result = new Snapshot { Initialized = true, ActiveSelf = target != null && target.activeSelf, ActiveInHierarchy = target != null && target.activeInHierarchy };
            if (Alive(item.Door)) { result.HasDoor = true; result.DoorOpen = item.Door.IsOpen; result.DoorAnimating = item.Door.IsAnimating; }
            if (Alive(item.Pickup)) { result.HasPickup = true; result.PickupHeld = item.Pickup.IsHeld; }
            if (Alive(item.Seat)) { result.HasSeat = true; result.SeatOccupied = item.Seat.IsOccupied; result.SeatTransitioning = item.Seat.IsTransitioning; }
            if (Alive(item.Visibility)) { result.HasVisibility = true; result.Visible = item.Visibility.IsVisible; }
            return result;
        }

        static bool Alive(object capability) => capability != null && (!(capability is UnityEngine.Object unity) || unity != null);

        void Forget(EntityHandle handle)
        {
            tracked.Remove(handle);
            if (!fires.TryGetValue(handle, out var fire)) return;
            UnsubscribeFire(handle, fire);
        }
        void UnsubscribeFire(EntityHandle handle, FireSubscription fire)
        {
            fire.Model.StateChanged -= fire.StateChanged;
            fire.Model.LevelLowered -= fire.LevelLowered;
            fire.Model.Reignited -= fire.Reignited;
            fire.Model.Extinguished -= fire.Extinguished;
            fires.Remove(handle);
        }

        void Publish(string topic, EntityHandle handle, JObject fields)
        {
            if (disposed || session.State != LevelSessionState.Running) return;
            var payload = fields ?? new JObject();
            payload["entity"] = ScriptEntityToken.Encode(handle);
            payload["id"] = handle.Id.Value;
            session.ScriptEvents.TryPublish(topic, payload.ToString(Formatting.None), out _);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            registry.Registered -= Registered;
            registry.Unregistered -= Unregistered;
            foreach (var pair in fires) { pair.Value.Model.StateChanged -= pair.Value.StateChanged; pair.Value.Model.LevelLowered -= pair.Value.LevelLowered; pair.Value.Model.Reignited -= pair.Value.Reignited; pair.Value.Model.Extinguished -= pair.Value.Extinguished; }
            fires.Clear(); tracked.Clear(); pollBuffer.Clear(); pendingRemoval.Clear();
        }
    }
}
