# Build Notes — Non-SDK .csproj on .NET Framework 4.8

This project uses an **old-style (non-SDK) `.csproj`** targeting .NET Framework 4.8.
That format was designed for MSBuild/Visual Studio. Building with `dotnet build` works
but requires explicit fixes that VS handles silently. These notes document the three
issues discovered when getting the tool running on a second machine.

---

## Issue 1: Transitive NuGet dependencies not copied by `dotnet build`

**Symptom:** App crashes on the target machine with `FileNotFoundException` for a DLL
that is present on the dev machine's output folder but was never explicitly referenced.

**Cause:** Old-style `.csproj` + `dotnet build` only copies *direct* NuGet package DLLs
to the output folder. VS and MSBuild walk the full transitive closure and copy everything.
`0Harmony.dll`, Roslyn internals, and other transitive deps are silently handled by VS
but silently dropped by `dotnet build`.

**Fix:** Add to the relevant `<PropertyGroup>` in `.csproj`:

```xml
<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
```

This tells `dotnet build` to copy all lock-file assemblies (the full transitive set) to
the output folder, matching VS behaviour.

---

## Issue 2: `<PackageReference>` DLLs not on the compile path with `dotnet build`

**Symptom:** Build succeeds in VS but fails with `dotnet build` — compiler cannot find
types from NuGet packages (e.g. `ScintillaNET`, `Microsoft.CodeAnalysis`).

**Cause:** In old-style `.csproj`, `<PackageReference>` is handled differently than in
SDK-style projects. VS resolves the package and adds the DLL to the compile path
automatically. `dotnet build` restores the package but does not inject the DLL into the
compiler invocation unless there is also an explicit `<Reference>` with a `<HintPath>`.

**Fix:** For each `<PackageReference>` that must compile, add a matching `<Reference>`:

```xml
<PackageReference Include="Scintilla.NET" Version="5.3.2.9" />
<Reference Include="Scintilla.NET">
  <HintPath>$(NuGetPackageRoot)scintilla.net\5.3.2.9\lib\net45\Scintilla.NET.dll</HintPath>
</Reference>
```

---

## Issue 3: WinForms `.resx` with embedded images fails in non-VS pipelines

**Symptom:** Build error or runtime `SerializationException` / `FileNotFoundException`
when loading the main form — resources with embedded bitmaps or icons fail to
deserialize.

**Cause:** .NET Framework's default `ResXResourceWriter` cannot handle binary resources
(icons, bitmaps) through the newer `System.Resources.Extensions` pipeline. MSBuild
needs two things simultaneously:

1. The MSBuild flag `GenerateResourceUsePreserializedResources=true`
2. An explicit assembly reference to `System.Resources.Extensions` — without it MSBuild
   picks up the wrong serializer at build time even with the flag set.

**Fix:**

```xml
<!-- In the relevant <PropertyGroup> -->
<GenerateResourceUsePreserializedResources>true</GenerateResourceUsePreserializedResources>

<!-- In <ItemGroup> references -->
<Reference Include="System.Resources.Extensions">
  <HintPath>$(NuGetPackageRoot)system.resources.extensions\...\System.Resources.Extensions.dll</HintPath>
</Reference>
```

---

## Why these issues only appear on the second machine

Visual Studio abstracts all three of these details invisibly:

| Behaviour | Visual Studio | `dotnet build` on non-SDK `.csproj` |
|---|---|---|
| Transitive dep copy | Automatic | Requires `CopyLocalLockFileAssemblies=true` |
| PackageReference on compile path | Automatic | Requires explicit `<Reference>` + `<HintPath>` |
| Binary `.resx` serializer | Automatic | Requires flag + explicit assembly reference |

The dev machine never surfaced these because VS was the only build tool used.
The second machine (no VS, using `dotnet build` or VS Code) exposed all three at once.

---

## Summary of fixes applied to this project

| Fix | File changed |
|---|---|
| `Prefer32Bit=false` — run as 64-bit to match x64 Scintilla native DLLs | `.csproj` |
| `CopyScintillaNativeDlls` AfterBuild MSBuild target | `.csproj` |
| `0Harmony.dll` removed from `setup.ps1` SE DLL list (not in ModSDK, copied transitively) | `setup.ps1` |
