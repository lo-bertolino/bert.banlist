using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bert.Banlist.VisualStudio
{
    /// <summary>
    /// Finds the project directory that owns a document by walking up the directory tree.
    ///
    /// Preferred over the workspace query API because it depends only on the active document's path:
    /// whether <c>GetActiveProjectAsync</c> follows the editor or the Solution Explorer selection
    /// varies with how the command was invoked, and the ban list is per project by design.
    /// </summary>
    public static class ProjectLocator
    {
        private static readonly string[] ProjectExtensions = { "*.csproj", "*.vbproj", "*.fsproj" };

        /// <summary>
        /// Returns the nearest ancestor directory of <paramref name="documentFilePath"/> containing a
        /// project file, or null. <paramref name="enumerateProjectFiles"/> lists the project files in
        /// a directory — injected so the walk is testable without touching the file system.
        /// </summary>
        public static string? FindProjectDirectory(
            string? documentFilePath,
            Func<string, IEnumerable<string>> enumerateProjectFiles)
        {
            if (string.IsNullOrWhiteSpace(documentFilePath))
            {
                return null;
            }

            string? directory;
            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(documentFilePath!));
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }

            while (!string.IsNullOrEmpty(directory))
            {
                if (enumerateProjectFiles(directory!).Any())
                {
                    return directory;
                }

                var parent = Path.GetDirectoryName(directory!);
                if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                directory = parent;
            }

            return null;
        }

        /// <summary>File-system-backed overload used by the command.</summary>
        public static string? FindProjectDirectory(string? documentFilePath)
            => FindProjectDirectory(documentFilePath, EnumerateProjectFiles);

        private static IEnumerable<string> EnumerateProjectFiles(string directory)
        {
            foreach (var pattern in ProjectExtensions)
            {
                string[] matches;
                try
                {
                    matches = Directory.GetFiles(directory, pattern);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var match in matches)
                {
                    yield return match;
                }
            }
        }
    }
}
