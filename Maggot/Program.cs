﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Construction;
using SharpSvn;

namespace Maggot
{
	class Program
	{
		private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
		public static string InputSolutionFile { get; private set; }
		public static Dictionary<string, IList<string>> ParsedSolution { get; private set; }

		public static int TotalProjectsCompleted { get; set; }
		public static int TotalFilesCompleted { get; set; }
		public static int TotalDeadFilesFound { get; set; }

		public static int ProjectsToProcess { get; set; }
		public static int FilesToProcess { get; set; }

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
			var projectCounter = 1;
			foreach (var project in ParsedSolution)
			{
				Log.Info("-----------------------------");
				ProcessProject(project.Key, project.Value, projectCounter);
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

			ProjectsToProcess = ParsedSolution.Count;
			FilesToProcess = ParsedSolution.SelectMany(x => x.Value).Count();
		}

		private static void ProcessProject(string projectFile, IList<string> implementationFiles, int projectCounter)
		{
			Log.InfoFormat("Processing " + projectFile + " ({0}/{1})", projectCounter, ProjectsToProcess);
			Log.Info("-----------------------------");

			Log.Info("Verifying solution will build before processing project");
			if (!BuildSolution(InputSolutionFile, "Initial build"))
			{
				Log.Error("Solution is not in a buildable state - unable to perform debridement!");
				Console.WriteLine("Press any key to exit...");
				Console.ReadKey();
				return;
			}
			Log.Info("Solution built successfully");
			Log.Info("-----------------------------");

			var projectDirectory = Path.GetDirectoryName(projectFile);
			var projectName = Path.GetFileNameWithoutExtension(projectFile);

			var deadFiles = new List<string>();

			var fileCounter = 1;
			foreach (var implementationFile in implementationFiles)
			{
				Log.InfoFormat(implementationFile + " ({0}/{1})", fileCounter, implementationFiles.Count);

				DeleteContentsOfDirectory(projectDirectory);
				RevertChangesInDirectory(projectDirectory);

				RemoveReferenceToFile(projectFile, implementationFile);

				var builtSuccessfully = BuildSolution(InputSolutionFile, projectName);
				if (builtSuccessfully)
				{
					Log.Info("Build succeeded: Dead code identified!");
					deadFiles.Add(implementationFile);
				}

				fileCounter++;
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
			TotalProjectsCompleted += 1;
			TotalFilesCompleted += implementationFiles.Count;
			TotalDeadFilesFound += deadFiles.Count;

			var percentDeadFilesInProject = (deadFiles.Count / (double)implementationFiles.Count);
			Log.InfoFormat("{0} dead files identified in this project ({1:P2} of files)", deadFiles.Count, percentDeadFilesInProject);
			var percentDeadFilesSoFar = (TotalDeadFilesFound / (double)TotalFilesCompleted);
			Log.InfoFormat("{0} dead files identified so far ({1:P2} of files)", TotalDeadFilesFound, percentDeadFilesSoFar);
			var percentProjectsCompleted = (TotalProjectsCompleted / (double)ProjectsToProcess);
			Log.InfoFormat("{0} projects (of {1}) processed so far ({2:P2})", TotalProjectsCompleted, ProjectsToProcess, percentProjectsCompleted);
			var percentFilesCompleted = (TotalFilesCompleted / (double)FilesToProcess);
			Log.InfoFormat("{0} files (of {1}) processed so far ({2:P2})", TotalFilesCompleted, FilesToProcess, percentFilesCompleted);
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

		private static bool BuildSolution(string solutionFile, string projectName)
		{
			Log.Debug("Beginning build");

			try
			{
				var logFileDirectory = Path.Combine(Directory.GetCurrentDirectory() + @"\Logs");
				Directory.CreateDirectory(logFileDirectory);
				var buildLogfileName = Path.Combine(logFileDirectory + $"\\Build - {projectName}.log");

				var arguments = new StringBuilder();
//				arguments.Append("/p:Configuration=Release ");
//				arguments.Append("/p:Platform=\"Mixed Platforms\" ");
				arguments.Append("/p:SolutionDir=\"" + Path.GetDirectoryName(solutionFile) + "\\\\\" ");
				arguments.Append("/m ");

				arguments.Append("/filelogger ");
				arguments.Append("/fileloggerparameters:"
					+ "LogFile=\"" + buildLogfileName + "\";"
					+ "Append");
				
				var msBuildProcess = new Process();
				msBuildProcess.StartInfo.FileName = @"C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe";
				msBuildProcess.StartInfo.Arguments = arguments + " \"" + solutionFile + "\"";

				msBuildProcess.Start();
				msBuildProcess.WaitForExit();

				if (msBuildProcess.ExitCode == 0)
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
