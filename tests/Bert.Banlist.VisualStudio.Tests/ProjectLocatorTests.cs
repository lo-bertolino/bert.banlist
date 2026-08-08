using System;
using System.Collections.Generic;
using System.Linq;
using Bert.Banlist.VisualStudio;
using Xunit;

namespace Bert.Banlist.VisualStudio.Tests
{
    public class ProjectLocatorTests
    {
        private static Func<string, IEnumerable<string>> ProjectsIn(params string[] directories)
        {
            var set = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
            return directory => set.Contains(directory.TrimEnd('\\'))
                ? new[] { directory + "\\Project.csproj" }
                : Enumerable.Empty<string>();
        }

        [Fact]
        public void FindsTheNearestAncestorHoldingAProjectFile()
        {
            var result = ProjectLocator.FindProjectDirectory(
                @"C:\repo\src\App\Features\Thing.cs",
                ProjectsIn(@"C:\repo\src\App", @"C:\repo"));

            Assert.Equal(@"C:\repo\src\App", result);
        }

        [Fact]
        public void FindsAProjectInTheDocumentsOwnDirectory()
        {
            var result = ProjectLocator.FindProjectDirectory(
                @"C:\repo\src\App\Thing.cs",
                ProjectsIn(@"C:\repo\src\App"));

            Assert.Equal(@"C:\repo\src\App", result);
        }

        [Fact]
        public void ReturnsNullWhenNoProjectExistsAllTheWayToTheRoot()
        {
            var result = ProjectLocator.FindProjectDirectory(
                @"C:\scratch\Thing.cs",
                ProjectsIn());

            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ReturnsNullForAMissingDocumentPath(string? path)
        {
            Assert.Null(ProjectLocator.FindProjectDirectory(path, ProjectsIn(@"C:\repo")));
        }

        [Fact]
        public void StopsAtTheDriveRootInsteadOfLooping()
        {
            var visited = new List<string>();
            var result = ProjectLocator.FindProjectDirectory(
                @"C:\a\b\c\Thing.cs",
                directory =>
                {
                    visited.Add(directory);
                    return Enumerable.Empty<string>();
                });

            Assert.Null(result);
            Assert.Equal(new[] { @"C:\a\b\c", @"C:\a\b", @"C:\a", @"C:\" }, visited);
        }
    }
}
