﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Microsoft.MixedReality.Toolkit.Utilities.Editor
{
    /// <summary>
    /// Class that enables MRTK to add scoped registries and/or packages to the Unity Package Mangager manifest
    /// </summary>
    internal static class PackageManifestUpdater
    {
        private static string MSBuildRegistryUrl = "https://pkgs.dev.azure.com/UnityDeveloperTools/MSBuildForUnity/_packaging/UnityDeveloperTools/npm/registry";
        private static string MSBuildRegistryName = "MS Build for Unity";
        private static string[] MSBuildRegistryScopes = new string[] { "com.microsoft" };

        internal const string MSBuildPackageName = "com.microsoft.msbuildforunity";
        internal const string MSBuildPackageVersion = "0.9.1";

        /// <summary>
        /// Finds and returns the fully qualified path to the Unity Package Manager manifest
        /// </summary>
        /// <returns>The path to the manifest file, or null.</returns>
        private static string GetPackageManifestFilePath()
        {
            // Locate the full path to the package manifest.
            DirectoryInfo projectRoot = new DirectoryInfo(Application.dataPath).Parent;
            string[] paths = { projectRoot.FullName, "Packages", "manifest.json" };
            string manifestPath = Path.Combine(paths);

            // Verify that the package manifest file exists.
            if (!File.Exists(manifestPath))
            {
                Debug.LogError($"Package manifest file ({manifestPath}) could not be found.");
                return null;
            }

            return manifestPath;
        }

        /// <summary>
        /// Reports whether or not the appropriate version of MSBuild for Unity is specified
        /// in the Unity Package Manager manifest.
        /// </summary>
        /// <returns>
        /// True if an appropriate verson of MS Build for Unity is configured in the manifest, otherwise false.
        /// </returns>
        internal static bool IsMSBuildForUnityEnabled()
        {
            string manifestPath = GetPackageManifestFilePath();
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                return false;
            }

            // Load the manifest file.
            string manifestFileContents = File.ReadAllText(manifestPath);
            if (string.IsNullOrWhiteSpace(manifestFileContents))
            {
                return false;
            }

            // Read the package manifest a line at a time.
            bool msBuildFound = false;
            bool isAppropriateVersion = false;
            Version minVersion = Version.Parse(MSBuildPackageVersion);
            using (FileStream manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read))
            {
                using (StreamReader reader = new StreamReader(manifestStream))
                {
                    // Read the manifest file a line at a time.
                    while (!reader.EndOfStream)
                    {
                        string line = reader.ReadLine();
                        if (line.Contains(MSBuildPackageName))
                        {
                            msBuildFound = true;

                            // Next, check the version.
                            string[] splitLine = line.Split(new char[] { ':' });
                            if (splitLine.Length == 2)
                            {
                                // Ensure correct formatting of the version string, before we attempt to parse it.
                                string versionString = splitLine[1].Trim(new char[] { ' ', '\"', ',' });
                                bool replaceOnEquals = false;
                                if (versionString.Contains("-"))
                                {
                                    // The string references a preview version. Truncate at the '-'.
                                    versionString = versionString.Substring(0, versionString.IndexOf('-'));
                                    
                                    // We want to update preview versions to the final.
                                    replaceOnEquals = true;
                                }

                                Version version;
                                if (Version.TryParse(versionString, out version))
                                {
                                    isAppropriateVersion = replaceOnEquals ? (version > minVersion) : (version >= minVersion);
                                }
                            }

                            break;
                        }
                    }
                }
            }

            return (msBuildFound && isAppropriateVersion);
        }

        /// <summary>
        /// Ensures the required settings exist in the package manager to allow for using MSBuild for Unity.
        /// </summary>
        internal static void EnsureMSBuildForUnity()
        {
            PackageManifest manifest = null;

            string manifestPath = GetPackageManifestFilePath();
            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                return;
            }

            // Read the package manifest into a list of strings (for easy finding of entries)
            // and then deserialize.
            List<string> manifestFileLines = new List<string>();
            using (FileStream manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read))
            {
                using (StreamReader reader = new StreamReader(manifestStream))
                {
                    // Read the manifest file a line at a time.
                    while (!reader.EndOfStream)
                    {
                        string line = reader.ReadLine();
                        manifestFileLines.Add(line);
                    }

                    // Go back to the start of the file.
                    manifestStream.Seek(0, 0);

                    // Deserialize the scoped registries portion of the package manifest.
                    manifest = JsonUtility.FromJson<PackageManifest>(reader.ReadToEnd());
                }
            }

            if (manifest == null)
            {
                Debug.LogError($"Failed to read the package manifest file ({manifestPath})");
                return;
            }

            // Ensure that pre-exising scoped registries are retained.
            List<ScopedRegistry> scopedRegistries = new List<ScopedRegistry>();
            if ((manifest.scopedRegistries != null) && (manifest.scopedRegistries.Length > 0))
            {
                scopedRegistries.AddRange(manifest.scopedRegistries);
            }

            // Attempt to find an entry in the scoped regstries collection for the MSBuild for Unity url
            bool needToAddRegistry = true;
            foreach (ScopedRegistry registry in scopedRegistries)
            {
                if (registry.url == MSBuildRegistryUrl)
                {
                    needToAddRegistry = false;
                }
            }

            // If no entry was found, add one.
            if (needToAddRegistry)
            {
                ScopedRegistry registry = new ScopedRegistry();
                registry.name = MSBuildRegistryName;
                registry.url = MSBuildRegistryUrl;
                registry.scopes = MSBuildRegistryScopes;

                scopedRegistries.Add(registry);
            }

            // Update the manifest's scoped registries, as the collection may have been modified.
            manifest.scopedRegistries = scopedRegistries.ToArray();

            int dependenciesStartIndex = -1;
            int scopedRegistriesStartIndex = -1;
            int scopedRegistriesEndIndex = -1;
            int packageLine = -1;

            // Attempt to find the MSBuild for Unity package entry in the dependencies collection
            // This loop also identifies the dependecies collection line and the start / end of a
            // pre-existing scoped registries collections
            bool addPackage = true;
            for (int i = 0; i < manifestFileLines.Count; i++)
            {
                if (manifestFileLines[i].Contains("\"scopedRegistries\":"))
                {
                    scopedRegistriesStartIndex = i;
                }
                if (manifestFileLines[i].Contains("],") && (scopedRegistriesStartIndex != -1) && (scopedRegistriesEndIndex == -1))
                {
                    scopedRegistriesEndIndex = i;
                }
                if (manifestFileLines[i].Contains("\"dependencies\": {"))
                {
                    dependenciesStartIndex = i;
                }
                if (manifestFileLines[i].Contains(MSBuildPackageName))
                {
                    packageLine = i;
                    addPackage = false;
                }
            }

            // If no package was found add it to the dependencies collection.
            if (addPackage)
            {
                // Add the package to the collection (pad the entry with four spaces)
                manifestFileLines.Insert(dependenciesStartIndex + 1, $"    \"{MSBuildPackageName}\": \"{MSBuildPackageVersion}\",");
            }
            else
            {
                // Replace the line that currently exists
                manifestFileLines[packageLine] = $"    \"{MSBuildPackageName}\": \"{MSBuildPackageVersion}\",";
            }

            // Update the manifest file.
            // First, serialize the scoped registry collection.
            string serializedRegistriesJson = JsonUtility.ToJson(manifest, true);

            // Ensure that the file is truncated to ensure it is always valid after writing.
            using (FileStream outFile = new FileStream(manifestPath, FileMode.Truncate, FileAccess.Write))
            {
                using (StreamWriter writer = new StreamWriter(outFile))
                {
                    bool scopedRegistriesWritten = false;

                    // Write each line of the manifest back to the file.
                    for (int i = 0; i < manifestFileLines.Count; i++)
                    {
                        if ((i >= scopedRegistriesStartIndex) && (i <= scopedRegistriesEndIndex))
                        {
                            // Skip these lines, they will be replaced.
                            continue;
                        }

                        if (!scopedRegistriesWritten && (i > 0))
                        {
                            // Trim the leading '{' and '\n' from the serialized scoped registries
                            serializedRegistriesJson = serializedRegistriesJson.Remove(0, 2);
                            // Trim, the trailing '\n' and '}'
                            serializedRegistriesJson = serializedRegistriesJson.Remove(serializedRegistriesJson.Length - 2);
                            // Append a trailing ',' to close the scopedRegistries node
                            serializedRegistriesJson = serializedRegistriesJson.Insert(serializedRegistriesJson.Length, ",");
                            writer.WriteLine(serializedRegistriesJson);

                            scopedRegistriesWritten = true;
                        }

                        writer.WriteLine(manifestFileLines[i]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// This class encapsulates the portion of the package manifest
    /// file format that is of greatest interest to the MRTK.
    /// </summary>
    [Serializable]
    internal class PackageManifest
    {
        public ScopedRegistry[] scopedRegistries = null;
    }

    /// <summary>
    /// This class defines a scoped registry, per the manifest file format.
    /// </summary>
    [Serializable]
    internal class ScopedRegistry
    {
        public string name = null;
        public string url = null;
        public string[] scopes = null;
    }
}
