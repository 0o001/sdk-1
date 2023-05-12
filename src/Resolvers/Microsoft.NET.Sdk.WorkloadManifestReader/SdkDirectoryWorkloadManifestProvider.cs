﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Workloads.Workload;
using Microsoft.NET.Sdk.Localization;

namespace Microsoft.NET.Sdk.WorkloadManifestReader
{
    public class SdkDirectoryWorkloadManifestProvider : IWorkloadManifestProvider
    {
        private readonly string _sdkRootPath;
        private readonly SdkFeatureBand _sdkVersionBand;
        private readonly string[] _manifestRoots;
        private static HashSet<string> _outdatedManifestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "microsoft.net.workload.android", "microsoft.net.workload.blazorwebassembly", "microsoft.net.workload.ios",
            "microsoft.net.workload.maccatalyst", "microsoft.net.workload.macos", "microsoft.net.workload.tvos", "microsoft.net.workload.mono.toolchain" };
        private readonly Dictionary<string, int>? _knownManifestIdsAndOrder;

        private readonly Dictionary<string, ManifestSpecifier> _requestedManifestVersions;

        public SdkDirectoryWorkloadManifestProvider(string sdkRootPath, string sdkVersion, string? userProfileDir, IEnumerable<ManifestSpecifier>? requestedManifestVersions = null)
            : this(sdkRootPath, sdkVersion, Environment.GetEnvironmentVariable, userProfileDir, requestedManifestVersions)
        {

        }

        internal SdkDirectoryWorkloadManifestProvider(string sdkRootPath, string sdkVersion, Func<string, string?> getEnvironmentVariable, string? userProfileDir, IEnumerable<ManifestSpecifier>? requestedManifestVersions = null)
        {
            if (string.IsNullOrWhiteSpace(sdkVersion))
            {
                throw new ArgumentException($"'{nameof(sdkVersion)}' cannot be null or whitespace", nameof(sdkVersion));
            }

            if (string.IsNullOrWhiteSpace(sdkRootPath))
            {
                throw new ArgumentException($"'{nameof(sdkRootPath)}' cannot be null or whitespace",
                    nameof(sdkRootPath));
            }

            _sdkRootPath = sdkRootPath;
            _sdkVersionBand = new SdkFeatureBand(sdkVersion);

            var knownManifestIdsFilePath = Path.Combine(_sdkRootPath, "sdk", sdkVersion, "KnownWorkloadManifests.txt");
            if (!File.Exists(knownManifestIdsFilePath))
            {
                knownManifestIdsFilePath = Path.Combine(_sdkRootPath, "sdk", sdkVersion, "IncludedWorkloadManifests.txt");
            }

            if (File.Exists(knownManifestIdsFilePath))
            {
                int lineNumber = 0;
                _knownManifestIdsAndOrder = new Dictionary<string, int>();
                foreach (var manifestId in File.ReadAllLines(knownManifestIdsFilePath).Where(l => !string.IsNullOrEmpty(l)))
                {
                    _knownManifestIdsAndOrder[manifestId] = lineNumber++;
                }
            }

            if (getEnvironmentVariable(EnvironmentVariableNames.WORKLOAD_MANIFEST_IGNORE_DEFAULT_ROOTS) == null)
            {
                string? userManifestsRoot = userProfileDir is null ? null : Path.Combine(userProfileDir, "sdk-manifests");
                string dotnetManifestRoot = Path.Combine(_sdkRootPath, "sdk-manifests");
                if (userManifestsRoot != null && WorkloadFileBasedInstall.IsUserLocal(_sdkRootPath, _sdkVersionBand.ToString()) && Directory.Exists(userManifestsRoot))
                {
                    _manifestRoots = new[] { userManifestsRoot, dotnetManifestRoot };
                }
                else
                {
                    _manifestRoots = new[] { dotnetManifestRoot };
                }
            }

            var manifestDirectoryEnvironmentVariable = getEnvironmentVariable(EnvironmentVariableNames.WORKLOAD_MANIFEST_ROOTS);
            if (manifestDirectoryEnvironmentVariable != null)
            {
                //  Append the SDK version band to each manifest root specified via the environment variable.  This allows the same
                //  environment variable settings to be shared by multiple SDKs.
                _manifestRoots = manifestDirectoryEnvironmentVariable.Split(Path.PathSeparator)
                                    .Concat(_manifestRoots ?? Array.Empty<string>()).ToArray();

            }

            _manifestRoots = _manifestRoots ?? Array.Empty<string>();

            _requestedManifestVersions = new Dictionary<string, ManifestSpecifier>(StringComparer.OrdinalIgnoreCase);

            if (requestedManifestVersions != null)
            {
                foreach (var manifestVersion in requestedManifestVersions)
                {
                    _requestedManifestVersions[manifestVersion.Id.ToString()] = manifestVersion;
                }
            }
        }

        public IEnumerable<ReadableWorkloadManifest> GetManifests()
        {
            foreach (var workloadManifestDirectory in GetManifestDirectories())
            {
                var workloadManifestPath = Path.Combine(workloadManifestDirectory, "WorkloadManifest.json");
                var id = Path.GetFileName(workloadManifestDirectory);

                yield return new(
                    id,
                    workloadManifestPath,
                    () => File.OpenRead(workloadManifestPath),
                    () => WorkloadManifestReader.TryOpenLocalizationCatalogForManifest(workloadManifestPath)
                );
            }
        }

