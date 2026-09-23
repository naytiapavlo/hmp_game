using System;
using System.Threading;
using HMProtection.EntityAdapters;
using HMProtection.LuaRuntime;

namespace HMProtection.Scripting
{
    /// <summary>One VM and one API client per running level; owns all script-created resources.</summary>
    public sealed class LevelLuaRunner : IDisposable
    {
        readonly LevelSessionHost host;
        LuaComponentApi api;
        LevelLuaVm vm;
        CancellationTokenRegistration lifetime;
        bool disposed;
        public bool IsRunning => !disposed && vm != null && !vm.IsFaulted && host != null && host.IsRunning;
        public string LastError { get; private set; }
        public int UpdateCount { get; private set; }
        LevelLuaRunner(LevelSessionHost owner) { host = owner; }

        public static bool TryCreate(LevelSessionHost host, LevelDefinition definition, out LevelLuaRunner runner, out string error)
        {
            runner = null; error = null;
            if (host == null || !host.IsRunning || host.Scripts == null || definition == null)
            { error = "Lua requires a prepared level session and definition."; return false; }
            var created = new LevelLuaRunner(host);
            try
            {
                created.api = host.Scripts.CreateClient(definition.levelId + ".lua");
                if (!LevelLuaVm.TryCreate(definition.luaApiSource, definition.luaSource, definition.levelId + "/" + definition.scriptEntry,
                    created.api.Call, out created.vm, out error))
                { created.LastError = error; created.Dispose(); return false; }
                created.lifetime = host.Session.Lifetime.Register(created.Dispose);
                runner = created; return true;
            }
            catch (Exception exception)
            { error = exception.Message; created.LastError = error; created.Dispose(); return false; }
        }
        public bool Tick(float delta, float unscaled, out string error)
        {
            error = LastError;
            if (!IsRunning) return false;
            if (!vm.Tick(delta, unscaled, out error))
            { LastError = error; Dispose(); return false; }
            UpdateCount++; return true;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                vm?.Dispose();
                if (!string.IsNullOrEmpty(vm?.LastError)) LastError = vm.LastError;
            }
            finally { api?.Dispose(); lifetime.Dispose(); vm = null; api = null; }
        }
    }
}
