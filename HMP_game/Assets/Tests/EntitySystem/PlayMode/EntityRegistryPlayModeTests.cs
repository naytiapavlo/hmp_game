using System.Collections;
using HMProtection.Entities;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace HMProtection.EntitySystem.PlayModeTests
{
    public sealed class EntityRegistryPlayModeTests
    {
        [UnityTest]
        public IEnumerator DestroyedEntityIsPurgedAfterAFrameAndItsHandleCannotBeUsed()
        {
            var root = new GameObject("entity-test-root");
            var target = new GameObject("destroyed-target");
            target.transform.SetParent(root.transform, false);
            var entity = target.AddComponent<GameEntity>();
            entity.Configure("destroyed.target");
            var scope = new EntityScope("destroy");
            Assert.That(scope.TryInitialize(null, new[] { root.transform }, out var error), Is.True, error.ToString());
            Assert.That(scope.Registry.TryResolve("destroyed.target", out var handle, out error), Is.True, error.ToString());

            Object.Destroy(target);
            yield return null;

            Assert.That(scope.Registry.IsValid(handle), Is.False);
            Assert.That(scope.Registry.TryResolve("destroyed.target", out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.EntityNotFound));
            scope.Dispose();
            Object.Destroy(root);
        }

        [UnityTest]
        public IEnumerator InitiallyInactiveDynamicEntityCanBeEnabledUnregisteredAndDestroyed()
        {
            var root = new GameObject("dynamic-root");
            var scope = new EntityScope("dynamic");
            Assert.That(scope.TryInitialize(null, new[] { root.transform }, out var error), Is.True, error.ToString());
            var facade = new EntityFacade(scope);
            var target = new GameObject("dynamic-target");
            target.SetActive(false);
            var entity = target.AddComponent<GameEntity>();
            entity.Configure("dynamic.target", new[] { "runtime" });

            Assert.That(scope.Registry.TryRegister(entity, out var handle, out error), Is.True, error.ToString());
            Assert.That(facade.TrySetActive(handle, true, out error), Is.True, error.ToString());
            Assert.That(scope.Registry.TryUnregister(handle, out error), Is.True, error.ToString());
            Object.Destroy(target);
            yield return null;

            Assert.That(scope.Registry.IsValid(handle), Is.False);
            Assert.That(scope.Registry.TryResolve("dynamic.target", out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.EntityNotFound));
            scope.Dispose();
            Object.Destroy(root);
        }

        [UnityTest]
        public IEnumerator InitiallyInactiveRegisteredEntityIsPurgedWhenDestroyed()
        {
            var root = new GameObject("inactive-destroy-root");
            var target = new GameObject("inactive-destroy-target");
            target.transform.SetParent(root.transform, false);
            var entity = target.AddComponent<GameEntity>();
            entity.Configure("inactive.destroyed");
            target.SetActive(false);
            var scope = new EntityScope("inactive-destroy");
            Assert.That(scope.TryInitialize(null, new[] { root.transform }, out var error), Is.True, error.ToString());
            Assert.That(scope.Registry.TryResolve("inactive.destroyed", out var handle, out error), Is.True, error.ToString());

            Object.Destroy(target);
            yield return null;

            Assert.That(scope.Registry.IsValid(handle), Is.False);
            Assert.That(scope.Registry.TryResolve("inactive.destroyed", out _, out error), Is.False);
            Assert.That(error.Code, Is.EqualTo(EntityErrorCode.EntityNotFound));
            scope.Dispose();
            Object.Destroy(root);
        }

        [UnityTest]
        public IEnumerator ScopeHostDisposesItsScopeWhenOwningSceneIsUnloaded()
        {
            var scene = SceneManager.CreateScene("entity-scope-unload-test");
            var root = new GameObject("content-root");
            var entityObject = new GameObject("scene-entity");
            var hostObject = new GameObject("scope-host");
            SceneManager.MoveGameObjectToScene(root, scene);
            SceneManager.MoveGameObjectToScene(entityObject, scene);
            SceneManager.MoveGameObjectToScene(hostObject, scene);
            entityObject.transform.SetParent(root.transform, false);
            entityObject.AddComponent<GameEntity>().Configure("scene.entity");
            var host = hostObject.AddComponent<EntityScopeHost>();
            host.Configure("unload", new[] { root.transform });
            Assert.That(host.TryInitialize("unload", out var error), Is.True, error.ToString());
            var scope = host.Scope;

            var unload = SceneManager.UnloadSceneAsync(scene);
            while (!unload.isDone) yield return null;
            yield return null;

            Assert.That(scope.IsDisposed, Is.True);
        }
    }
}
