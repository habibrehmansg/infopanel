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
        /// <summary>The most links <see cref="ResolveRealPath"/> follows before it gives up on a loop.</summary>
        private const int MaxLinkHops = 64;

        /// <summary>
        /// The path with every symbolic link in it (the folder itself and each ancestor) replaced by its final target,
        /// as <c>realpath</c> does; components that don't exist yet are kept as they are. Resolved one component at a
        /// time in the order the filesystem does: a link is followed before the <c>..</c> after it applies, so
        /// <c>..</c> leaves the link's target, not the folder that holds the link. That is Unix's order; on Windows the
        /// path and each link's target are made full first, which collapses <c>..</c> as Windows itself does. After a
        /// component that doesn't exist, <c>..</c> removes it.
        /// </summary>
        /// <exception cref="IOException">The links loop (more than 64 followed).</exception>
        public static string ResolveRealPath(string path)
        {
            // Windows collapses ".." in a path (a link's target included) before the filesystem sees it; Unix resolves
            // each ".." in the filesystem, against the real folder it is reached from.
            var windows = OperatingSystem.IsWindows();
            var start = windows ? Path.GetFullPath(path) : Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
            var root = Path.GetPathRoot(start) ?? "";
            var current = root;
            var pending = new Stack<string>(Components(start[root.Length..]).Reverse());
            var hops = 0;
            var exists = true; // whether current exists, so its children may be links
            while (pending.Count > 0)
            {
                var part = pending.Pop();
                if (part == ".")
                    continue;
                if (part == "..")
                {
                    current = Path.GetDirectoryName(current) ?? root;
                    exists = exists || Directory.Exists(current);
                    continue;
                }

                var next = Path.Combine(current, part);
                var target = exists ? LinkTargetOf(Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next)) : null;
                if (target == null)
                {
                    current = next;
                    exists = exists && Path.Exists(next);
                    continue;
                }

                if (++hops > MaxLinkHops)
                    throw new IOException($"Too many levels of symbolic links in '{path}'.");
                // The target's components come next: from its root when rooted, else from the folder that holds the link.
                if (windows)
                    target = Path.GetFullPath(target, current);
                if (Path.IsPathRooted(target))
                {
                    root = Path.GetPathRoot(target) ?? "";
                    current = root;
                    target = target[root.Length..];
                }

                foreach (var component in Components(target).Reverse())
                    pending.Push(component);
            }

            return Path.TrimEndingDirectorySeparator(current) is { Length: > 0 } trimmed ? trimmed : current;
        }

        private static string[] Components(string path) =>
            path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

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
