using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HMProtection.Entities;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace HMProtection.EntitySystem.Tests
{
    public sealed class EntityRegistryEditModeTests
    {
        private readonly List<UnityEngine.Object> objects = new List<UnityEngine.Object>();
        private readonly List<EntityScope> scopes = new List<EntityScope>();

        [TearDown]
        public void TearDown()
        {
            foreach (var scope in scopes) scope.Dispose();
            foreach (var item in objects) if (item != null) UnityEngine.Object.DestroyImmediate(item);
            scopes.Clear();
            objects.Clear();
        }

        [Test]
        public void SameIdIsIsolatedByScope_AndDuplicateIsRejectedInsideOneScope()
        {
            var first = CreateEntity("shared.id");
            var second = CreateEntity("shared.id");
            var firstScope = Initialize("first", first);
            var secondScope = Initialize("second", second);

            Assert.That(firstScope.Registry.TryResolve("shared.id", out var firstHandle, out var firstError), Is.True, firstError.ToString());
            Assert.That(secondScope.Registry.TryResolve("shared.id", out var secondHandle, out var secondError), Is.True, secondError.ToString());
            Assert.That(firstHandle.ScopeId, Is.Not.EqualTo(secondHandle.ScopeId));
            Assert.That(firstScope.Registry.IsValid(secondHandle), Is.False);

            var duplicate = CreateEntity("shared.id");
            Assert.That(firstScope.Registry.TryRegister(duplicate, out _, out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.DuplicateId));
        }

        [Test]
        public void InitiallyInactiveEntityRemainsRegistered_AndFacadeCanEnableIt()
        {
            var entity = CreateEntity("inactive.entity", tags: new[] { "fire" });
            entity.gameObject.SetActive(false);
            var scope = Initialize("inactive", entity);
            var facade = new EntityFacade(scope);

            Assert.That(facade.TryGet("inactive.entity", out var handle, out var error), Is.True, error.ToString());
            Assert.That(scope.Registry.FindByTag("fire").Count, Is.EqualTo(1));
            Assert.That(scope.Registry.FindByTag("fire", activeOnly: true), Is.Empty);
            Assert.That(facade.TrySetActive(handle, true, out error), Is.True, error.ToString());
            Assert.That(entity.gameObject.activeSelf, Is.True);
            Assert.That(scope.Registry.FindByTag("fire", activeOnly: true).Count, Is.EqualTo(1));
        }

        [Test]
        public void ActiveStateReportsInactiveAncestorWithoutActivatingIt()
        {
            var ancestor = Track(new GameObject("inactive-ancestor"));
            var entity = CreateEntity("nested.entity", parent: ancestor.transform);
            var scope = Initialize("nested-active", entity);
            entity.transform.SetParent(ancestor.transform, false);
            var facade = new EntityFacade(scope);
            Assert.That(facade.TryGet("nested.entity", out var handle, out var error), Is.True, error.ToString());

            ancestor.SetActive(false);
            Assert.That(facade.TryGetActive(handle, out var activeSelf, out var activeInHierarchy, out error), Is.True, error.ToString());
            Assert.That(activeSelf, Is.True);
            Assert.That(activeInHierarchy, Is.False);
            Assert.That(facade.TrySetActive(handle, true, out error), Is.True, error.ToString());
            Assert.That(ancestor.activeSelf, Is.False);
        }

        [Test]
        public void InvalidRequiredBindingDoesNotAttachPrepareOrPublishRegistration()
        {
            var root = Track(new GameObject("invalid-binding-root"));
            var entity = CreateEntity("binding.candidate", parent: root.transform);
            var capability = entity.gameObject.AddComponent<TransactionProbeCapability>();
            entity.Configure("binding.candidate", null, null, new[] { capability });
            var bindingObject = Track(new GameObject("invalid-bindings"));
            var bindings = bindingObject.AddComponent<SceneBindingSet>();
            bindings.Configure(new[] { new SceneEntityBinding("required", "missing.entity") });
            var scope = new EntityScope("invalid-binding");
            scopes.Add(scope);
            var published = false;
            scope.Registry.Registered += _ => published = true;

            Assert.That(scope.TryInitialize(bindings, new[] { root.transform }, out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.EntityNotFound));
            Assert.That(capability.AttachCount, Is.EqualTo(0));
            Assert.That(capability.PrepareCount, Is.EqualTo(0));
            Assert.That(published, Is.False);
        }

        [Test]
        public void RegistrationPublishesOnlyAfterReadyAndContextResolvesDuringAttach()
        {
            var root = Track(new GameObject("ready-event-root"));
            var entity = CreateEntity("ready.event", parent: root.transform);
            var capability = entity.gameObject.AddComponent<TransactionProbeCapability>();
            entity.Configure("ready.event", null, null, new[] { capability });
            var scope = new EntityScope("ready-event");
            scopes.Add(scope);
            var callbackReady = false;
            EntityError callbackError = default;
            scope.Registry.Registered += _ =>
            {
                callbackReady = scope.IsReady;
                scope.Registry.TryResolve("ready.event", out _, out callbackError);
            };

            Assert.That(scope.TryInitialize(null, new[] { root.transform }, out var error), Is.True, error.ToString());
            Assert.That(callbackReady, Is.True);
            Assert.That(callbackError.IsNone, Is.True, callbackError.ToString());
            Assert.That(capability.PublicResolveError.Code, Is.EqualTo(EntityErrorCode.ScopeNotReady));
            Assert.That(capability.ContextResolveSucceeded, Is.True);
        }

        [Test]
        public void UnregisterThenReregisterInvalidatesOldHandle()
        {
            var original = CreateEntity("reused.id");
            var scope = Initialize("generation", original);
            Assert.That(scope.Registry.TryResolve("reused.id", out var oldHandle, out var error), Is.True, error.ToString());
            Assert.That(scope.Registry.TryUnregister(oldHandle, out error), Is.True, error.ToString());

            var replacement = CreateEntity("reused.id");
            Assert.That(scope.Registry.TryRegister(replacement, out var currentHandle, out error), Is.True, error.ToString());
            Assert.That(currentHandle.Generation, Is.Not.EqualTo(oldHandle.Generation));
            Assert.That(scope.Registry.IsValid(oldHandle), Is.False);
            Assert.That(scope.Registry.TryGetEntity(oldHandle, out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.InvalidHandle));
        }

        [Test]
        public void FailedDynamicRegistrationRollsBackOnlyTheCandidate()
        {
            var existing = CreateEntity("existing");
            var scope = Initialize("rollback", existing);
            var candidate = CreateEntity("candidate");
            var failingCapability = candidate.gameObject.AddComponent<FailingPrepareCapability>();
            candidate.Configure("candidate", null, null, new[] { failingCapability });

            Assert.That(scope.Registry.TryRegister(candidate, out _, out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.CapabilityNotReady));
            Assert.That(scope.Registry.TryResolve("existing", out _, out var existingError), Is.True, existingError.ToString());
            Assert.That(scope.Registry.TryResolve("candidate", out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.EntityNotFound));
            Assert.That(candidate.Handle.IsValid, Is.False);
        }

        [Test]
        public void RegisteredCallbackCannotSynchronouslyReenterRegistry()
        {
            var scope = Initialize("reentry", CreateEntity("seed"));
            EntityError callbackError = default;
            var callbackCandidate = CreateEntity("from.callback");
            scope.Registry.Registered += _ => scope.Registry.TryRegister(callbackCandidate, out _, out callbackError);

            Assert.That(scope.Registry.TryRegister(CreateEntity("trigger"), out _, out var error), Is.True, error.ToString());
            Assert.That(callbackError.Code, Is.EqualTo(EntityErrorCode.Busy));
            Assert.That(scope.Registry.TryResolve("from.callback", out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.EntityNotFound));
        }

        [Test]
        public void BindingChecksCapabilityKeyAndColliderMapsToExplicitTarget()
        {
            var root = Track(new GameObject("content-root"));
            var entity = CreateEntity("target.fire", parent: root.transform, tags: new[] { "fire" });
            var collider = entity.gameObject.AddComponent<BoxCollider>();
            var proxy = entity.gameObject.AddComponent<EntityHitProxy>();
            proxy.Configure(entity, new[] { collider });
            var bindingObject = Track(new GameObject("bindings"));
            var bindings = bindingObject.AddComponent<SceneBindingSet>();
            bindings.Configure(new[] { new SceneEntityBinding("primaryFire", "target.fire", capabilityKey: "test.capability") });
            var capability = entity.gameObject.AddComponent<TestCapability>();
            entity.Configure("target.fire", new[] { "fire" }, null, new[] { capability });
            var host = Track(new GameObject("scope-host")).AddComponent<EntityScopeHost>();
            host.Configure("hit", new[] { root.transform }, bindings, new[] { proxy });

            Assert.That(host.TryInitialize("hit", out var error), Is.True, error.ToString());
            Assert.That(bindings.TryResolveRole("primaryFire", host.Scope, out var bound, out error), Is.True, error.ToString());
            Assert.That(host.Scope.Registry.TryResolveHit(collider, out var hit, out error), Is.True, error.ToString());
            Assert.That(hit, Is.EqualTo(bound));

            host.DisposeScope();
            bindings.Configure(new[] { new SceneEntityBinding("primaryFire", "target.fire", capabilityKey: "missing.capability") });
            Assert.That(host.TryInitialize("hit", out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.MissingCapability));
        }

        [Test]
        public void TenScopeReopensProduceFreshScopeIdsAndInvalidateEveryPriorHandle()
        {
            EntityHandle previous = default;
            for (var index = 0; index < 10; index++)
            {
                var entity = CreateEntity("reopen.entity");
                var scope = Initialize("reopen", entity);
                Assert.That(scope.Registry.TryResolve("reopen.entity", out var current, out var error), Is.True, error.ToString());
                if (previous.IsValid) Assert.That(scope.Registry.IsValid(previous), Is.False);
                previous = current;
                scope.Dispose();
                Assert.That(scope.Registry.IsValid(current), Is.False);
            }
        }

        [Test]
        public void RegisteredEntityCannotBeReconfiguredAndInvalidContentIsRejected()
        {
            var entity = CreateEntity("locked.id", tags: new[] { "one" });
            var scope = Initialize("locked", entity);
            entity.Configure("changed.id", new[] { "two" });
            Assert.That(entity.Id.Value, Is.EqualTo("locked.id"));
            Assert.That(entity.Tags, Is.EquivalentTo(new[] { "one" }));

            var invalidId = CreateEntity("Upper.Case");
            Assert.That(scope.Registry.TryRegister(invalidId, out _, out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.InvalidId));
            var duplicateTag = CreateEntity("duplicate.tag", tags: new[] { "fire", "fire" });
            Assert.That(scope.Registry.TryRegister(duplicateTag, out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.DuplicateCapability));
        }

        [Test]
        public void CrossOwnerActivationTargetAndSharedCapabilityAreRejected()
        {
            var scope = Initialize("ownership", CreateEntity("seed"));
            var foreignOwner = CreateEntity("foreign.owner");
            var invalidTarget = CreateEntity("invalid.target");
            invalidTarget.Configure("invalid.target", null, foreignOwner.gameObject);
            Assert.That(scope.Registry.TryRegister(invalidTarget, out _, out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.InvalidOwner));

            var first = CreateEntity("shared.first");
            var second = CreateEntity("shared.second");
            var capability = first.gameObject.AddComponent<TestCapability>();
            first.Configure("shared.first", null, null, new[] { capability });
            second.Configure("shared.second", null, null, new[] { capability });
            Assert.That(scope.Registry.TryRegisterBatch(new[] { first, second }, out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.AlreadyOwned));
            Assert.That(first.Handle.IsValid, Is.False);
            Assert.That(second.Handle.IsValid, Is.False);
        }

        [TestCase(ThrowPhase.Validate)]
        [TestCase(ThrowPhase.Attach)]
        [TestCase(ThrowPhase.Prepare)]
        public void CapabilityExceptionsAreContainedAndRegistrationRollsBack(ThrowPhase phase)
        {
            var scope = Initialize("throws", CreateEntity("stable"));
            var candidate = CreateEntity("throws.candidate");
            var throwing = candidate.gameObject.AddComponent<ThrowingCapability>();
            throwing.Phase = phase;
            candidate.Configure("throws.candidate", null, null, new[] { throwing });

            Assert.That(scope.Registry.TryRegister(candidate, out _, out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.OperationRejected));
            Assert.That(scope.Registry.TryResolve("stable", out _, out var stableError), Is.True, stableError.ToString());
            Assert.That(scope.Registry.TryResolve("throws.candidate", out _, out error), Is.False);
            Assert.That(candidate.Handle.IsValid, Is.False);
        }

        [Test]
        public void AllCapabilitiesAttachBeforeAnyCapabilityPrepares()
        {
            OrderedCapability.ResetCounters();
            var first = CreateEntity("phase.first");
            var second = CreateEntity("phase.second");
            var firstCapability = first.gameObject.AddComponent<OrderedCapability>();
            firstCapability.Name = "first";
            var secondCapability = second.gameObject.AddComponent<OrderedCapability>();
            secondCapability.Name = "second";
            first.Configure("phase.first", null, null, new[] { firstCapability });
            second.Configure("phase.second", null, null, new[] { secondCapability });
            var root = Track(new GameObject("phase-root"));
            first.transform.SetParent(root.transform, false);
            second.transform.SetParent(root.transform, false);
            var scope = new EntityScope("phase");
            scopes.Add(scope);

            Assert.That(scope.TryInitialize(null, new[] { root.transform }, out var error), Is.True, error.ToString());
            CollectionAssert.AreEqual(new[] { "attach:first", "attach:second", "prepare:first", "prepare:second" }, OrderedCapability.Calls);
        }

        [Test]
        public void DetachExceptionDuringRollbackIsLoggedAndCannotLeaveCandidateRegistered()
        {
            var scope = Initialize("detach", CreateEntity("stable"));
            var candidate = CreateEntity("detach.candidate");
            var detachThrower = candidate.gameObject.AddComponent<ThrowingDetachCapability>();
            var failing = candidate.gameObject.AddComponent<FailingPrepareCapability>();
            candidate.Configure("detach.candidate", null, null, new EntityCapability[] { detachThrower, failing });
            LogAssert.Expect(LogType.Exception, new Regex("detach"));

            Assert.That(scope.Registry.TryRegister(candidate, out _, out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.CapabilityNotReady));
            Assert.That(scope.Registry.TryResolve("detach.candidate", out _, out error), Is.False);
            Assert.That(candidate.Handle.IsValid, Is.False);
        }

        [Test]
        public void DuplicateColliderProxiesAreRejectedEvenForSameEntity()
        {
            var root = Track(new GameObject("proxy-root"));
            var entity = CreateEntity("proxy.entity", parent: root.transform);
            var collider = entity.gameObject.AddComponent<BoxCollider>();
            var firstProxy = entity.gameObject.AddComponent<EntityHitProxy>();
            firstProxy.Configure(entity, new[] { collider });
            var secondProxy = Track(new GameObject("second-proxy")).AddComponent<EntityHitProxy>();
            secondProxy.Configure(entity, new[] { collider });
            var host = Track(new GameObject("proxy-host")).AddComponent<EntityScopeHost>();
            host.Configure("proxy", new[] { root.transform }, null, new[] { firstProxy, secondProxy });

            Assert.That(host.TryInitialize("proxy", out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.InvalidOwner));
            Assert.That(host.Scope, Is.Null);
        }

        [Test]
        public void FacadeRejectsCommandsBeforeReadyAfterDisposeAndDuringDisposeCallbacks()
        {
            var unready = new EntityScope("unready");
            scopes.Add(unready);
            var facade = new EntityFacade(unready);
            Assert.That(facade.TryGet("anything", out _, out var error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.ScopeNotReady));

            var scope = Initialize("dispose", CreateEntity("dispose.entity"));
            Assert.That(scope.Registry.TryResolve("dispose.entity", out var handle, out error), Is.True, error.ToString());
            var callbackInvoked = false;
            EntityError callbackError = default;
            scope.Registry.Unregistered += _ =>
            {
                callbackInvoked = true;
                scope.Registry.TryUnregister(handle, out callbackError);
            };
            scope.Dispose();
            Assert.That(callbackInvoked, Is.True, "Dispose must publish cleanup after invalidating handles.");
            Assert.That(callbackError.Code, Is.EqualTo(EntityErrorCode.Busy).Or.EqualTo(EntityErrorCode.ScopeDisposed));
            Assert.That(new EntityFacade(scope).TryGet("dispose.entity", out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.ScopeDisposed));
        }

        [Test]
        public void DisposeDetachesNewestRegistrationFirstAndRejectsDuplicateColliderWithinOneProxy()
        {
            DetachOrderCapability.ResetCounters();
            var first = CreateEntity("dispose.first");
            var second = CreateEntity("dispose.second");
            var firstCapability = first.gameObject.AddComponent<DetachOrderCapability>();
            firstCapability.Name = "first";
            var secondCapability = second.gameObject.AddComponent<DetachOrderCapability>();
            secondCapability.Name = "second";
            first.Configure("dispose.first", null, null, new[] { firstCapability });
            second.Configure("dispose.second", null, null, new[] { secondCapability });
            var scope = Initialize("dispose.order", first);
            Assert.That(scope.Registry.TryRegister(second, out _, out var error), Is.True, error.ToString());
            scope.Dispose();
            CollectionAssert.AreEqual(new[] { "second", "first" }, DetachOrderCapability.Calls);

            var root = Track(new GameObject("duplicate-in-proxy-root"));
            var entity = CreateEntity("duplicate.in.proxy", parent: root.transform);
            var collider = entity.gameObject.AddComponent<BoxCollider>();
            var proxy = entity.gameObject.AddComponent<EntityHitProxy>();
            proxy.Configure(entity, new[] { collider, collider });
            var host = Track(new GameObject("duplicate-in-proxy-host")).AddComponent<EntityScopeHost>();
            host.Configure("duplicate-in-proxy", new[] { root.transform }, null, new[] { proxy });
            Assert.That(host.TryInitialize("duplicate-in-proxy", out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.InvalidOwner));
        }

        private EntityScope Initialize(string levelId, params GameEntity[] entities)
        {
            var root = Track(new GameObject(levelId + "-root"));
            foreach (var entity in entities) entity.transform.SetParent(root.transform, false);
            var scope = new EntityScope(levelId);
            scopes.Add(scope);
            Assert.That(scope.TryInitialize(null, new[] { root.transform }, out var error), Is.True, error.ToString());
            return scope;
        }

        private GameEntity CreateEntity(string id, Transform parent = null, IEnumerable<string> tags = null, IEnumerable<Func<GameObject, EntityCapability>> capabilities = null)
        {
            var gameObject = Track(new GameObject(id));
            if (parent != null) gameObject.transform.SetParent(parent, false);
            var entity = gameObject.AddComponent<GameEntity>();
            var createdCapabilities = new List<EntityCapability>();
            if (capabilities != null) foreach (var create in capabilities) createdCapabilities.Add(create(gameObject));
            entity.Configure(id, tags, null, createdCapabilities);
            return entity;
        }

        private GameObject Track(GameObject gameObject) { objects.Add(gameObject); return gameObject; }

    }

    public sealed class TestCapability : EntityCapability { public override string Key => "test.capability"; }

    public sealed class FailingPrepareCapability : EntityCapability
    {
        public override string Key => "test.failing";
        public override bool Prepare(out EntityError error)
        {
            error = EntityError.Create(EntityErrorCode.CapabilityNotReady, "Prepare", "Intentional test failure.");
            return false;
        }
    }

    public enum ThrowPhase { Validate, Attach, Prepare }

    public sealed class ThrowingCapability : EntityCapability
    {
        public ThrowPhase Phase;
        public override string Key => "test.throwing";
        public override bool Validate(GameEntity owner, out EntityError error)
        {
            if (Phase == ThrowPhase.Validate) throw new InvalidOperationException("validate");
            error = default; return true;
        }
        public override bool Attach(EntityCapabilityContext context, out EntityError error)
        {
            if (Phase == ThrowPhase.Attach) throw new InvalidOperationException("attach");
            error = default; return true;
        }
        public override bool Prepare(out EntityError error)
        {
            if (Phase == ThrowPhase.Prepare) throw new InvalidOperationException("prepare");
            error = default; return true;
        }
    }

    public sealed class OrderedCapability : EntityCapability
    {
        public static readonly List<string> Calls = new List<string>();
        public string Name;
        public override string Key => "test.ordered." + Name;
        public static void ResetCounters() => Calls.Clear();
        public override bool Attach(EntityCapabilityContext context, out EntityError error)
        {
            Calls.Add("attach:" + Name); error = default; return true;
        }
        public override bool Prepare(out EntityError error)
        {
            Calls.Add("prepare:" + Name); error = default; return true;
        }
    }

    public sealed class ThrowingDetachCapability : EntityCapability
    {
        public override string Key => "test.detach.throwing";
        public override void Detach() => throw new InvalidOperationException("detach");
    }

    public sealed class DetachOrderCapability : EntityCapability
    {
        public static readonly List<string> Calls = new List<string>();
        public string Name;
        public override string Key => "test.detach.order." + Name;
        public static void ResetCounters() => Calls.Clear();
        public override void Detach() => Calls.Add(Name);
    }

    public sealed class TransactionProbeCapability : EntityCapability
    {
        public override string Key => "test.transaction.probe";
        public int AttachCount { get; private set; }
        public int PrepareCount { get; private set; }
        public EntityError PublicResolveError { get; private set; }
        public bool ContextResolveSucceeded { get; private set; }
        public override bool Attach(EntityCapabilityContext context, out EntityError error)
        {
            AttachCount++;
            context.Scope.Registry.TryResolve(context.Handle.Id, out _, out var publicError);
            PublicResolveError = publicError;
            ContextResolveSucceeded = context.TryResolve(context.Handle.Id, out _, out var contextError) && contextError.IsNone;
            error = default;
            return true;
        }
        public override bool Prepare(out EntityError error) { PrepareCount++; error = default; return true; }
    }
}
