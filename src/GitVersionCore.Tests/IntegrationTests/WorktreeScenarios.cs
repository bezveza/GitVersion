using GitTools.Testing;
using LibGit2Sharp;
using NUnit.Framework;
using System.IO;
using GitVersionCore.Tests.Helpers;

namespace GitVersionCore.Tests.IntegrationTests
{

    [TestFixture]
    public class WorktreeScenarios : TestBase
    {

        [Test]
        [Category("NoMono")]
        [Description("LibGit2Sharp fails here when running under Mono")]
        public void UseWorktreeRepositoryForVersion()
        {
            using var fixture = new EmptyRepositoryFixture();
            var repoDir = new DirectoryInfo(fixture.RepositoryPath);
            var worktreePath = Path.Combine(repoDir.Parent.FullName, $"{repoDir.Name}-v1");

            fixture.Repository.MakeATaggedCommit("v1.0.0");
            var branchV1 = fixture.Repository.CreateBranch("support/1.0");

            fixture.Repository.MakeATaggedCommit("v2.0.0");
            fixture.AssertFullSemver("2.0.0");

            fixture.Repository.Worktrees.Add(branchV1.CanonicalName, "1.0", worktreePath, false);
            using var worktreeFixture = new LocalRepositoryFixture(new Repository(worktreePath));
            worktreeFixture.AssertFullSemver("1.0.0");
        }

    }
}
