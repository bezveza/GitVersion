using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GitVersion.Common;
using GitVersion.Configuration;
using GitVersion.Extensions;
using GitVersion.Logging;
using LibGit2Sharp;

namespace GitVersion
{
    public class GitRepoMetadataProvider : IGitRepoMetadataProvider
    {
        private readonly Dictionary<Branch, List<BranchCommit>> mergeBaseCommitsCache = new Dictionary<Branch, List<BranchCommit>>();
        private readonly Dictionary<Tuple<Branch, Branch>, MergeBaseData> mergeBaseCache = new Dictionary<Tuple<Branch, Branch>, MergeBaseData>();
        private readonly Dictionary<Branch, List<SemanticVersion>> semanticVersionTagsOnBranchCache = new Dictionary<Branch, List<SemanticVersion>>();
        private const string MissingTipFormat = "{0} has no tip. Please see http://example.com/docs for information on how to fix this.";

        private readonly ILog log;
        private IRepository repository;

        public GitRepoMetadataProvider(ILog log)
        {
            this.log = log;
        }

        public void WithData(IRepository repo)
        {
            repository = repo;
        }

        public IEnumerable<Tuple<Tag, SemanticVersion>> GetValidVersionTags(string tagPrefixRegex, DateTimeOffset? olderThan = null)
        {
            var tags = new List<Tuple<Tag, SemanticVersion>>();

            foreach (var tag in repository.Tags)
            {
                if (!(tag.PeeledTarget() is Commit commit) || (olderThan.HasValue && commit.When() > olderThan.Value))
                    continue;

                if (SemanticVersion.TryParse(tag.FriendlyName, tagPrefixRegex, out var semver))
                {
                    tags.Add(Tuple.Create(tag, semver));
                }
            }

            return tags;
        }

        public IEnumerable<SemanticVersion> GetVersionTagsOnBranch(Branch branch, string tagPrefixRegex)
        {
            if (semanticVersionTagsOnBranchCache.ContainsKey(branch))
            {
                log.Debug($"Cache hit for version tags on branch '{branch.CanonicalName}");
                return semanticVersionTagsOnBranchCache[branch];
            }

            using (log.IndentLog($"Getting version tags from branch '{branch.CanonicalName}'."))
            {
                var tags = GetValidVersionTags(tagPrefixRegex);

                var versionTags = branch.Commits.SelectMany(c => tags.Where(t => c.Sha == t.Item1.Target.Sha).Select(t => t.Item2)).ToList();

                semanticVersionTagsOnBranchCache.Add(branch, versionTags);
                return versionTags;
            }
        }

        // TODO Should we cache this?
        public IEnumerable<Branch> GetBranchesContainingCommit(Commit commit, IEnumerable<Branch> branches, bool onlyTrackedBranches)
        {
            if (commit == null)
            {
                throw new ArgumentNullException(nameof(commit));
            }

            using (log.IndentLog($"Getting branches containing the commit '{commit.Id}'."))
            {
                var directBranchHasBeenFound = false;
                log.Info("Trying to find direct branches.");
                // TODO: It looks wasteful looping through the branches twice. Can't these loops be merged somehow? @asbjornu
                var branchList = branches as Branch[] ?? branches.ToArray();
                foreach (var branch in branchList)
                {
                    if (branch.Tip != null && branch.Tip.Sha != commit.Sha || ((onlyTrackedBranches && branch.IsTracking) || !onlyTrackedBranches))
                    {
                        continue;
                    }

                    directBranchHasBeenFound = true;
                    log.Info($"Direct branch found: '{branch.FriendlyName}'.");
                    yield return branch;
                }

                if (directBranchHasBeenFound)
                {
                    yield break;
                }

                log.Info($"No direct branches found, searching through {(onlyTrackedBranches ? "tracked" : "all")} branches.");
                foreach (var branch in branchList.Where(b => (onlyTrackedBranches && b.IsTracking) || !onlyTrackedBranches))
                {
                    log.Info($"Searching for commits reachable from '{branch.FriendlyName}'.");

                    var commits = repository.GetCommitsReacheableFrom(commit, branch);

                    if (!commits.Any())
                    {
                        log.Info($"The branch '{branch.FriendlyName}' has no matching commits.");
                        continue;
                    }

                    log.Info($"The branch '{branch.FriendlyName}' has a matching commit.");
                    yield return branch;
                }
            }
        }


