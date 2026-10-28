using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Extensions
{
    public static class PathExtensions
    {
        /// <summary>
        /// The path with every symbolic link in it (the folder itself and each ancestor) replaced by its final target,
        /// as <c>realpath</c> does; components that don't exist yet are kept as they are.
        /// </summary>
        /// <exception cref="IOException">The links loop.</exception>
        public static string ResolveRealPath(string path)
        {
            var full = Path.GetFullPath(path);
            for (var hops = 0; hops < 64; hops++)
            {
                var next = ResolveFirstLink(full);
                if (next == null)
                    return Path.TrimEndingDirectorySeparator(full) is { Length: > 0 } trimmed ? trimmed : full;
                full = next;
            }

            throw new IOException($"Too many levels of symbolic links in '{path}'.");
        }

        /// <summary>The path with its first link component (from the root down) replaced by the link's target, or null when none is a link.</summary>
        private static string? ResolveFirstLink(string full)
        {
            var root = Path.GetPathRoot(full) ?? "";
            var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                var target = LinkTargetOf(info);
                if (target == null && !info.Exists)
                    return null; // the rest doesn't exist yet
                if (target != null)
                {
                    // A relative target is relative to the folder that holds the link.
                    var resolved = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? root);
                    return Path.GetFullPath(Path.Combine([resolved, .. parts[(i + 1)..]]));
                }
            }

            return null;
        }

        /// <summary>The link's target as stored, or null when <paramref name="info"/> isn't a link (or is gone).</summary>
        public static string? LinkTargetOf(FileSystemInfo info)
        {
            try
            {
                return info.LinkTarget;
            }
            catch (IOException)
            {
                return null; // gone, or not readable as a link
            }
        }

        public static bool IsSubdirectoryOf(this string childPath, string parentPath)
        {
            // Get the full paths to ensure consistency
            var parentFullPath = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var childFullPath = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // The child must be the parent itself or start with the parent followed by a separator,
            // so "/assets/abc" is not treated as inside "/assets/ab".
            if (childFullPath.Equals(parentFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return childFullPath.StartsWith(parentFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || childFullPath.StartsWith(parentFullPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        public static string SanitizeFileName(this string fileName)
        {
            // Get invalid characters for file names
            char[] invalidChars = Path.GetInvalidFileNameChars();

            // Replace invalid characters with an underscore
            return string.Concat(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
        }
    }
}
