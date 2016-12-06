using System;
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
		private static log4net.ILog _log;

		public static DateTime StartTime { get; private set; }
		public static string ResultsDirectory { get; private set; }
		public static string InputSolutionFile { get; private set; }
		public static Dictionary<string, IList<string>> ParsedSolution { get; private set; }

		public static int TotalProjectsCompleted { get; set; }
		public static int TotalFilesCompleted { get; set; }
		public static int TotalDeadFilesFound { get; set; }

		public static int ProjectsToProcess { get; set; }
		public static int FilesToProcess { get; set; }

		static void Main(string[] args)
		{
			ResultsDirectory = Path.Combine(Directory.GetCurrentDirectory(), DateTime.Now.ToString("yyyy_MM_dd - HH_mm_ss"));
			Directory.CreateDirectory(ResultsDirectory);

			log4net.GlobalContext.Properties["LogName"] = Path.Combine(ResultsDirectory, "Maggot.log");
			_log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

			StartTime = DateTime.Now;

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

			_log.Info("Beginning analysis of " + InputSolutionFile);
			ParseSolution();
			_log.Info("=============================");
			_log.Info("Projects: " + ParsedSolution.Count);
			_log.Info("Implementation Files: " + ParsedSolution.SelectMany(x => x.Value).Count());
			_log.Info("=============================");

			_log.Info("Beginning debridement");
			var projectCounter = 1;
			foreach (var project in ParsedSolution)
			{
				_log.Info("-----------------------------");
				ProcessProject(project.Key, project.Value, projectCounter);
				projectCounter++;
			}
			_log.Info("-----------------------------");
			_log.Info("Debridement complete!");

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
			var projectDirectory = Path.GetDirectoryName(projectFile);

			try
			{
				var projectName = Path.GetFileNameWithoutExtension(projectFile);

				_log.InfoFormat("Processing " + projectFile + " ({0}/{1})", projectCounter, ProjectsToProcess);
				_log.Info("-----------------------------");

				_log.Info("Verifying solution will build before processing project");
				if (!BuildSolution(InputSolutionFile, projectName))
				{
					_log.Error("Solution is not in a buildable state - unable to perform debridement!");
					Console.WriteLine("Press any key to exit...");
					Console.ReadKey();
					return;
				}
				_log.Info("Solution built successfully");
				_log.Info("-----------------------------");

				var deadFiles = new List<string>();

				var fileCounter = 1;
				foreach (var implementationFile in implementationFiles)
				{
					_log.InfoFormat(implementationFile + " ({0}/{1})", fileCounter, implementationFiles.Count);

					DeleteContentsOfDirectory(projectDirectory);
					RevertChangesInDirectory(projectDirectory);

					RemoveReferenceToFile(projectFile, implementationFile);

					var builtSuccessfully = BuildSolution(InputSolutionFile, projectName);
					if (builtSuccessfully)
					{
						_log.Info("*** Build succeeded: Dead code identified! ***");
						deadFiles.Add(implementationFile);
					}

					fileCounter++;
				}

				// We have now finished processing this project
				// Revert any changes we have made so they don't interfere with the next project
				RevertChangesInDirectory(projectDirectory);

				if (deadFiles.Any())
				{
					_log.Debug("-----------------------------");
					_log.Debug($"Writing out {deadFiles.Count} files to DeadFileSummaries");

					var targetDirectory = Path.Combine(ResultsDirectory, "DeadFileSummaries");
					Directory.CreateDirectory(targetDirectory);
					File.WriteAllLines(Path.Combine(targetDirectory, Path.GetFileNameWithoutExtension(projectFile) + ".txt"), deadFiles);
					_log.Debug("Done.");
				}

				_log.Info("-----------------------------");
				TotalProjectsCompleted += 1;
				TotalFilesCompleted += implementationFiles.Count;
				TotalDeadFilesFound += deadFiles.Count;

				var totalTimeElapsed = DateTime.Now - StartTime;
				_log.InfoFormat("Total time elapsed: {0}", totalTimeElapsed.ToReadableString());

				var percentDeadFilesInProject = (deadFiles.Count / (double)implementationFiles.Count);
				_log.InfoFormat("{0} dead files identified in this project ({1:P2} of files in project)", deadFiles.Count, percentDeadFilesInProject);
				var percentDeadFilesSoFar = (TotalDeadFilesFound / (double)TotalFilesCompleted);
				_log.InfoFormat("{0} dead files identified so far ({1:P2} of files processed so far)", TotalDeadFilesFound, percentDeadFilesSoFar);
				var percentProjectsCompleted = (TotalProjectsCompleted / (double)ProjectsToProcess);
				_log.InfoFormat("{0} projects (of {1}) processed so far ({2:P2})", TotalProjectsCompleted, ProjectsToProcess, percentProjectsCompleted);
				var percentFilesCompleted = (TotalFilesCompleted / (double)FilesToProcess);
				_log.InfoFormat("{0} files (of {1}) processed so far ({2:P2})", TotalFilesCompleted, FilesToProcess, percentFilesCompleted);
			}
			catch (Exception ex)
			{
				// There seems to be a problem within this project so we just skip it and continue processing the remaining projects

				_log.Error("*****************************");
				_log.Error("An unexpected error occured when processing project " + projectFile);
				_log.Error(ex.Message);
				_log.Error("*****************************");

				// Revert any changes we have made so they don't interfere with the next project
				RevertChangesInDirectory(projectDirectory);
			}
		}

		private static void DeleteContentsOfDirectory(string directory)
		{
			_log.Debug("Deleting contents of " + directory);

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
			_log.Debug("Reverting changes in " + directory);

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
			_log.Debug("Removing reference to file from project...");

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
			_log.Debug("Beginning build");

			try
			{
				var buildLogsDirectory = Path.Combine(ResultsDirectory, "Build");
				Directory.CreateDirectory(buildLogsDirectory);
				var buildLogfileName = Path.Combine(buildLogsDirectory, $@"{projectName}.log");

				var arguments = new StringBuilder();
				arguments.Append("/p:Configuration=Debug ");
//				arguments.Append("/p:Platform=\"Mixed Platforms\" ");
//				arguments.Append("/p:SolutionDir=\"" + Path.GetDirectoryName(solutionFile) + "\\\\\" ");
				arguments.Append("/m ");

				arguments.Append("/filelogger ");
				arguments.Append("/fileloggerparameters:"
					+ "LogFile=\"" + buildLogfileName + "\";"
					+ "Verbosity=normal;"
					+ "Append");
				
				var msBuildProcess = new Process();
				msBuildProcess.StartInfo.FileName = @"C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe";
				msBuildProcess.StartInfo.Arguments = arguments + " \"" + solutionFile + "\"";

				msBuildProcess.Start();
				msBuildProcess.WaitForExit();

				if (msBuildProcess.ExitCode == 0)
				{
					_log.Debug("Build succeeded");
					return true;
				}
			}
			catch (Exception e)
			{
				_log.Error("Exception thrown: " + e);
			}

			_log.Debug("Build failed");

			return false;
		}
	}
}
