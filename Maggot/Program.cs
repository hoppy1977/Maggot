using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using SharpSvn;

namespace Maggot
{
	class Program
	{
		private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
		public static string InputSolutionFile { get; private set; }

		static void Main(string[] args)
		{
			if (args.Length != 1)
			{
				Console.WriteLine("Syntax is 'maggot <solution_to_process>'.");
				Console.WriteLine("Press any key to exit...");
				Console.ReadKey();
				return;
			}

			InputSolutionFile = args[0];

			if (!File.Exists(InputSolutionFile))
			{
				Console.WriteLine("Specified solution file does not exist.");
				Console.WriteLine("Press any key to exit...");
				Console.ReadKey();
				return;
			}

			Log.Info("Beginning debridement of " + InputSolutionFile + "...");

			var projectsInSolution = SolutionFile.Parse(InputSolutionFile).ProjectsInOrder;
			Log.Info(projectsInSolution.Count + " projects to process");

			foreach (var projectInSolution in projectsInSolution)
			{
				var projectFile = projectInSolution.AbsolutePath;
				ProcessProject(projectFile);
			}

			Log.Info("Debridement complete!");

			Console.WriteLine("Press any key to exit...");
			Console.ReadKey();
		}

		private static void ProcessProject(string projectFile)
		{
			Log.Info("Processing " + projectFile + "...");

			var projectDirectory = Path.GetDirectoryName(projectFile);
			Debug.Assert(projectDirectory != null);

			Log.Debug("Building list of files in project...");
			var filesToProcess = new List<string>();

			// Now get a list of the implementation files referenced from this project
			var doc = XDocument.Load(projectFile);
			var ns = doc.Root?.Name.Namespace;
			var itemGroups = doc.Root?.Elements(ns + "ItemGroup").ToList();

			if (itemGroups != null)
			{
				var fileItems = itemGroups.Elements(ns + "ClCompile");
				if (fileItems != null)
				{
					foreach (var item in fileItems)
					{
						var xAttribute = item.Attribute("Include");
						if (xAttribute != null)
						{
							filesToProcess.Add(xAttribute.Value);
						}
					}
				}
			}

			Log.Info(filesToProcess.Count + " implementation files to process");

			// Ok, we have built up a list of files in the solution
			// Now we process each one
			var deadFiles = new List<string>();
			foreach (var implementationFile in filesToProcess)
			{
				Log.Info(implementationFile);

				RefreshDirectory(projectDirectory);

				RemoveReferenceToFile(projectFile, implementationFile);

				var builtSuccessfully = BuildSolution(InputSolutionFile);
				if (builtSuccessfully)
				{
					Log.Info("Build suceeded: Dead code identified!");
					deadFiles.Add(implementationFile);
				}
			}

			if (deadFiles.Any())
			{
				var targetDirectory = Path.Combine(Directory.GetCurrentDirectory() + @"\DeadFiles");
				Directory.CreateDirectory(targetDirectory);
				File.WriteAllLines(Path.Combine(targetDirectory, Path.GetFileNameWithoutExtension(projectFile) + ".txt"), deadFiles);
			}
		}

		private static void RefreshDirectory(string directory)
		{
			Log.Debug("Refreshing contents of " + directory + "...");

			Log.Debug("Deleting contents of folder");
			var directoryInfo = new DirectoryInfo(directory);
			foreach (var file in directoryInfo.GetFiles())
			{
				file.Delete();
			}
			foreach (var subDirectory in directoryInfo.GetDirectories())
			{
				subDirectory.Delete(true);
			}

			using (var client = new SvnClient())
			{
				Log.Debug("Reverting all changes to folder");
				client.Revert(directory, new SvnRevertArgs
				{
					Depth = SvnDepth.Infinity,
				});
			}

			Log.Debug("Finished refreshing directory " + directory);
		}

		private static void RemoveReferenceToFile(string projectFile, string fileName)
		{
			Log.Debug("Removing reference to file from project...");

			var doc = XDocument.Load(projectFile);
			var ns = doc.Root?.Name.Namespace;
			var itemGroups = doc.Root?.Elements(ns + "ItemGroup").ToList();

			if (itemGroups != null)
			{
				var fileItems = itemGroups.Elements(ns + "ClCompile");
				if (fileItems != null)
				{
					foreach (var item in fileItems)
					{
						var xAttribute = item.Attribute("Include");
						if (xAttribute != null)
						{
							if (xAttribute.Value == fileName)
							{
								item.Remove();
								doc.Save(projectFile);
								break;
							}
						}
					}
				}
			}

			Log.Debug("Finished removing reference to file from project...");
		}

		private static bool BuildSolution(string solutionFile)
		{
			Log.Debug("Beginning build");

			try
			{
				var pc = new ProjectCollection();

				var globalProperty = new Dictionary<string, string>();
				globalProperty.Add("Configuration", "Debug");
				globalProperty.Add("Platform", "x86");

				var buildRequest = new BuildRequestData(solutionFile, globalProperty, null, new string[] { "Build" }, null);
				var buildResult = BuildManager.DefaultBuildManager.Build(new BuildParameters(pc), buildRequest);
				if (buildResult.OverallResult == BuildResultCode.Success)
				{
					Log.Debug("Build succeeded");
					return true;
				}
			}
			catch (Exception e)
			{
				Log.Debug("Exception thrown: " + e);
			}

			Log.Debug("Build failed");

			return false;
		}
	}
}
