using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using HMProtection.Entities;
using HMProtection.EntityAdapters;
using HMProtection.Sessions;

namespace HMProtection.Scripting
{
    public static class ScriptEntityToken
    {
        public static string Encode(EntityHandle handle) => handle.ScopeId + ":" + handle.Generation.ToString(CultureInfo.InvariantCulture) + ":" + handle.Id.Value;
        public static bool TryDecode(string token, out EntityHandle handle)
        {
            handle = default;
            if (string.IsNullOrEmpty(token)) return false;
            var parts = token.Split(new[] { ':' }, 3);
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[2]) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int generation) || generation <= 0) return false;
            handle = new EntityHandle(parts[0], new EntityId(parts[2]), generation);
            return true;
        }
    }

    /// <summary>One service per level run. Each Lua VM/script gets its own disposable resource owner.</summary>
    public sealed class ComponentScriptService : IDisposable
    {
        internal readonly LevelSession Session;
        internal readonly LevelSceneBindings Bindings;
        internal readonly int MainThread;
        internal LuaComponentApi FlowOwner;
        readonly HashSet<LuaComponentApi> clients = new HashSet<LuaComponentApi>();
        readonly ComponentEventRelay relay;
        CancellationTokenRegistration stopping;
        bool disposed;
        public ComponentScriptService(LevelSession session, LevelSceneBindings bindings = null)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            if (session.State != LevelSessionState.Running) throw new InvalidOperationException("A running session is required.");
            Bindings = bindings; MainThread = Thread.CurrentThread.ManagedThreadId;
            relay = new ComponentEventRelay(session);
            stopping = session.Lifetime.Register(Dispose);
        }
        public LuaComponentApi CreateClient(string scriptId)
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThread) throw new InvalidOperationException("CreateClient must run on the Unity thread.");
            if (disposed || Session.State != LevelSessionState.Running) throw new ObjectDisposedException(nameof(ComponentScriptService));
            if (string.IsNullOrWhiteSpace(scriptId) || scriptId.Length > 128) throw new ArgumentException("Script ID must contain 1-128 characters.");
            if (clients.Count >= 64) throw new InvalidOperationException("At most 64 script clients may be active in a session.");
            var client = new LuaComponentApi(this, scriptId);
            clients.Add(client); return client;
        }
        internal void Forget(LuaComponentApi client) => clients.Remove(client);
        public void Tick() { if (!disposed && Session.State == LevelSessionState.Running) relay.Tick(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var client in new List<LuaComponentApi>(clients)) client.Dispose();
            clients.Clear(); relay.Dispose(); stopping.Dispose();
        }
    }
}
