// SPDX-License-Identifier: MPL-2.0

using System.Reflection;
using System.Runtime.InteropServices;
using Mono.Addins;

[assembly: AssemblyTitle("NexVerse.RegionModules")]
[assembly: AssemblyDescription("OpenGenesisLINK simulator region modules")]
[assembly: AssemblyCompany("OpenGenesisLINK")]
[assembly: AssemblyProduct("OpenGenesisLINK")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion(OpenSim.VersionInfo.AssemblyVersionNumber)]

[assembly: Addin("NexVerse.RegionModules", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
