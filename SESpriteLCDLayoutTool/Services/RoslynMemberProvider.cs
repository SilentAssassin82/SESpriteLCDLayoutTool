using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SESpriteLCDLayoutTool.Services
{
    /// <summary>
    /// Resolves public members of SE API types by reflecting over assemblies that
    /// are already loaded (or discoverable via the DLL paths used by SyntaxHighlighter).
    /// Results are cached permanently — reflection is only done once per type name.
    /// </summary>
    internal static class RoslynMemberProvider
    {
        private static readonly Dictionary<string, string[]> _cache =
            new Dictionary<string, string[]>(StringComparer.Ordinal);

        private static readonly object _lock = new object();

        /// <summary>
        /// Returns display members (properties, fields, methods) for the given simple
        /// type name (e.g. "IMyThrust", "MySprite", "Vector2").
        /// Returns null if the type cannot be found in any loaded assembly.
        /// </summary>
        public static string[] GetMembers(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;

            lock (_lock)
            {
                if (_cache.TryGetValue(typeName, out var cached))
                    return cached;

                var result = ResolveFromAssemblies(typeName);
                _cache[typeName] = result; // null is a valid cache entry (type not found)
                return result;
            }
        }

        private static string[] ResolveFromAssemblies(string simpleName)
        {
            // Search all assemblies currently loaded in the AppDomain.
            // SyntaxHighlighter loads SE DLLs as MetadataReference (not into the AppDomain),
            // so we search what IS loaded — the tool's own references plus anything loaded at startup.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;

                try
                {
                    // Try every exported type whose simple name matches
                    foreach (var type in asm.GetExportedTypes())
                    {
                        if (type.Name != simpleName) continue;
                        return ExtractMembers(type);
                    }
                }
                catch { /* skip inaccessible assemblies */ }
            }
            return null;
        }

        private static string[] ExtractMembers(Type type)
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

            var members = new HashSet<string>(StringComparer.Ordinal);

            // Properties
            foreach (var p in type.GetProperties(flags))
            {
                if (!IsHiddenMember(p.Name))
                    members.Add(p.Name);
            }

            // Fields (public only — captures enum values and static constants)
            foreach (var f in type.GetFields(flags))
            {
                if (!IsHiddenMember(f.Name))
                    members.Add(f.Name);
            }

            // Methods — show with () suffix, skip operator overloads and property accessors
            foreach (var m in type.GetMethods(flags))
            {
                if (m.IsSpecialName) continue; // get_, set_, op_, etc.
                if (IsHiddenMember(m.Name)) continue;
                members.Add(m.Name + "()");
            }

            if (members.Count == 0) return null;

            var list = members.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        private static bool IsHiddenMember(string name)
        {
            // Skip noisy infrastructure members that aren't useful in autocomplete
            return name == "GetType"
                || name == "GetHashCode"
                || name == "Equals"
                || name == "ToString"
                || name == "MemberwiseClone"
                || name == "Finalize"
                || name == "ReferenceEquals"
                || name.StartsWith("__", StringComparison.Ordinal);
        }
    }
}