        /// <summary>
        /// Find the merge base of the two branches, i.e. the best common ancestor of the two branches' tips.
        /// </summary>
        public Commit FindMergeBase(Branch branch, Branch otherBranch)
        {
            var key = Tuple.Create(branch, otherBranch);

            if (mergeBaseCache.ContainsKey(key))
            {
                log.Debug($"Cache hit for merge base between '{branch.FriendlyName}' and '{otherBranch.FriendlyName}'.");
                return mergeBaseCache[key].MergeBase;
            }

            using (log.IndentLog($"Finding merge base between '{branch.FriendlyName}' and '{otherBranch.FriendlyName}'."))
            {
                // Otherbranch tip is a forward merge
                var commitToFindCommonBase = otherBranch.Tip;
                var commit = branch.Tip;
                if (otherBranch.Tip.Parents.Contains(commit))
                {
                    commitToFindCommonBase = otherBranch.Tip.Parents.First();
                }

                var findMergeBase = repository.ObjectDatabase.FindMergeBase(commit, commitToFindCommonBase);
                if (findMergeBase != null)
                {
                    log.Info($"Found merge base of {findMergeBase.Sha}");
                    // We do not want to include merge base commits which got forward merged into the other branch
                    Commit forwardMerge;
                    do
                    {
                        // Now make sure that the merge base is not a forward merge
                        forwardMerge = repository.GetForwardMerge(commitToFindCommonBase, findMergeBase);

                        if (forwardMerge != null)
                        {
                            // TODO Fix the logging up in this section
                            var second = forwardMerge.Parents.First();
                            log.Debug("Second " + second.Sha);
                            var mergeBase = repository.ObjectDatabase.FindMergeBase(commit, second);
                            if (mergeBase == null)
                            {
                                log.Warning("Could not find mergbase for " + commit);
                            }
                            else
                            {
                                log.Debug("New Merge base " + mergeBase.Sha);
                            }
                            if (mergeBase == findMergeBase)
                            {
                                log.Debug("Breaking");
                                break;
                            }
                            findMergeBase = mergeBase;
                            commitToFindCommonBase = second;
                            log.Info($"Merge base was due to a forward merge, next merge base is {findMergeBase}");
                        }
                    } while (forwardMerge != null);
                }

                // Store in cache.
                mergeBaseCache.Add(key, new MergeBaseData(findMergeBase));

                log.Info($"Merge base of {branch.FriendlyName}' and '{otherBranch.FriendlyName} is {findMergeBase}");
                return findMergeBase;
            }
        }

        /// <summary>
        /// Find the commit where the given branch was branched from another branch.
        /// If there are multiple such commits and branches, tries to guess based on commit histories.
        /// </summary>
        public BranchCommit FindCommitBranchWasBranchedFrom(Branch branch, Config configuration, params Branch[] excludedBranches)
        {
            if (branch == null)
            {
                throw new ArgumentNullException(nameof(branch));
            }

            using (log.IndentLog($"Finding branch source of '{branch.FriendlyName}'"))
            {
                if (branch.Tip == null)
                {
                    log.Warning(string.Format(MissingTipFormat, branch.FriendlyName));
                    return BranchCommit.Empty;
                }

                var possibleBranches = GetMergeCommitsForBranch(branch, configuration, excludedBranches)
                    .Where(b => !branch.IsSameBranch(b.Branch))
                    .ToList();

                if (possibleBranches.Count > 1)
                {
                    var first = possibleBranches.First();
                    log.Info($"Multiple source branches have been found, picking the first one ({first.Branch.FriendlyName}).{System.Environment.NewLine}" +
                        $"This may result in incorrect commit counting.{System.Environment.NewLine}Options were:{System.Environment.NewLine}" +
                        string.Join(", ", possibleBranches.Select(b => b.Branch.FriendlyName)));
                    return first;
                }

                return possibleBranches.SingleOrDefault();
            }
        }

        private IEnumerable<BranchCommit> GetMergeCommitsForBranch(Branch branch, Config configuration, IEnumerable<Branch> excludedBranches)
        {
            if (mergeBaseCommitsCache.ContainsKey(branch))
            {
                log.Debug($"Cache hit for getting merge commits for branch {branch.CanonicalName}.");
                return mergeBaseCommitsCache[branch];
            }

            var currentBranchConfig = configuration.GetConfigForBranch(branch.NameWithoutRemote());
            var regexesToCheck = currentBranchConfig == null
                ? new[] { ".*" } // Match anything if we can't find a branch config
                : currentBranchConfig.SourceBranches.Select(sb => configuration.Branches[sb].Regex);
            var branchMergeBases = repository.Branches
                .ExcludingBranches(excludedBranches)
                .Where(b =>
                {
                    if (b == branch) return false;
                    var branchCanBeMergeBase = regexesToCheck.Any(regex => Regex.IsMatch(b.FriendlyName, regex));

                    return branchCanBeMergeBase;
                })
                .Select(otherBranch =>
                {
                    if (otherBranch.Tip == null)
                    {
                        log.Warning(string.Format(MissingTipFormat, otherBranch.FriendlyName));
                        return BranchCommit.Empty;
                    }

                    var findMergeBase = FindMergeBase(branch, otherBranch);
                    return new BranchCommit(findMergeBase, otherBranch);
                })
                .Where(b => b.Commit != null)
                .OrderByDescending(b => b.Commit.Committer.When)
                .ToList();
            mergeBaseCommitsCache.Add(branch, branchMergeBases);

            return branchMergeBases;
        }

        private class MergeBaseData
        {
            public Commit MergeBase { get; }

            public MergeBaseData(Commit mergeBase)
            {
                MergeBase = mergeBase;
            }
        }
    }
}