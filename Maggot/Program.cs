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
							var absFileName = Path.Combine(projectDirectory, xAttribute.Value);
							absFileName = Path.GetFullPath(absFileName);

							filesToProcess.Add(absFileName);
						}
					}
				}
			}

			Log.Debug(filesToProcess.Count + " implementation files to process");

			// Ok, we have built up a list of files in the solution
			// Now we process each one
			foreach (var implementationFile in filesToProcess)
			{
				Log.Info(implementationFile);

				CleanDirectory(projectDirectory);

				RemoveReferenceToFile(projectFile, implementationFile);

				var builtSuccessfully = BuildSolution(InputSolutionFile);
				if (builtSuccessfully)
				{
					Log.Info("Build suceeded: Dead code identified!");
				}
			}
		}

		private static void CleanDirectory(string directory)
		{
			Log.Debug("Cleaning " + directory + "...");

			using (var client = new SvnClient())
			{
				//client.Revert()
				//					client.CleanUp(projectDirectory, new SvnCleanUpArgs
				//{

				//});
			}
		}

		private static void RemoveReferenceToFile(string projectFile, string fileName)
		{
			Log.Debug("Removing reference to file from project...");

			// TODO:
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
