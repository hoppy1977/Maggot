using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using SharpSvn;

namespace Maggot
{
	class Program
	{
		private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
		public static string InputSolutionFile { get; private set; }
		public static Dictionary<string, IList<string>> ParsedSolution { get; private set; }

		public static int ProjectsCompleted { get; set; }
		public static int FilesCompleted { get; set; }

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

			Log.Info("Deleting old build log file");
			var buildLogfile = Path.Combine(Directory.GetCurrentDirectory() + @"\Logs\Build.log");
			File.Delete(buildLogfile);

			Log.Info("Beginning analysis of " + InputSolutionFile);
			ParseSolution();
			Log.Info("=============================");
			Log.Info("Projects: " + ParsedSolution.Count);
			Log.Info("Implementation Files: " + ParsedSolution.SelectMany(x => x.Value).Count());
			Log.Info("=============================");

			Log.Info("Beginning debridement");
			foreach (var project in ParsedSolution)
			{
				Log.Info("-----------------------------");
				ProcessProject(project.Key, project.Value);
			}
			Log.Info("-----------------------------");
			Log.Info("Debridement complete!");

			Console.WriteLine("Press any key to exit...");
			Console.ReadKey();
		}

		private static void ParseSolution()
		{
			ParsedSolution = new Dictionary<string, IList<string>>();

			var projectsInSolution = SolutionFile.Parse(InputSolutionFile).ProjectsInOrder
				.Where(x => x.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat);
			foreach (var projectInSolution in projectsInSolution)
			{
				var projectFile = projectInSolution.AbsolutePath;
				if (Path.GetExtension(projectFile) != ".vcxproj")
				{
					continue;
				}

				ParsedSolution.Add(projectFile, new List<string>());

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
								ParsedSolution[projectFile].Add(xAttribute.Value);
							}
						}
					}
				}
			}
		}

		private static void ProcessProject(string projectFile, IList<string> implementationFiles)
		{
			Log.Info("Processing " + projectFile);
			Log.Info("-----------------------------");

			Log.Info("Verifying solution will build before processing project");
			if (!BuildSolution(InputSolutionFile))
			{
				Log.Error("Solution is not in a buildable state - unable to perform debridement!");
				Console.WriteLine("Press any key to exit...");
				Console.ReadKey();
				return;
			}
			Log.Info("Solution built successfully");
			Log.Info("-----------------------------");

			var projectDirectory = Path.GetDirectoryName(projectFile);

			var deadFiles = new List<string>();

			foreach (var implementationFile in implementationFiles)
			{
				Log.Info(implementationFile);

				DeleteContentsOfDirectory(projectDirectory);
				RevertChangesInDirectory(projectDirectory);

				RemoveReferenceToFile(projectFile, implementationFile);

				var builtSuccessfully = BuildSolution(InputSolutionFile);
				if (builtSuccessfully)
				{
					Log.Info("Build suceeded: Dead code identified!");
					deadFiles.Add(implementationFile);
				}
			}

			// We have now finished processing this project
			// Revert any changes we have made so they don't interfere with the next project
			RevertChangesInDirectory(projectDirectory);

			if (deadFiles.Any())
			{
				var targetDirectory = Path.Combine(Directory.GetCurrentDirectory() + @"\DeadFileSummaries");
				Directory.CreateDirectory(targetDirectory);
				File.WriteAllLines(Path.Combine(targetDirectory, Path.GetFileNameWithoutExtension(projectFile) + ".txt"), deadFiles);
			}

			Log.Info("-----------------------------");
			ProjectsCompleted += 1;
			FilesCompleted += implementationFiles.Count;

			Log.InfoFormat("Dead files identified:                     {0}", deadFiles.Count);

			var totalProjects = ParsedSolution.Count;
			var percentProjectsCompleted = (ProjectsCompleted / (double)totalProjects);
			Log.InfoFormat("Percent complete (by project):             {0:P2}", percentProjectsCompleted);

			var totalFiles = ParsedSolution.SelectMany(x => x.Value).Count();
			var percentFilesCompleted = (FilesCompleted / (double)totalFiles);
			Log.InfoFormat("Percent complete (by implementation file): {0:P2}", percentFilesCompleted);
		}

		private static void DeleteContentsOfDirectory(string directory)
		{
			Log.Debug("Deleting contents of " + directory);

			var directoryInfo = new DirectoryInfo(directory);
			foreach (var file in directoryInfo.GetFiles())
			{
				file.Delete();
			}
			foreach (var subDirectory in directoryInfo.GetDirectories())
			{
				subDirectory.Delete(true);
			}
		}

		private static void RevertChangesInDirectory(string directory)
		{
			Log.Debug("Reverting changes in " + directory);

			using (var client = new SvnClient())
			{
				client.Revert(directory, new SvnRevertArgs
				{
					Depth = SvnDepth.Infinity,
				});
			}
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
		}

		private static bool BuildSolution(string solutionFile)
		{
			Log.Debug("Beginning build");

			try
			{
				var globalProperty = new Dictionary<string, string>();
				//globalProperty.Add("Configuration", "Debug");
				//globalProperty.Add("Platform", "x86");
				//globalProperty.Add("Platform", "Mixed Platforms");

				var pc = new ProjectCollection();
				var buildParameters = new BuildParameters(pc);
				buildParameters.Loggers = new[]
				{
					//new ConsoleLogger(),
					new FileLogger
					{
						Verbosity = LoggerVerbosity.Normal,
						Parameters = @"LogFile=Logs\Build.log;Append=true",
					},
				};

				var buildRequest = new BuildRequestData(solutionFile, globalProperty, null, new string[] { "Build" }, null);
				var buildResult = BuildManager.DefaultBuildManager.Build(buildParameters, buildRequest);
				if (buildResult.OverallResult == BuildResultCode.Success)
				{
					Log.Debug("Build succeeded");
					return true;
				}
			}
			catch (Exception e)
			{
				Log.Error("Exception thrown: " + e);
			}

			Log.Debug("Build failed");

			return false;
		}
	}
}
