// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.NET.TestFramework
{
    public class RepoInfo
    {
        private static string s_repoRoot;

        private static string s_configuration;

        public static string RepoRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(s_repoRoot))
                {
                    return s_repoRoot;
                }

                string directory = GetBaseDirectory();

                while (!Directory.Exists(Path.Combine(directory, ".git")) && directory != null)
                {
                    directory = Directory.GetParent(directory).FullName;
                }

                if (directory == null)
                {
                    throw new DirectoryNotFoundException("Cannot find the git repository root");
                }

                s_repoRoot = directory;
                return s_repoRoot;
            }
        }

        private static string Configuration
        {
            get
            {
                if (string.IsNullOrEmpty(s_configuration))
                {
                    s_configuration = FindConfigurationInBasePath();
                }

                return s_configuration;
            }
        }

        public static string BinPath
        {
            get
            {
                return Path.Combine(RepoRoot, "bin");
            }
        }

        public static string PackagesPath
        {
            get { return Path.Combine(BinPath, Configuration, "Packages"); }
        }

        public static string NuGetCachePath
        {
            get { return Path.Combine(RepoRoot, "Packages"); }
        }


        public static string SdksPath
        {
            get { return Path.Combine(BinPath, Configuration, "Sdks"); }
        }

        public static string DotNetHostPath
        {
            get
            {
                return Path.Combine(RepoRoot, ".dotnet_cli", $"dotnet{Constants.ExeSuffix}");
            }
        }

        public static string NetCoreApp20Version { get; } = ReadNetCoreApp20Version();

        private static string ReadNetCoreApp20Version()
        {
            var dependencyVersionsPath = Path.Combine(RepoRoot, "build", "DependencyVersions.props");
            var root = XDocument.Load(dependencyVersionsPath).Root;
            var ns = root.Name.Namespace;

            var version = root
                .Elements(ns + "PropertyGroup")
                .Elements(ns + "MicrosoftNETCoreApp20Version")
                .FirstOrDefault()
                ?.Value;

            if (string.IsNullOrEmpty(version))
            {
                throw new InvalidOperationException($"Could not find a property named 'MicrosoftNETCoreApp20Version' in {dependencyVersionsPath}");
            }

            return version;
        }

        private static string FindConfigurationInBasePath()
        {
            // assumes tests are always executed from the "bin/$Configuration/Tests" directory
            return new DirectoryInfo(GetBaseDirectory()).Parent.Name;
        }

        private static string GetBaseDirectory()
        {
#if NET451
            string directory = AppDomain.CurrentDomain.BaseDirectory;
#else
            string directory = AppContext.BaseDirectory;
#endif

            return directory;
        }
    }
}