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
        public static bool IsSubdirectoryOf(this string childPath, string parentPath)
        {
            // Get the full paths to ensure consistency
            var parentFullPath = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var childFullPath = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Check if the child path starts with the parent path
            return childFullPath.StartsWith(parentFullPath, StringComparison.OrdinalIgnoreCase);
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