        public IEnumerable<string> GetManifestDirectories()
        {
            //  Scan manifest directories
            //  TODO: Handle new format with subfolders for version
            var manifestIdsToDirectories = new Dictionary<string, string>();
            if (_manifestRoots.Length == 1)
            {
                //  Optimization for common case where test hook to add additional directories isn't being used
                var manifestDirectory = Path.Combine(_manifestRoots[0], _sdkVersionBand.ToString());
                if (Directory.Exists(manifestDirectory))
                {
                    foreach (var workloadManifestDirectory in Directory.EnumerateDirectories(manifestDirectory))
                    {
                        if (!IsManifestIdOutdated(workloadManifestDirectory))
                        {
                            manifestIdsToDirectories.Add(Path.GetFileName(workloadManifestDirectory), workloadManifestDirectory);
                        }
                    }
                }
            }
            else
            {
                //  If the same folder name is in multiple of the workload manifest directories, take the first one
                Dictionary<string, string> directoriesWithManifests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var manifestRoot in _manifestRoots.Reverse())
                {
                    var manifestDirectory = Path.Combine(manifestRoot, _sdkVersionBand.ToString());
                    if (Directory.Exists(manifestDirectory))
                    {
                        foreach (var workloadManifestDirectory in Directory.EnumerateDirectories(manifestDirectory))
                        {
                            directoriesWithManifests[Path.GetFileName(workloadManifestDirectory)] = workloadManifestDirectory;
                        }
                    }
                }

                foreach (var workloadManifestDirectory in directoriesWithManifests.Values)
                {
                    if (!IsManifestIdOutdated(workloadManifestDirectory))
                    {
                        manifestIdsToDirectories.Add(Path.GetFileName(workloadManifestDirectory), workloadManifestDirectory);
                    }
                }
            }

            //  Load manifests that were explicitly specified
            foreach (var kvp in _requestedManifestVersions)
            {
                manifestIdsToDirectories.Add(kvp.Key, GetManifestDirectoryFromSpecifier(kvp.Value));
            }

            if (_knownManifestIdsAndOrder != null && _knownManifestIdsAndOrder.Keys.Any(id => !manifestIdsToDirectories.ContainsKey(id)))
            {
                var missingManifestIds = _knownManifestIdsAndOrder.Keys.Where(id => !manifestIdsToDirectories.ContainsKey(id));
                foreach (var missingManifestId in missingManifestIds)
                {
                    var manifestDir = FallbackForMissingManifest(missingManifestId);
                    if (!string.IsNullOrEmpty(manifestDir))
                    {
                        manifestIdsToDirectories.Add(missingManifestId, manifestDir);
                    }
                }
            }

            //  Return manifests in a stable order.  Manifests in the KnownWorkloadManifests.txt file will be first, and in the same order they appear in that file.
            //  Then the rest of the manifests (if any) will be returned in (ordinal case-insensitive) alphabetical order.
            return manifestIdsToDirectories
                .OrderBy(kvp =>
                {
                    if (_knownManifestIdsAndOrder != null &&
                        _knownManifestIdsAndOrder.TryGetValue(kvp.Key, out var order))
                    {
                        return order;
                    }
                    return int.MaxValue;
                })
                .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => kvp.Value)
                .ToList();
        }

        private string FallbackForMissingManifest(string manifestId)
        {
            //  Only use the last manifest root (usually the dotnet folder itself) for fallback
            var sdkManifestPath = _manifestRoots.Last();
            if (!Directory.Exists(sdkManifestPath))
            {
                return string.Empty;
            }

            var candidateFeatureBands = Directory.GetDirectories(sdkManifestPath)
                .Select(dir => Path.GetFileName(dir))
                .Select(featureBand => new SdkFeatureBand(featureBand))
                .Where(featureBand => featureBand < _sdkVersionBand || _sdkVersionBand.ToStringWithoutPrerelease().Equals(featureBand.ToString(), StringComparison.Ordinal));
            var matchingManifestFatureBands = candidateFeatureBands
                .Where(featureBand => Directory.Exists(Path.Combine(sdkManifestPath, featureBand.ToString(), manifestId)));
            if (matchingManifestFatureBands.Any())
            {
                return Path.Combine(sdkManifestPath, matchingManifestFatureBands.Max()!.ToString(), manifestId);
            }
            else
            {
                // Manifest does not exist
                return string.Empty;
            }
        }

        private string GetManifestDirectoryFromSpecifier(ManifestSpecifier manifestSpecifier)
        {
            foreach (var manifestDirectory in _manifestRoots)
            {
                var specifiedManifestDirectory = Path.Combine(manifestDirectory, manifestSpecifier.FeatureBand.ToString(), manifestSpecifier.Id.ToString(),
                    manifestSpecifier.Version.ToString());
                if (File.Exists(Path.Combine(specifiedManifestDirectory, "WorkloadManifest.json")))
                {
                    return specifiedManifestDirectory;
                }
            }

            throw new FileNotFoundException(string.Format(Strings.SpecifiedManifestNotFound, manifestSpecifier.ToString()));
        }

        private bool IsManifestIdOutdated(string workloadManifestDir)
        {
            var manifestId = Path.GetFileName(workloadManifestDir);
            return _outdatedManifestIds.Contains(manifestId);
        }

        public string GetSdkFeatureBand()
        {
            return _sdkVersionBand.ToString();
        }
    }
}
