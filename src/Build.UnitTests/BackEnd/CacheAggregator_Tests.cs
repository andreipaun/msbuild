// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Shouldly;
using Xunit;
using static Microsoft.Build.Unittest.BuildResultUtilities;

namespace Microsoft.Build.UnitTests.BackEnd
{
    public class CacheAggregator_Tests
    {
        private CacheAggregator aggregator;

        public CacheAggregator_Tests()
        {
            var i = BuildRequestConfiguration.InvalidConfigurationId + 1;
            aggregator = new CacheAggregator(() => i++);
        }

        [Fact]
        public void NoCachesProducesEmptyCaches()
        {
            var aggregation = aggregator.Aggregate();

            aggregation.ConfigCache.ShouldNotBeNull();
            aggregation.ConfigCache.GetEnumerator().ToEnumerable().ShouldBeEmpty();

            aggregation.ResultsCache.ShouldNotBeNull();
            aggregation.ResultsCache.GetEnumerator().ToEnumerable().ShouldBeEmpty();

            aggregation.LastConfigurationId.ShouldBe(0);
        }

        [Fact]
        public void CannotCallAggregateTwice()
        {
            aggregator.Aggregate();

            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDDONOTLAUNCHDEBUGGER", "1");
                var e = Should.Throw<InternalErrorException>(() => aggregator.Aggregate());
                e.Message.ShouldContain("Cannot aggregate twice");
            }
        }

