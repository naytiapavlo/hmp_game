using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace HMProtection.LuaRuntime.Tests
{
    public sealed class LevelLuaVmTests
    {
        const string Shared = "return {}";
        static string Ok(string method, string args) => "{\"apiVersion\":1,\"ok\":true,\"code\":\"ok\",\"error\":\"\",\"data\":{}}";

        [Test]
        public void RealLuaSupportsClosuresTablesChineseJsonAndEmptyArrays()
        {
            const string entry = "return { new=function(host,json) local v=json.decode('{\\\"中文\\\":\\\"值\\\",\\\"empty\\\":[]}'); assert(v['中文']=='值'); assert(json.encode(v.empty)=='[]'); return { prefix='中', fn=function(self) return self.prefix..'文' end, max=64 } end, update=function(self,d,u) assert(self.fn(self)=='中文'); assert(self.max==64) end, stop=function(self) end }";
            Assert.That(LevelLuaVm.TryCreate(Shared, entry, "json-test", Ok, out var vm, out var error), Is.True, error);
            using (vm) Assert.That(vm.Tick(.01f, .02f, out error), Is.True, error);
        }

        [Test]
        public void JsonEncodeWholeIntegerReachesHostAsIntegerToken()
        {
            string payload = null;
            const string entry = "return { new=function(host,json) host:Call('capture',json.encode({max=64})); return {} end, update=function(self,d,u) end, stop=function(self) end }";
            Assert.That(LevelLuaVm.TryCreate(Shared, entry, "integer-json", (method, args) => { payload = args; return Ok(method, args); }, out var vm, out var error), Is.True, error);
            using (vm)
            {
                var value = JObject.Parse(payload);
                Assert.That(value["max"].Type, Is.EqualTo(JTokenType.Integer));
                Assert.That((long)value["max"], Is.EqualTo(64L));
            }
        }

        [Test]
        public void JsonDecodeAcceptsLegalSeventyKilobyteHostResponse()
        {
            string response = "{\"text\":\"" + new string('x', 70000) + "\"}";
            const string entry = "return { new=function(host,json) local data=json.decode(host:Call('events.poll','{}')); assert(#data.text==70000); return {} end, update=function(self,d,u) end, stop=function(self) end }";
            Assert.That(LevelLuaVm.TryCreate(Shared, entry, "large-host-response", (method, args) => response, out var vm, out var error), Is.True, error);
            vm.Dispose();
        }

        [Test]
        public void SyntaxAndConstructorFailuresRejectCreation()
        {
            Assert.That(LevelLuaVm.TryCreate(Shared, "function (", "syntax", Ok, out _, out _), Is.False);
            Assert.That(LevelLuaVm.TryCreate(Shared, "while true do end", "top-loop", Ok, out _, out var topError), Is.False);
            Assert.That(topError, Does.Contain("budget").Or.Contain("yielded"));
            const string startupLoop = "return { new=function(host,json) while true do end end, update=function(self,d,u) end, stop=function(self) end }";
            Assert.That(LevelLuaVm.TryCreate(Shared, startupLoop, "startup-loop", Ok, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("budget").Or.Contain("yielded"));
        }

        [Test]
        public void UpdateAndPcallWrappedInfiniteLoopsFaultWithoutResume()
        {
            const string entry = "return { new=function(host,json) return {} end, update=function(self,d,u) local ok,err=pcall(function() while true do end end); assert(ok) end, stop=function(self) end }";
            Assert.That(LevelLuaVm.TryCreate(Shared, entry, "pcall-loop", Ok, out var vm, out var error), Is.True, error);
            using (vm)
            {
                Assert.That(vm.Tick(.01f, .01f, out error), Is.False);
                Assert.That(vm.IsFaulted, Is.True);
                Assert.That(error, Does.Contain("budget").Or.Contain("yielded"));
            }
        }

        [Test]
        public void StopInfiniteLoopIsBudgetedAndFaulted()
        {
            const string entry = "return { new=function(host,json) return {} end, update=function(self,d,u) end, stop=function(self) while true do end end }";
            Assert.That(LevelLuaVm.TryCreate(Shared, entry, "stop-loop", Ok, out var vm, out var error), Is.True, error);
            vm.Dispose();
            Assert.That(vm.IsFaulted, Is.True);
            Assert.That(vm.LastError, Does.Contain("budget").Or.Contain("yielded"));
        }
    }
}
