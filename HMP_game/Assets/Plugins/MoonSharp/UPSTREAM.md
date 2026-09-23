# MoonSharp vendored runtime

Upstream: https://github.com/moonsharp-devs/moonsharp
Release tag: v2.0.0.0
Commit: 3154416e535ab96c0d52bf12e3e472985a1532f4
License: BSD-3-Clause (see LICENSE.txt)

Only interpreter C# source is included. Excludes alternative project trees, debugger, tests, tools and binaries.
Local changes: Unity asmdef and metas; normalized UTF-8 source encoding; replace legacy UNITY_5 compile guards with UNITY_5_3_OR_NEWER for Unity 6 support.
Changed compile-guard files:
- Execution/VM/ByteCode.cs
- Loaders/UnityAssetsScriptLoader.cs
- Platforms/DotNetCorePlatformAccessor.cs
- Platforms/PlatformAutoDetector.cs
- Platforms/StandardPlatformAccessor.cs