        [Fact]
        public void CannotAddAfterAggregation()
        {
            aggregator.Aggregate();

            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDDONOTLAUNCHDEBUGGER", "1");

                var e = Should.Throw<InternalErrorException>(() =>
                {
                    aggregator.Add(new ConfigCache(), new ResultsCache());
                });
                e.Message.ShouldContain("Cannot add after aggregation");
            }

        }

        [Fact]
        public void RejectCachesWithMoreConfigEntriesThanResultEntries()
        {
            var configCache = new ConfigCache();
            configCache.AddConfiguration(new BuildRequestConfiguration(configId: 1, new BuildRequestData("path", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"a", "b"}, null), "13"));
            configCache.AddConfiguration(new BuildRequestConfiguration(configId: 2, new BuildRequestData("path2", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"c", "d"}, null), "13"));

            var resultsCache = new ResultsCache();
            var buildResult = new BuildResult(new BuildRequest(1, 2, configurationId: 1, new List<string>(){"a", "b"}, null, BuildEventContext.Invalid, null));
            buildResult.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());
            resultsCache.AddResult(buildResult);

            aggregator.Add(configCache, resultsCache);

            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDDONOTLAUNCHDEBUGGER", "1");
                var e = Should.Throw<InternalErrorException>(() => aggregator.Aggregate());
                e.Message.ShouldContain("Assuming 1-to-1 mapping between configs and results. Otherwise it means the caches are either not minimal or incomplete");
            }
        }

        [Fact]
        public void RejectCachesWithMoreResultEntriesThanConfigEntries()
        {
            var configCache = new ConfigCache();
            configCache.AddConfiguration(new BuildRequestConfiguration(configId: 1, new BuildRequestData("path", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"a", "b"}, null), "13"));

            var resultsCache = new ResultsCache();
            var buildResult = new BuildResult(new BuildRequest(1, 2, configurationId: 1, new List<string>(){"a", "b"}, null, BuildEventContext.Invalid, null));
            buildResult.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());

            resultsCache.AddResult(buildResult);

            var buildResult2 = new BuildResult(new BuildRequest(1, 2, configurationId: 2, new List<string>(){"a", "b"}, null, BuildEventContext.Invalid, null));
            buildResult2.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());

            resultsCache.AddResult(buildResult2);

            aggregator.Add(configCache, resultsCache);

            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDDONOTLAUNCHDEBUGGER", "1");
                var e = Should.Throw<InternalErrorException>(() => aggregator.Aggregate());
                e.Message.ShouldContain("Assuming 1-to-1 mapping between configs and results. Otherwise it means the caches are either not minimal or incomplete");
            }
        }

        [Fact]
        public void RejectCachesWithMismatchedIds()
        {
            // one entry in each cache but different config ids

            var configCache = new ConfigCache();
            configCache.AddConfiguration(new BuildRequestConfiguration(configId: 1, new BuildRequestData("path", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"a", "b"}, null), "13"));

            var resultsCache = new ResultsCache();
            var buildResult = new BuildResult(new BuildRequest(1, 2, configurationId: 2, new List<string>(){"a", "b"}, null, BuildEventContext.Invalid, null));
            buildResult.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());
            resultsCache.AddResult(buildResult);

            aggregator.Add(configCache, resultsCache);

            using (var env = TestEnvironment.Create())
            {
                env.SetEnvironmentVariable("MSBUILDDONOTLAUNCHDEBUGGER", "1");
                var e = Should.Throw<InternalErrorException>(() => aggregator.Aggregate());
                e.Message.ShouldContain("Each result should have a corresponding configuration. Otherwise the caches are not consistent");
            }
        }

        [Theory]
        [InlineData(true, true, null)]
        [InlineData(true, false, "Input caches should not contain duplicate entries where only some are exempt from isolation constraints")]
        [InlineData(false, true, "Input caches should not contain duplicate entries where only some are exempt from isolation constraints")]
        [InlineData(false, false, "Input caches should not contain entries for the same configuration")]
        public void RejectCollidingConfigurationsFromSeparateCaches(bool config1IsExempt, bool config2IsExempt, string expectedErrorMessage)
        {
            // collides with the config id from configCache2
            var config1 = new BuildRequestConfiguration(1,
                new BuildRequestData(
                    projectFullPath: "path",
                    globalProperties: new Dictionary<string, string> {["p"] = "v"},
                    toolsVersion: "13",
                    targetsToBuild: new[] {"foo"},
                    hostServices: null), "13")
            {
                SkippedFromStaticGraphIsolationConstraints = config1IsExempt
            };

            var configCache1 = new ConfigCache();
            configCache1.AddConfiguration(config1);

            var resultsCache1 = new ResultsCache();
            var buildResult11 = new BuildResult(new BuildRequest(1, 2, configurationId: 1,
                new List<string>() {"foo"}, null, BuildEventContext.Invalid, null));
            buildResult11.AddResultsForTarget("foo", GetNonEmptySucceedingTargetResult());
            resultsCache1.AddResult(buildResult11);

            var config2 = new BuildRequestConfiguration(1,
                new BuildRequestData(
                    projectFullPath: "path",
                    globalProperties: new Dictionary<string, string> {["p"] = "v"},
                    toolsVersion: "13",
                    targetsToBuild: new[] {"bar"},
                    hostServices: null), "13")
            {
                SkippedFromStaticGraphIsolationConstraints = config2IsExempt
            };

            var configCache2 = new ConfigCache();
            configCache2.AddConfiguration(config2);

            var resultsCache2 = new ResultsCache();
            var buildResult21 = new BuildResult(new BuildRequest(1, 2, configurationId: 1,
                new List<string>() {"e", "f"}, null, BuildEventContext.Invalid, null));
            buildResult21.AddResultsForTarget("bar", GetNonEmptySucceedingTargetResult());
            resultsCache2.AddResult(buildResult21);

            aggregator.Add(configCache1, resultsCache1);
            aggregator.Add(configCache2, resultsCache2);

            if (expectedErrorMessage == null)
            {
                var aggregatedCaches = aggregator.Aggregate();
                aggregatedCaches.ConfigCache.Count().ShouldBe(1);
                aggregatedCaches.ResultsCache.Count().ShouldBe(1);

                // In case of accepted duplicate configs, first one wins.
                aggregatedCaches.ConfigCache.First().TargetNames.ShouldBeEquivalentTo(new []{"foo"});
                aggregatedCaches.ResultsCache.First().HasResultsForTarget("foo");
            }
            else
            {
                using var env = TestEnvironment.Create();

                env.SetEnvironmentVariable("MSBUILDDONOTLAUNCHDEBUGGER", "1");
                var e = Should.Throw<InternalErrorException>(() => aggregator.Aggregate());
                e.Message.ShouldContain(expectedErrorMessage);
            }
        }

        [Fact]
        public void SingleEmpty()
        {
            var configCache = new ConfigCache();

            var resultsCache = new ResultsCache();

            aggregator.Add(configCache, resultsCache);

            var results = aggregator.Aggregate();

            AssertAggregation(new[] {(configCache, resultsCache)}, results);
        }

        [Fact]
        public void SingleCacheWithSingleEntry()
        {
            var configCache = new ConfigCache();
            configCache.AddConfiguration(new BuildRequestConfiguration(configId: 1, new BuildRequestData("path", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"a", "b"}, null), "13"));

            var resultsCache = new ResultsCache();
            var buildResult = new BuildResult(new BuildRequest(1, 2, configurationId: 1, new List<string>(){"a", "b"}, null, BuildEventContext.Invalid, null));
            buildResult.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());
            resultsCache.AddResult(buildResult);

            aggregator.Add(configCache, resultsCache);

            var results = aggregator.Aggregate();

            AssertAggregation(new[] {(configCache, resultsCache)}, results);
        }

        [Fact]
        public void MultipleCachesMultipleEntries()
        {
            var configCache1 = new ConfigCache();
            configCache1.AddConfiguration(new BuildRequestConfiguration(configId: 1, new BuildRequestData("path", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"a", "b"}, null), "13"));
            configCache1.AddConfiguration(new BuildRequestConfiguration(configId: 2, new BuildRequestData("path2", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"c", "d"}, null), "13"));

            var resultsCache1 = new ResultsCache();
            var buildResult11 = new BuildResult(new BuildRequest(1, 2, configurationId: 1, new List<string>(){"a", "b"}, null, BuildEventContext.Invalid, null));
            buildResult11.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());
            var buildResult12 = new BuildResult(new BuildRequest(1, 2, configurationId: 2, new List<string>(){"c", "d"}, null, BuildEventContext.Invalid, null));
            buildResult12.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());
            resultsCache1.AddResult(buildResult11);
            resultsCache1.AddResult(buildResult12);

            var configCache2 = new ConfigCache();
            configCache2.AddConfiguration(new BuildRequestConfiguration(configId: 1, new BuildRequestData("path3", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"e", "f"}, null), "13"));
            configCache2.AddConfiguration(new BuildRequestConfiguration(configId: 2, new BuildRequestData("path4", new Dictionary<string, string>(){["p"] = "v"}, "13", new []{"g", "h"}, null), "13"));

            var resultsCache2 = new ResultsCache();
            var buildResult21 = new BuildResult(new BuildRequest(1, 2, configurationId: 1, new List<string>(){"e", "f"}, null, BuildEventContext.Invalid, null));
            buildResult21.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());
            var buildResult22 = new BuildResult(new BuildRequest(1, 2, configurationId: 2, new List<string>(){"g", "h"}, null, BuildEventContext.Invalid, null));
            buildResult22.AddResultsForTarget("a", GetNonEmptySucceedingTargetResult());
            resultsCache2.AddResult(buildResult21);
            resultsCache2.AddResult(buildResult22);

            aggregator.Add(configCache1, resultsCache1);
            aggregator.Add(configCache2, resultsCache2);

            var results = aggregator.Aggregate();

            AssertAggregation(new[] {(configCache1, resultsCache1), (configCache2, resultsCache2)}, results);
        }

        private void AssertAggregation((ConfigCache configCache, ResultsCache resultsCache)[] inputCaches, CacheAggregation aggregation)
        {
            var currentConfigurationIndex = 0;
            var currentBuildResultIndex = 0;

            var aggregatedConfigs = aggregation.ConfigCache.GetEnumerator().ToArray();

            var aggregatedResults = aggregation.ResultsCache.GetEnumerator().ToArray();

            foreach (var (configCache, resultsCache) in inputCaches)
            {
                foreach (var inputConfiguration in configCache)
                {
                    AssertConfigurationsEquivalent(inputConfiguration, aggregatedConfigs[currentConfigurationIndex]);
                    currentConfigurationIndex++;
                }

                foreach (var inputResult in resultsCache)
                {
                    AssertBuildResultsEquivalent(inputResult, aggregatedResults[currentBuildResultIndex]);
                    currentBuildResultIndex++;
                }
            }

            currentConfigurationIndex.ShouldBe(currentBuildResultIndex);

            aggregatedConfigs.Length.ShouldBe(currentBuildResultIndex);
            aggregatedResults.Length.ShouldBe(currentBuildResultIndex);

            aggregation.LastConfigurationId.ShouldBe(currentBuildResultIndex);
        }

        private void AssertBuildResultsEquivalent(BuildResult inputResult, BuildResult aggregatedBuildResult)
        {
            aggregatedBuildResult.ConfigurationId.ShouldNotBe(BuildRequestConfiguration.InvalidConfigurationId);

            aggregatedBuildResult.ParentGlobalRequestId.ShouldBe(BuildRequest.InvalidGlobalRequestId);
            aggregatedBuildResult.GlobalRequestId.ShouldBe(BuildRequest.InvalidGlobalRequestId);
            aggregatedBuildResult.NodeRequestId.ShouldBe(BuildRequest.InvalidNodeRequestId);
            aggregatedBuildResult.SubmissionId.ShouldBe(BuildEventContext.InvalidSubmissionId);

            inputResult.InitialTargets.ShouldBe(aggregatedBuildResult.InitialTargets);
            inputResult.DefaultTargets.ShouldBe(aggregatedBuildResult.DefaultTargets);
            inputResult.CircularDependency.ShouldBe(aggregatedBuildResult.CircularDependency);
            inputResult.Exception.ShouldBe(aggregatedBuildResult.Exception);
            inputResult.OverallResult.ShouldBe(aggregatedBuildResult.OverallResult);
            inputResult.ProjectStateAfterBuild.ShouldBe(aggregatedBuildResult.ProjectStateAfterBuild);

            Helpers.AssertDictionariesEqual(inputResult.ResultsByTarget, aggregatedBuildResult.ResultsByTarget, (a, b) =>
            {
                a.Key.ShouldBe(b.Key);

                a.Value.Exception.ShouldBe(b.Value.Exception);
                a.Value.Items.ShouldBe(b.Value.Items);
                a.Value.ResultCode.ShouldBe(b.Value.ResultCode);

                a.Value.WorkUnitResult.ActionCode.ShouldBe(b.Value.WorkUnitResult.ActionCode);
                a.Value.WorkUnitResult.Exception.ShouldBe(b.Value.WorkUnitResult.Exception);
                a.Value.WorkUnitResult.ResultCode.ShouldBe(b.Value.WorkUnitResult.ResultCode);
            });
        }

        private void AssertConfigurationsEquivalent(BuildRequestConfiguration inputConfiguration, BuildRequestConfiguration aggregatedConfig)
        {
            aggregatedConfig.ConfigurationId.ShouldNotBe(BuildRequestConfiguration.InvalidConfigurationId);

            aggregatedConfig.ResultsNodeId.ShouldBe(Scheduler.InvalidNodeId);

            var aggregatedConfigWithInitialId = aggregatedConfig.ShallowCloneWithNewId(inputConfiguration.ConfigurationId);

            inputConfiguration.ShouldBe(aggregatedConfigWithInitialId);
        }
    }
}
